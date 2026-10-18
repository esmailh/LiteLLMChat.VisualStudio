using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

/// <summary>The query a browser delivers to the loopback callback. An OAuth error is a legitimate end of the flow and
/// is kept beside the code, so the caller can say why the sign-in stopped instead of reporting a timeout.</summary>
public sealed class McpOAuthCallback
{
    public string? Code { get; set; }
    public string? State { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(Code);

    public static McpOAuthCallback FromQuery(string? query)
    {
        var values = ParseQuery(query);
        values.TryGetValue("code", out var code);
        values.TryGetValue("state", out var state);
        values.TryGetValue("error", out var error);
        values.TryGetValue("error_description", out var description);
        return new McpOAuthCallback
        {
            Code = string.IsNullOrEmpty(code) ? null : code,
            State = string.IsNullOrEmpty(state) ? null : state,
            Error = string.IsNullOrEmpty(error) ? null : error,
            ErrorDescription = string.IsNullOrEmpty(description) ? null : description
        };
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (var pair in query!.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            var name = Decode(equals < 0 ? pair : pair.Substring(0, equals));
            var value = equals < 0 ? "" : Decode(pair.Substring(equals + 1));
            if (name.Length > 0) result[name] = value;
        }
        return result;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }
}

/// <summary>The loopback receiver for an authorization-code sign-in. A raw <see cref="TcpListener"/> is used rather
/// than <c>HttpListener</c> because the latter needs a URL reservation (or elevation) on Windows, and a developer
/// extension must not ask for one.
///
/// Only one callback ends the wait. Any other request the browser makes — a favicon, a preflight — is answered and
/// ignored; a callback whose <c>state</c> does not match is answered with an error and also ignored, because a
/// stray request must not be able to end the flow the user is still completing.</summary>
public sealed class McpLoopback : IDisposable
{
    public const string CallbackPath = "/callback";

    private readonly TcpListener _listener;
    private volatile bool _disposed;

    public McpLoopback()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => "http://127.0.0.1:" + Port + CallbackPath;

    /// <summary>Waits for the browser's redirect. The listener is stopped on timeout, so a wait that failed cannot
    /// be resumed by a second call: the caller starts a new flow, which is what the user expects after retrying.</summary>
    public async Task<McpOAuthCallback> WaitAsync(string expectedState, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpLoopback));
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw TimedOut(timeout);

            var accept = _listener.AcceptTcpClientAsync();
            var delay = Task.Delay(remaining, cancellationToken);
            var winner = await Task.WhenAny(accept, delay).ConfigureAwait(false);
            if (winner != accept)
            {
                Stop(accept);
                cancellationToken.ThrowIfCancellationRequested();
                throw TimedOut(timeout);
            }

            using (var client = await accept.ConfigureAwait(false))
            {
                McpOAuthCallback? callback;
                try { callback = await ServeAsync(client, expectedState, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                {
                    // The browser closed before it read the answer. The request itself may still have been the
                    // callback, but nothing proves it, so the wait continues.
                    continue;
                }
                if (callback != null) return callback;
            }
        }
    }

    private async Task<McpOAuthCallback?> ServeAsync(TcpClient client, string expectedState,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
        var uri = ParseTarget(target);
        if (uri == null || !uri.AbsolutePath.Equals(CallbackPath, StringComparison.Ordinal))
        {
            await RespondAsync(stream, 404, "Not found",
                "This address only answers the sign-in callback.").ConfigureAwait(false);
            return null;
        }

        var callback = McpOAuthCallback.FromQuery(uri.Query);
        if (!string.Equals(callback.State, expectedState, StringComparison.Ordinal))
        {
            await RespondAsync(stream, 400, "Sign-in rejected",
                "The sign-in response did not belong to this request. Return to Visual Studio and start again.")
                .ConfigureAwait(false);
            return null;
        }

        if (callback.Error != null)
        {
            await RespondAsync(stream, 200, "Sign-in not completed",
                "The authorization server reported: " + callback.Error + ".").ConfigureAwait(false);
            return callback;
        }

        await RespondAsync(stream, 200, "Signed in",
            "Visual Studio now has access. You can close this tab.").ConfigureAwait(false);
        return callback;
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var text = new StringBuilder();
        while (text.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            var end = text.ToString().IndexOf('\n');
            if (end < 0) continue;
            var line = text.ToString(0, end).TrimEnd('\r');
            var parts = line.Split(' ');
            return parts.Length >= 2 && parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        }
        return null;
    }

    private static Uri? ParseTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var text = target!.StartsWith("/", StringComparison.Ordinal) ? "http://127.0.0.1" + target : target;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string message)
    {
        var body = "<!doctype html><meta charset=\"utf-8\"><title>" + title + "</title><p>" + message + "</p>";
        var bytes = Encoding.UTF8.GetBytes(
            "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n" +
            "Connection: close\r\n\r\n" + body);
        try { await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static string Reason(int status) => status == 200 ? "OK" : status == 400 ? "Bad Request" : "Not Found";

    private static McpException TimedOut(TimeSpan timeout) => new McpException(
        "The sign-in did not finish within " + Math.Max(1, (int)Math.Round(timeout.TotalMinutes)) +
        " minute(s). Nothing was changed; start it again from the MCP dashboard.", authRequired: true);

    /// <summary>Stops the listener and leaves the abandoned accept task to observe <see cref="ObjectDisposedException"/>
    /// on its own; awaiting it here would block the timeout's own throw.</summary>
    private void Stop(Task<TcpClient> accept)
    {
        try { _listener.Stop(); } catch (SocketException) { }
        _ = accept.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch (SocketException) { }
    }
}
