using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteLLM.Core;

/// <summary>One <c>@@</c> block of an update: the lines the patch expects to find, and the lines that replace them.
/// <see cref="Before"/> and <see cref="After"/> interleave context lines with removals and additions, which is what
/// makes a hunk an exact three-line-anchored match rather than a search for loose fragments.</summary>
public sealed class PatchHunk
{
    public string Header = "";
    public List<string> Before = new List<string>();
    public List<string> After = new List<string>();
    /// <summary>Set by <c>*** End of File</c>: the block must sit at the very end, which is how a patch appends.</summary>
    public bool EndOfFile;

    internal void Context(string line) { Before.Add(line); After.Add(line); }
}

/// <summary>One file's worth of a patch: a whole new file, a deletion, or a set of hunks against an existing one.</summary>
public sealed class PatchFile
{
    public string Path = "";
    /// <summary>Set by <c>*** Move to:</c> — the path the file is renamed to after its hunks are applied.</summary>
    public string? MoveTo;
    public bool Delete;
    /// <summary>The full contents when the patch adds a file, otherwise null.</summary>
    public string? NewContent;
    public List<PatchHunk> Hunks = new List<PatchHunk>();

    public bool IsAdd => NewContent != null;
    public bool IsUpdate => !Delete && NewContent == null;
}

/// <summary>The <c>apply_patch</c> format, taken from the grammar in Copilot's own tool description:
/// <code>
/// Patch    := "*** Begin Patch" NEWLINE { FileOp } "*** End Patch" NEWLINE
/// FileOp   := AddFile | DeleteFile | UpdateFile
/// AddFile  := "*** Add File: " path NEWLINE { "+" line NEWLINE }
/// UpdateFile := "*** Update File: " path NEWLINE [ MoveTo ] { Hunk }
/// Hunk     := "@@" [ header ] NEWLINE { (" " | "-" | "+") text NEWLINE } [ "*** End of File" NEWLINE ]
/// </code>
/// This is a parser and an applier only — it never touches the disk, so every rule here is testable without one.</summary>
public static class PatchApply
{
    public static List<PatchFile> Parse(string patch)
    {
        var lines = LineText.Normalize(patch ?? "").Split('\n').ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // the patch's own trailing newline

        var start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0) start++;
        if (start >= lines.Count || !Is(lines[start], "*** Begin Patch"))
            throw new EditFailure("The patch must start with a line reading \"*** Begin Patch\".");

        var files = new List<PatchFile>();
        PatchFile? current = null;
        PatchHunk? hunk = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (Is(line, "*** End Patch"))
            {
                if (files.Count == 0) throw new EditFailure("The patch contains no file sections.");
                return files;
            }

