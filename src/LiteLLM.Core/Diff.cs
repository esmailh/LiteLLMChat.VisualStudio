using System;
using System.Text;

namespace LiteLLM.Core;

/// <summary>The "show me what changed" text behind the chat's <c>Copy diff</c> command.
///
/// It is built from the edit that was applied rather than by diffing the file afterwards: the exact old and new lines
/// are already known, so the result is minimal and exact instead of an approximation of someone else's diff algorithm.
/// Nothing here is rendered into the transcript — the diff is offered on demand, so a large edit costs no tokens and
/// does not push the conversation off the screen.</summary>
public static class Diff
{
    public static string ForReplacement(string path, string originalNormalized, int at, string oldString, string newString)
    {
        var before = LineText.Normalize(originalNormalized);
        var offset = Math.Max(0, Math.Min(at, before.Length));
        var line = 1 + StringReplacer.Count(before.Substring(0, offset), "\n");
        var oldLines = LineText.Split(LineText.Normalize(oldString));
        var newLines = LineText.Split(LineText.Normalize(newString));

        var text = new StringBuilder();
        text.Append("--- ").Append(path).Append('\n');
        text.Append("+++ ").Append(path).Append('\n');
        text.Append("@@ -").Append(line).Append(',').Append(oldLines.Count)
            .Append(" +").Append(line).Append(',').Append(newLines.Count).Append(" @@\n");
        foreach (var value in oldLines) text.Append('-').Append(value).Append('\n');
        foreach (var value in newLines) text.Append('+').Append(value).Append('\n');
        return text.ToString();
    }

    /// <summary>The same diff for a change already reduced to its region, which is what a patch or a whole-file edit
    /// produces: there is no single offset to report, only the lines that went and the lines that arrived.</summary>
    public static string ForRegion(string path, TextChange change)
    {
        var text = new StringBuilder();
        text.Append("--- ").Append(path).Append('\n');
        text.Append("+++ ").Append(path).Append('\n');
        text.Append("@@ -").Append(change.StartLine).Append(',').Append(change.Removed.Count)
            .Append(" +").Append(change.StartLine).Append(',').Append(change.Added.Count).Append(" @@\n");
        foreach (var value in change.Removed) text.Append('-').Append(value).Append('\n');
        foreach (var value in change.Added) text.Append('+').Append(value).Append('\n');
        return text.ToString();
    }

    /// <summary>How many lines the edit removed and added, for the one-line row the chat shows in place of the diff.</summary>
    public static string Summary(string oldString, string newString)
    {
        var removed = LineText.Split(LineText.Normalize(oldString)).Count;
        var added = LineText.Split(LineText.Normalize(newString)).Count;
        return "+" + added + " −" + removed;
    }

    /// <summary>Counts a whole patch's effect, which is what a multi-file edit reports instead of one row per file.</summary>
    public static string Summary(int removed, int added) => "+" + added + " −" + removed;
}
