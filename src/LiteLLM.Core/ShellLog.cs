using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiteLLM.Core;

/// <summary>What one read of a shell log found.
///
/// A log is a file another process is still writing, so it is opened for sharing rather than through
/// <see cref="File.ReadAllLines"/> — which asks for a reader-only handle and can be refused by the shell that owns
/// the file. Reading from the end is the default: the line that matters in a log is the last one, and a server's
/// log has no meaningful "first page". Reading from a line the caller named is still available, so nothing is
/// unreachable.</summary>
public sealed class ShellLogRead
{
    /// <summary>False when the file could not be opened — it may not have been created yet, or another process
    /// holds it in a way sharing does not allow.</summary>
    public bool Found { get; set; } = true;

    /// <summary>The lines that were asked for, one per line, without a trailing newline.</summary>
    public string Text { get; set; } = "";

    /// <summary>How many lines <see cref="Text"/> holds.</summary>
    public int Count { get; set; }

    /// <summary>More lines follow the last one returned. Only a read from the start can say this.</summary>
    public bool More { get; set; }

    /// <summary>Lines before the first one returned were not read, either because the read started at the end or
    /// because the file is longer than the window that was examined.</summary>
    public bool Earlier { get; set; }

    /// <summary>1-based first line returned, for a read that named its start; 0 for a read from the end, which
    /// does not count the lines before it.</summary>
    public int FirstLine { get; set; }

    /// <summary>1-based last line returned; 0 for a read from the end.</summary>
    public int LastLine { get; set; }

    /// <summary>The file's size in bytes, so a caller can tell an empty log from a missing one.</summary>
    public long Length { get; set; }
}

/// <summary>Reads the tail of a log file a background shell is writing.
///
/// The encoding is worked out from the file rather than assumed: PowerShell's redirection writes UTF-16 with a
/// byte-order mark, cmd's writes whatever the child wrote, and a log that is read as the wrong one is mojibake
/// that looks like a program printing garbage. Only the end of the file is examined unless a line was named,
/// because counting the lines of a large log to show its last twenty costs more than the answer is worth.</summary>
public static class ShellLog
{
    /// <summary>How much of the end of a log is examined to find its last lines.</summary>
    private const int TailWindowBytes = 256 * 1024;

    public const int MaxChars = 60000;

    /// <summary>Reads <paramref name="count"/> lines. With <paramref name="fromStart"/> the read begins at the
    /// 1-based <paramref name="startLine"/>; otherwise it ends at the last line of the file.</summary>
    public static ShellLogRead Read(string? path, int startLine, int count, bool fromStart, int maxChars = MaxChars)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ShellLogRead { Found = false };
        if (count < 1) count = 1;
        if (maxChars < 200) maxChars = 200;
        try
        {
            if (!File.Exists(path)) return new ShellLogRead { Found = false };
            using (var stream = new FileStream(path!, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                var result = new ShellLogRead { Length = stream.Length };
                if (startLine < 1) startLine = 1;
                return fromStart
                    ? ReadFrom(stream, startLine, count, maxChars, result)
                    : ReadTail(stream, count, maxChars, result);
            }
        }
        catch (IOException) { return new ShellLogRead { Found = false }; }
        catch (UnauthorizedAccessException) { return new ShellLogRead { Found = false }; }
    }

    private static ShellLogRead ReadTail(FileStream stream, int count, int maxChars, ShellLogRead result)
    {
        var encoding = Detect(stream);
        var offset = result.Length > TailWindowBytes ? result.Length - TailWindowBytes : 0;
        // A UTF-16 window that starts on an odd byte starts in the middle of a code unit, which turns every
        // character after it into noise.
        if (encoding is UnicodeEncoding && offset % 2 == 1) offset++;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Max(0, result.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0) break;
            read += got;
        }
        var text = encoding.GetString(buffer, 0, read);
        if (offset == 0 && text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        if (offset > 0)
        {
            // The window can begin inside a line; that fragment is not a line the log has.
            var newline = text.IndexOf('\n');
            text = newline >= 0 ? text.Substring(newline + 1) : "";
        }
        var lines = Lines(text);
        var take = Math.Min(count, lines.Count);
        var first = lines.Count - take;
        result.Count = take;
        result.More = false;
        result.Earlier = offset > 0 || first > 0;
        result.Text = Clip(Join(lines, first, take), maxChars);
        return result;
    }

    private static ShellLogRead ReadFrom(FileStream stream, int startLine, int count, int maxChars, ShellLogRead result)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var taken = new List<string>(count);
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
        {
            var line = 0;
            string? current;
            while ((current = reader.ReadLine()) != null)
            {
                line++;
                if (line < startLine) continue;
                if (taken.Count == count) { result.More = true; break; }
                taken.Add(current);
            }
            result.Earlier = startLine > 1;
            result.Count = taken.Count;
            result.FirstLine = startLine;
            result.LastLine = startLine + taken.Count - 1;
            result.Text = Clip(Join(taken, 0, taken.Count), maxChars);
            if (taken.Count == 0) result.Found = false;
            return result;
        }
    }

    /// <summary>Splits text into lines and drops the empty one a trailing newline leaves behind, so "one line and
    /// a newline" is one line rather than two.</summary>
    private static List<string> Lines(string text)
    {
        var lines = new List<string>(text.Split('\n'));
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && line[line.Length - 1] == '\r') lines[i] = line.Substring(0, line.Length - 1);
        }
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Join(List<string> lines, int start, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) text.Append('\n');
            text.Append(lines[start + i]);
        }
        return text.ToString();
    }

    /// <summary>The encoding the file itself declares, looked for at its beginning. A UTF-16 file is recognised
    /// without a byte-order mark as well, because PowerShell's own redirection is not the only writer of one and
    /// text whose every other byte is a zero is not UTF-8.</summary>
    private static Encoding Detect(FileStream stream)
    {
        var head = new byte[4];
        stream.Seek(0, SeekOrigin.Begin);
        var read = stream.Read(head, 0, head.Length);
        if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return Encoding.UTF8;
        if (read == 4 && head[0] != 0 && head[1] == 0 && head[2] != 0 && head[3] == 0) return Encoding.Unicode;
        return Encoding.UTF8;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "\n… [" + (text.Length - max) + " more characters]";
}
