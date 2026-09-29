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
    public static async Task ReadAsync(Stream stream, Func<string, Task> onData, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();       // incremental: a chunk can split a multi-byte character
        var chars = new char[8192];
        var line = new StringBuilder();
        var data = new List<string>();
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
                    if (c == '\n') { await DispatchAsync(line, data, onData).ConfigureAwait(false); line.Clear(); }
                    else if (c != '\r') line.Append(c);
                }
            }
            if (line.Length > 0) await DispatchAsync(line, data, onData).ConfigureAwait(false);
            if (data.Count > 0) await onData(string.Join("\n", data)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw new OperationCanceledException(cancellationToken); }
    }

    private static async Task DispatchAsync(StringBuilder line, List<string> data, Func<string, Task> onData)
    {
        if (line.Length == 0)
        {
            if (data.Count > 0) { await onData(string.Join("\n", data)).ConfigureAwait(false); data.Clear(); }
            return;
        }
        var text = line.ToString();
        if (text[0] == ':') return;
        if (text.StartsWith("data:", StringComparison.Ordinal)) data.Add(text.Substring(5).TrimStart());
    }
}
