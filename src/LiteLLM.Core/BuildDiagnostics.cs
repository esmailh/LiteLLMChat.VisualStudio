using System;
using System.Text;

namespace LiteLLM.Core;

/// <summary>What one automatic post-edit build reported. Stored on the turn so a reloaded conversation
/// still shows the row, and shaped here rather than in the chat so the counting and the clipping are
/// testable without Visual Studio.</summary>
public sealed class BuildReport
{
    public int Errors { get; set; }
    public int Warnings { get; set; }
    /// <summary>The one-line label the chat row shows.</summary>
    public string Summary { get; set; } = "";
    /// <summary>The bounded diagnostics, for the row's tooltip and the model's note.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>Turns the Error List, read as text, into a report. Deliberately language-agnostic: the
/// Error List carries a severity, a description, a file and a line for every project type, so nothing
/// here knows about C# compiler codes or any other language's error format.</summary>
public static class BuildDiagnostics
{
    public const int MaxDetailChars = 12000;
    public const string RepairReminder = "The verification build is still failing. Do not finish with an explanation or claim success: inspect the diagnostics already provided, edit the files responsible for errors caused by your change, and let automatic verification run again. If none of the errors were caused by your change, state that explicitly.";

    /// <summary>Counts the entries and bounds the text. <paramref name="errorListText"/> is the newline
    /// separated shape the Error List reader produces — "error: description — file:line". Counting is
    /// best-effort: a description carrying its own line break reads as an entry with no severity, which
    /// undercounts rather than inventing one.</summary>
    public static BuildReport Summarize(string? errorListText, int maxChars = MaxDetailChars)
    {
        var text = (errorListText ?? "").Trim();
        var report = new BuildReport { Detail = ChatStateLimits.Clip(text, Math.Max(1, maxChars)) };
        if (text.Length == 0)
        {
            report.Summary = ToSummaryLine(report);
            return report;
        }

        foreach (var line in text.Split('\n'))
        {
            switch (Severity(line))
            {
                case "error": report.Errors++; break;
                case "warning": report.Warnings++; break;
            }
        }
        report.Summary = ToSummaryLine(report);
        return report;
    }

    /// <summary>The severity a line opens with, or null when it opens with neither. The leading token has
    /// to be exactly the severity, so a description that merely mentions an error is not counted as one:
    /// everything after the first colon is the description, and the Error List writes the code there.</summary>
    private static string? Severity(string line)
    {
        var trimmed = line.TrimStart();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return null;
        var token = trimmed.Substring(0, colon).Trim();
        if (token.Equals("error", StringComparison.OrdinalIgnoreCase)) return "error";
        if (token.Equals("warning", StringComparison.OrdinalIgnoreCase)) return "warning";
        return null;
    }

    /// <summary>The row's label. One line by construction, because the chat row is one line.</summary>
    public static string ToSummaryLine(BuildReport report)
    {
        if (report.Errors == 0 && report.Warnings == 0) return "Build · no errors or warnings";
        var text = new StringBuilder("Build · ");
        if (report.Errors > 0) text.Append(Count(report.Errors, "error"));
        if (report.Errors > 0 && report.Warnings > 0) text.Append(", ");
        if (report.Warnings > 0) text.Append(Count(report.Warnings, "warning"));
        return text.ToString();
    }

    private static string Count(int value, string noun) => value + " " + noun + (value == 1 ? "" : "s");

    /// <summary>Ceiling on the standing note's detail. The Error List covers the whole solution and most of
    /// what is in it is older than the conversation, so the model is given the count and the beginning of the
    /// list rather than all 12 000 characters of it on every round of every turn.</summary>
    public const int MaxStandingLines = 20;
    public const int MaxStandingChars = 2000;

    /// <summary>The Error List as it stands, for the system prompt — or "" when it holds nothing, which costs
    /// the request exactly nothing. Deliberately not <see cref="ToModelNote"/>: that one answers a build this
    /// turn ran and says so, and nothing was built here. Letting the model believe otherwise would have it read
    /// an entry from before its work as the result of it. It goes in the system prompt and not in the history
    /// because the system prompt is rebuilt per request: the block is current by construction, needs no
    /// sanitizing, and disappears on its own once the list is clean.</summary>
    public static string ToStandingNote(string? errorListText)
    {
        var raw = (errorListText ?? "").Trim();
        var report = Summarize(raw);   // the counts only, and over the whole list rather than over what is shown
        if (report.Errors == 0 && report.Warnings == 0) return "";

        // One line at a time, and the count is taken from what actually went in: joining the first twenty and then
        // clipping the result lets the note promise twenty lines and deliver nine, and a list the model reads as
        // smaller than it is has stopped being told the truth about the solution's state. The first line goes in
        // whatever its length, because an entry can be enormous — a template error names every type it tried — and
        // a list whose opening entry is unreadable still has to say something about it.
        var lines = raw.Split('\n');
        var detail = new StringBuilder();
        var kept = 0;
        for (var i = 0; i < lines.Length && i < MaxStandingLines; i++)
        {
            if (i > 0 && detail.Length + 1 + lines[i].Length > MaxStandingChars) break;
            if (i > 0) detail.Append('\n');
            detail.Append(i == 0 ? ChatStateLimits.Clip(lines[i], MaxStandingChars) : lines[i]);
            kept++;
        }

        var text = new StringBuilder("The solution's Error List currently holds ");
        if (report.Errors > 0) text.Append(Count(report.Errors, "error"));
        if (report.Errors > 0 && report.Warnings > 0) text.Append(" and ");
        if (report.Warnings > 0) text.Append(Count(report.Warnings, "warning"));
        text.Append(". It was read, not built for this request, so part of it may predate the work you are doing.");
        if (kept < lines.Length) text.Append(" Showing the first ").Append(kept).Append(" of ").Append(lines.Length).Append(" lines.");
        text.Append("\n\n").Append(detail);
        text.Append("\n\nDo not set out to clear this list. Fix what your own change caused; if an entry is not caused by your change, say so plainly and leave it alone rather than editing around it.");
        return text.ToString();
    }

    /// <summary>What goes back to the model when the build found errors. It is injected into the history
    /// as a user message rather than a tool result, because a tool result that answers no announced call
    /// is dropped by <see cref="ChatStateLimits.Sanitize"/> — and this note has to survive to the next
    /// round, which is the whole point of it.</summary>
    public static string ToModelNote(BuildReport report)
    {
        var text = new StringBuilder();
        text.Append("Automatic verification: the solution was built for you after your edits, so you do not need to build it yourself. ");
        text.Append(report.Errors > 0
            ? "It still reports " + Count(report.Errors, "error") + (report.Warnings > 0 ? " and " + Count(report.Warnings, "warning") : "") + "."
            : "It reports no errors.");
        text.Append("\n\n").Append(report.Detail);
        text.Append("\n\nFix the errors your change caused, then stop. If an entry is not caused by your change, say so plainly and leave it alone rather than editing around it.");
        return text.ToString();
    }
}
