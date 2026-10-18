using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

public static class SseParser
{
    /// <summary>Reads an SSE stream, refusing to block past <paramref name="cancellationToken"/>.
    /// <para><see cref="StreamReader.ReadLineAsync()"/> takes no token and cannot be cancelled, so a server that
    /// opens the stream and then goes quiet would leave the caller — and its Stop button — waiting forever.
    /// Reading the raw bytes honours the token and lets the request time out or be stopped.</para></summary>
    public static Task ReadAsync(Stream stream, Func<string, Task> onData, CancellationToken cancellationToken) =>
        ReadAsync(stream, (data, eventName) => onData(data), cancellationToken);

    /// <summary>Reads an SSE stream and reports each event's name along with its data.
    ///
    /// The name matters to MCP: a legacy HTTP+SSE server sends the URL to post to as an <c>endpoint</c> event, and
    /// every JSON-RPC message as a <c>message</c> event, on the same stream. The name is null when the event had
    /// none, which the SSE specification treats as <c>message</c>.</summary>
    public static async Task ReadAsync(Stream stream, Func<string, string?, Task> onEvent, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();       // incremental: a chunk can split a multi-byte character
        var chars = new char[8192];
        var line = new StringBuilder();
        var data = new List<string>();
        var eventName = "";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                for (var i = 0; i < count; i++)
                {
                    var c = chars[i];
                    if (c == '\n')
                    {
                        eventName = await DispatchAsync(line, data, eventName, onEvent).ConfigureAwait(false);
                        line.Clear();
                    }
                    else if (c != '\r') line.Append(c);
                }
            }
            if (line.Length > 0) eventName = await DispatchAsync(line, data, eventName, onEvent).ConfigureAwait(false);
            if (data.Count > 0) await onEvent(string.Join("\n", data), Named(eventName)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw new OperationCanceledException(cancellationToken); }
    }

    private static string? Named(string eventName) => eventName.Length > 0 ? eventName : null;

    private static async Task<string> DispatchAsync(StringBuilder line, List<string> data, string eventName,
        Func<string, string?, Task> onEvent)
    {
        if (line.Length == 0)
        {
            if (data.Count > 0)
            {
                var name = Named(eventName);
                await onEvent(string.Join("\n", data), name).ConfigureAwait(false);
                data.Clear();
                return "";
            }
            return eventName;
        }
        var text = line.ToString();
        if (text[0] == ':') return eventName;
        if (text.StartsWith("data:", StringComparison.Ordinal)) data.Add(text.Substring(5).TrimStart());
        else if (text.StartsWith("event:", StringComparison.Ordinal)) eventName = text.Substring(6).Trim();
        return eventName;
    }
}