            if (line.StartsWith("*** Add File:", StringComparison.Ordinal))
            {
                current = new PatchFile { Path = Rest(line, "*** Add File:"), NewContent = "" }; files.Add(current); hunk = null; continue;
            }
            if (line.StartsWith("*** Delete File:", StringComparison.Ordinal))
            {
                current = new PatchFile { Path = Rest(line, "*** Delete File:"), Delete = true }; files.Add(current); hunk = null; continue;
            }
            if (line.StartsWith("*** Update File:", StringComparison.Ordinal))
            {
                current = new PatchFile { Path = Rest(line, "*** Update File:") }; files.Add(current); hunk = null; continue;
            }
            if (line.StartsWith("*** Move to:", StringComparison.Ordinal))
            {
                if (current == null) throw new EditFailure("The patch has a \"*** Move to:\" line before any file.");
                current.MoveTo = Rest(line, "*** Move to:"); continue;
            }
            if (Is(line, "*** End of File"))
            {
                if (hunk != null) hunk.EndOfFile = true;
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current == null || !current.IsUpdate) throw new EditFailure("The patch has a hunk before any \"*** Update File:\" line.");
                hunk = new PatchHunk { Header = line.Substring(2).Trim() };
                current.Hunks.Add(hunk);
                continue;
            }

            if (current == null) throw new EditFailure("Line " + (i + 1) + " of the patch is outside any file section: " + line);
            if (current.IsAdd) { AddLine(current, line, i); continue; }
            if (current.Delete) continue;   // nothing follows a deletion
            if (hunk == null)
            {
                if (line.Length == 0) continue;
                throw new EditFailure("Line " + (i + 1) + " of the patch is inside a file but outside a hunk. Each hunk starts with \"@@\": " + line);
            }
            Body(hunk, line, i);
        }
        throw new EditFailure("The patch is missing its \"*** End Patch\" line.");
    }

    /// <summary>Applies an update's hunks to <paramref name="original"/>. Line endings are normalised to "\n" here and
    /// restored by the caller, so the matching rules below never depend on which convention the file uses.
    ///
    /// Hunks apply in order against a moving index, so a patch that changes two similar regions cannot have its second
    /// hunk match the first one's location.</summary>
    public static string Apply(PatchFile file, string original)
    {
        var normalized = LineText.Normalize(original);
        var trailing = normalized.Length == 0 || normalized.EndsWith("\n");
        var lines = LineText.Split(normalized);
        var index = 0;
        for (var h = 0; h < file.Hunks.Count; h++)
        {
            var hunk = file.Hunks[h];
            var at = Locate(lines, hunk, index, file.Path, h + 1);
            lines.RemoveRange(at, hunk.Before.Count);
            lines.InsertRange(at, hunk.After);
            index = at + hunk.After.Count;
        }
        var result = string.Join("\n", lines);
        return trailing && result.Length > 0 ? result + "\n" : result;
    }

    private static int Locate(List<string> lines, PatchHunk hunk, int from, string subject, int number)
    {
        // A hunk with no context is a pure insertion: it has nothing to anchor to, so it goes where the patch put it.
        if (hunk.Before.Count == 0) return hunk.EndOfFile ? lines.Count : Math.Min(from, lines.Count);

        var limit = lines.Count - hunk.Before.Count;
        for (var at = Math.Max(0, Math.Min(from, limit)); at <= limit; at++)
        {
            var match = true;
            for (var k = 0; k < hunk.Before.Count; k++)
                if (!string.Equals(lines[at + k], hunk.Before[k], StringComparison.Ordinal)) { match = false; break; }
            if (!match) continue;
            if (hunk.EndOfFile && at + hunk.Before.Count != lines.Count) continue;
            return at;
        }
        throw new EditFailure("Hunk " + number + " does not match anything in " + subject
            + ". Read the file again and write the patch against what it actually contains.");
    }

    private static void AddLine(PatchFile file, string line, int index)
    {
        if (line.StartsWith("+", StringComparison.Ordinal)) file.NewContent += line.Substring(1) + "\n";
        else if (line.Length == 0) file.NewContent += "\n";   // a blank line in an added file, prefix lost in transit
        else throw new EditFailure("Line " + (index + 1) + " of an added file must start with \"+\": " + line);
    }

    private static void Body(PatchHunk hunk, string line, int index)
    {
        if (line.StartsWith("\\", StringComparison.Ordinal)) return;   // "\ No newline at end of file" carries nothing
        if (line.Length == 0) { hunk.Context(""); return; }            // a blank context line, its single space gone
        switch (line[0])
        {
            case ' ': hunk.Context(line.Substring(1)); return;
            case '-': hunk.Before.Add(line.Substring(1)); return;
            case '+': hunk.After.Add(line.Substring(1)); return;
            default: throw new EditFailure("Line " + (index + 1) + " of the patch must start with \" \", \"-\" or \"+\": " + line);
        }
    }

    private static bool Is(string line, string marker) => line.Trim().Equals(marker, StringComparison.Ordinal);
    private static string Rest(string line, string marker) => line.Substring(marker.Length).Trim();
}

/// <summary>Line handling shared by the patch parser and the diff writer. Everything here works on "\n"-normalised
/// text; converting to and from the file's own convention is <see cref="TextFile.Content.Conform"/>'s job.</summary>
public static class LineText
{
    public static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Splits normalised text into lines without the empty element a trailing newline leaves behind.</summary>
    public static List<string> Split(string normalized)
    {
        var lines = new List<string>(normalized.Split('\n'));
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
