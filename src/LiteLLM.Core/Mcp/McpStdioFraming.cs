using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

/// <summary>Newline-delimited UTF-8 JSON, the stdio framing.
///
/// This is separate from the transport so the awkward parts are testable over a <see cref="System.IO.MemoryStream"/>:
/// a multi-byte character split across reads, CRLF from a Windows shim, and a line long enough to be a runaway
/// process rather than a message. An overlong line is discarded to the next newline instead of buffered until the
/// process runs out of memory.</summary>
public static class McpStdioFraming
{
    public const int DefaultMaxLineChars = 16 * 1024 * 1024;

    public static async Task ReadLinesAsync(System.IO.Stream stream, Func<string, Task> onLine,
        Action<string> onDiagnostic, int maxLineChars, CancellationToken cancellationToken)
    {
        if (maxLineChars <= 0) maxLineChars = DefaultMaxLineChars;
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        var line = new StringBuilder();
        var overlong = false;

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
                    var character = chars[i];
                    if (character == '\n')
                    {
                        if (overlong)
                        {
                            onDiagnostic("A line longer than " + maxLineChars + " characters was discarded.");
                            overlong = false;
                            line.Clear();
                        }
                        else
                        {
                            var text = line.ToString().TrimEnd('\r');
                            line.Clear();
                            if (text.Length > 0) await onLine(text).ConfigureAwait(false);
                        }
                    }
                    else if (character == '\r')
                    {
                        // Part of a CRLF pair; the newline that follows ends the line.
                    }
                    else if (!overlong)
                    {
                        if (line.Length >= maxLineChars)
                        {
                            overlong = true;
                            line.Clear();
                        }
                        else line.Append(character);
                    }
                }
            }

            if (!overlong && line.Length > 0)
            {
                var text = line.ToString().TrimEnd('\r');
                if (text.Length > 0) await onLine(text).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw new OperationCanceledException(cancellationToken); }
    }
}
