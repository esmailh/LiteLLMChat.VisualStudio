using System;
using System.IO;
using System.Text;

namespace LiteLLM.Core;

/// <summary>Reads and writes a file without rewriting its encoding or its line endings.
///
/// A one-line edit must not turn a UTF-8-with-BOM, CRLF file into a plain UTF-8, LF one. The content would still be
/// correct and the user's source control would show the entire file as changed, which is how an edit tool loses
/// someone's trust on its first use.</summary>
public static class TextFile
{
    /// <summary>Everything needed to write the file back the way it was found.</summary>
    public sealed class Content
    {
        public string Text = "";
        public Encoding Encoding = new UTF8Encoding(false);
        public bool Preamble;
        public string NewLine = "\r\n";

        /// <summary>Rewrites <paramref name="text"/>'s line endings to the file's own, so an edited file keeps the
        /// convention it already had regardless of which endings the model wrote.</summary>
        public string Conform(string text)
        {
            var unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return NewLine == "\n" ? unified : unified.Replace("\n", NewLine);
        }
    }

    /// <summary>A file that does not exist yet: UTF-8 without a BOM, which is what every editor in Visual Studio
    /// writes for a new source file.</summary>
    public static Content New() => new Content { Encoding = new UTF8Encoding(false), Preamble = false, NewLine = "\r\n" };

    public static Content Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = Detect(bytes, out var preamble);
        var text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        return new Content { Text = text, Encoding = encoding, Preamble = preamble > 0, NewLine = DominantNewLine(text) };
    }

    public static void Write(string path, Content file, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        var body = file.Encoding.GetBytes(text);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            if (file.Preamble)
            {
                var marker = file.Encoding.GetPreamble();
                stream.Write(marker, 0, marker.Length);
            }
            stream.Write(body, 0, body.Length);
        }
    }

    /// <summary>Sniffs the byte-order mark, then falls back to "is it valid UTF-8?" — a file that is not gets read as
    /// the system's ANSI code page rather than as UTF-8 full of replacement characters.</summary>
    private static Encoding Detect(byte[] bytes, out int preamble)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { preamble = 3; return new UTF8Encoding(true); }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { preamble = 2; return new UnicodeEncoding(false, true); }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { preamble = 2; return new UnicodeEncoding(true, true); }
        preamble = 0;
        try { new UTF8Encoding(false, true).GetString(bytes); return new UTF8Encoding(false); }
        catch (DecoderFallbackException) { return Encoding.Default; }
    }

    /// <summary>Which line ending the text uses. Ties and a text with no line ending at all get CRLF: every project
    /// this extension has been pointed at is a Windows one, and a stray LF is the more surprising guess.
    ///
    /// Public because an edit made to a file that is open in the editor is measured against the buffer, which may hold
    /// unsaved changes and so do not match what is on disk.</summary>
    public static string DominantNewLine(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return lf > crlf ? "\n" : "\r\n";
    }
}
