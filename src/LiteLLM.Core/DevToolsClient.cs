using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>A protocol call the browser refused, or a connection that ended under one. Its message is the sentence the
/// model reads, so it is written where the failure is known rather than where it is caught.</summary>
public sealed class CdpException : Exception
{
    public CdpException(string message) : base(message) { }
}

/// <summary>One attached page. Requests are numbered and answered by number, which is the whole of the correlation the
/// protocol needs; the events that arrive beside the answers are ignored, because everything this tool waits for — a
/// page that has loaded, an element that exists — is asked for and read back rather than watched for.</summary>
public interface ICdpConnection : IDisposable
{
    Task<JObject> CallAsync(string method, JObject? parameters, CancellationToken token);
}

/// <summary>The three plain requests the DevTools endpoint answers without a socket, and the socket itself. This is the
/// seam the session is tested through: a fake answers with the JSON a browser would, and every decision about which
/// page to drive stays checkable without a browser window.</summary>
public interface IDevTools
{
    /// <summary><c>/json/version</c>: the browser's own socket path and version, and what says the endpoint is alive.</summary>
    Task<JObject?> ProbeAsync(int port, CancellationToken token);

    /// <summary><c>/json/list</c>: every target the browser will attach to.</summary>
    Task<JArray?> ListAsync(int port, CancellationToken token);

    /// <summary><c>/json/new</c>: one more tab, for a profile whose last window was closed.</summary>
    Task<DevToolsTarget?> NewTargetAsync(int port, string url, CancellationToken token);

    ICdpConnection Connect(string webSocketUrl);
}

/// <summary>The real endpoint: three GETs and a socket, over loopback, with short timeouts. Every failure is answered
/// as "not there" rather than thrown, because each of them — a stale port file, a browser that has not finished
/// starting, a profile another window owns — is an ordinary state the session has an answer for.</summary>
public sealed class HttpDevTools : IDevTools
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<JObject?> ProbeAsync(int port, CancellationToken token) =>
        await GetAsync(DevToolsEndpoint.VersionUrl(port), token).ConfigureAwait(false) as JObject;

    public async Task<JArray?> ListAsync(int port, CancellationToken token) =>
        await GetAsync(DevToolsEndpoint.ListUrl(port), token).ConfigureAwait(false) as JArray;

    public async Task<DevToolsTarget?> NewTargetAsync(int port, string url, CancellationToken token)
    {
        try
        {
            // Newer Chromium refuses GET on this endpoint because a page can be made to issue one; the method is the
            // difference between a tab and a request forged by any page the browser happens to have open.
            using (var request = new HttpRequestMessage(HttpMethod.Put, DevToolsEndpoint.NewTargetUrl(port, url)))
            using (var response = await Http.SendAsync(request, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!(JToken.Parse(text) is JObject entry)) return null;
                return DevToolsEndpoint.ParseTargets(new JArray(entry)).FirstOrDefault();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public ICdpConnection Connect(string webSocketUrl) => new CdpConnection(webSocketUrl);

    private static async Task<JToken?> GetAsync(string url, CancellationToken token)
    {
        try
        {
            using (var response = await Http.GetAsync(url, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JToken.Parse(text);
            }
        }
        catch (Exception)
        {
            // Not running, still starting, or the port now belongs to something else: all three mean "ask again".
            return null;
        }
    }
}

/// <summary>The socket to one page.</summary>
public sealed class CdpConnection : ICdpConnection
{
    private readonly string _url;
    private readonly ClientWebSocket _socket = new ClientWebSocket();
    private readonly SemaphoreSlim _connect = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JObject>> _pending = new Dictionary<int, TaskCompletionSource<JObject>>();
    private int _next;
    private bool _disposed;

    public CdpConnection(string webSocketUrl) => _url = webSocketUrl;

    public async Task<JObject> CallAsync(string method, JObject? parameters, CancellationToken token)
    {
        await EnsureConnectedAsync(token).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _next);
        var source = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = source;
        try
        {
            var message = new JObject { ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            await _send.WaitAsync(token).ConfigureAwait(false);
            try { await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { _send.Release(); }
            // The registration is what keeps a cancelled turn from waiting for an answer nobody is listening to; the
            // pending entry is dropped in the finally below, so a late answer is ignored rather than delivered.
            using (token.Register(() => source.TrySetCanceled(token)))
                return await source.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_socket.State == WebSocketState.Open) return;
        await _connect.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open) return;
            if (_disposed) throw new CdpException("The connection to the browser was closed, so nothing was sent.");
            try
            {
                await _socket.ConnectAsync(new Uri(_url), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CdpException("The browser's debugging endpoint refused the connection, so the page could not be driven: " + ex.Message);
            }
            // The task is not kept: the socket's own receive operation roots it, and it ends when the socket does.
            _ = Task.Run(ReceiveAsync);
        }
        finally
        {
            _connect.Release();
        }
    }

    /// <summary>Reads until the socket ends. A message is assembled from its frames because a page's text arrives in
    /// several, and the loop must keep running while no call is waiting: an unread socket fills, and the next call then
    /// hangs on a browser that is only trying to answer.</summary>
    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new System.IO.MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            Dispatch(text);
        }
        FailPending("The connection to the browser closed, so the call could not be answered. The window may have been closed or reloaded; call browser again.");
    }

    private void Dispatch(string text)
    {
        JObject message;
        try { message = JObject.Parse(text); }
        catch (Exception) { return; }
        var id = message["id"];
        if (id == null || id.Type != JTokenType.Integer) return;   // an event, which nothing here waits for
        TaskCompletionSource<JObject>? source;
        lock (_pending)
        {
            if (!_pending.TryGetValue(id.Value<int>(), out source)) return;
        }
        var error = message["error"];
        if (error != null && error.Type != JTokenType.Null)
        {
            source.TrySetException(new CdpException(ProtocolError(error)));
            return;
        }
        source.TrySetResult(message["result"] as JObject ?? new JObject());
    }

    private static string ProtocolError(JToken error)
    {
        var message = error["message"]?.Type == JTokenType.String ? error["message"]!.Value<string>() : null;
        var data = error["data"]?.Type == JTokenType.String ? error["data"]!.Value<string>() : null;
        if (string.IsNullOrEmpty(message)) message = "the protocol refused the call without saying why";
        return string.IsNullOrEmpty(data) ? message! : message + ": " + data;
    }

    private void FailPending(string why)
    {
        List<TaskCompletionSource<JObject>> waiting;
        lock (_pending)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var source in waiting) source.TrySetException(new CdpException(why));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _socket.Dispose(); } catch (Exception) { }
        FailPending("The browser connection was closed.");
        _connect.Dispose();
        _send.Dispose();
    }
}
