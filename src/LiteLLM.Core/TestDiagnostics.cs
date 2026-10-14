using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>What one automatic test run reported, read out of the console output of
/// <c>vstest.console.exe</c>.
///
/// Deliberately shaped as a <see cref="BuildReport"/>: the row, the tooltip, the field the turn keeps and the
/// way the repair loop is driven are the same either way, and the only thing that differs is which command
/// ran. A failing test lands in <c>Errors</c>, so <c>report.Errors == 0</c> keeps meaning what it means for a
/// build; a skipped test lands in <c>Warnings</c>, which is the same kind of thing a build warning is —
/// reported, and not a failure. The labels say "skipped", because nothing here is a compiler's warning.</summary>
public static class TestDiagnostics
{
    public const int MaxDetailChars = 12000;

    /// <summary>Failure blocks carried into the detail. One block can be hundreds of characters of assertion
    /// text, so the list is bounded like everything else in this loop — and the detail says how many were left
    /// out, because a partial list that reads as the whole of it is how a model concludes it has seen every
    /// failure when it has seen the first five.</summary>
    public const int MaxFailures = 5;

    /// <summary>What the loop says when the tests are still failing and the model answered with prose. Separate
    /// from <see cref="BuildDiagnostics.RepairReminder"/>, which says the build is failing: the two ask for
    /// different work, and the test one carries the rule that matters most here — a red test is not made green
    /// by editing the test.</summary>
    public const string RepairReminder = "The tests are still failing after your edits. Do not finish with an explanation or claim success: inspect the failures already provided, fix the code responsible, and let automatic verification run again. Do not delete, skip or weaken a test to make it pass. If none of the failures were caused by your change, state that explicitly.";

    /// <summary>Every count the run reported, or null when the output carries no result line at all — the run
    /// never got as far as reporting, which is not the same as a run that reported no failures. The caller
    /// says which of the two happened instead of passing silence off as a pass.</summary>
    public static BuildReport? Summarize(string? output, int maxChars = MaxDetailChars)
    {
        var text = (output ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (!Counts(text, out var total, out var passed, out var failed, out var skipped)) return null;

        return new BuildReport
        {
            Errors = failed,
            Warnings = skipped,
            Summary = ToSummaryLine(total, passed, failed, skipped),
            // Only failures are carried: what passed is a number, and a list of two hundred passing names is
            // the noise that made the whole list unreadable in the first place.
            Detail = Failures(text, maxChars)
        };
    }

    /// <summary>The row's label. One line by construction, because the chat row is one line, and it carries the
    /// totals as well as the failures — "no failures" alone would read the same whether the suite ran or found
    /// nothing to run.</summary>
    public static string ToSummaryLine(int total, int passed, int failed, int skipped)
    {
        var text = new StringBuilder("Tests · ");
        if (failed > 0) text.Append(failed).Append(" failed, ").Append(passed).Append(" passed");
        else text.Append("all ").Append(total).Append(" passed");
        if (skipped > 0) text.Append(", ").Append(skipped).Append(" skipped");
        return text.ToString();
    }

    /// <summary>How the row reads when nothing could be verified. It says which check did not happen rather
    /// than staying quiet or, worse, letting the absence of a failure row read as a pass.</summary>
    public static string ToUnverifiedLine(string reason) =>
        "Tests · not run: " + ChatStateLimits.OneLine(reason ?? "", 160);

    /// <summary>What goes back to the model when a test failed. A user message rather than a tool result, for
    /// the same reason the build note is: a tool result answering no announced call is dropped by
    /// <see cref="ChatStateLimits.Sanitize"/>, and this has to survive to the next round.</summary>
    public static string ToModelNote(BuildReport report)
    {
        var text = new StringBuilder();
        text.Append("Automatic verification: the solution's tests were run for you after your edits, so you do not need to run them yourself. ");
        text.Append(report.Errors > 0
            ? "They still report " + Count(report.Errors, "failing test") + (report.Warnings > 0 ? " and " + Count(report.Warnings, "skipped test") : "") + "."
            : "They report no failures.");
        text.Append("\n\n").Append(report.Detail);
        // Test edits are the one way this check can be satisfied without the code being fixed, so the rule is
        // stated where it is read: at the point the model is deciding what to change.
        text.Append("\n\nFix the code your change broke, then stop. Do not delete, skip or weaken a test to make it pass — a test that no longer describes the wanted behaviour is the user's decision, not a silent edit. If a failure is not caused by your change, say so plainly and leave it alone rather than editing around it.");
        return text.ToString();
    }

    /// <summary>Reads the run's totals. Two shapes, because both are real and which one arrives depends on how
    /// the runner was invoked: one line of key/value pairs ("Failed!  - Failed:  2, Passed:  207, …") and the
    /// block of one key per line ("Total tests: 210"). The last of either wins, because a run that retried
    /// writes its result at the end.</summary>
    private static bool Counts(string text, out int total, out int passed, out int failed, out int skipped)
    {
        total = passed = failed = skipped = 0;
        var found = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // The pair line is the only place all three appear together, which is what tells it apart from a
            // test whose name happens to contain the word "Failed:".
            if (!IsResult(line)) continue;

            found = true;
            foreach (Match match in Pairs.Matches(line))
            {
                var value = int.Parse(match.Groups[2].Value);
                switch (match.Groups[1].Value)
                {
                    case "Total": total = value; break;
                    case "Passed": passed = value; break;
                    case "Failed": failed = value; break;
                    case "Skipped": skipped = value; break;
                }
            }
        }
        if (found) return true;

        foreach (var raw in text.Split('\n'))
        {
            var match = Block.Match(raw.Trim());
            if (!match.Success) continue;
            var value = int.Parse(match.Groups[2].Value);
            switch (match.Groups[1].Value)
            {
                case "Total tests": total = value; found = true; break;
                case "Passed": passed = value; break;
                case "Failed": failed = value; break;
                case "Skipped": skipped = value; break;
            }
        }
        return found;
    }

    /// <summary>The failure blocks, bounded. A block opens on the line naming the test and carries its message
    /// and stack trace; the runner's own duplicate of the same failure, printed per diagnostic on another
    /// stream, is skipped, since the block below it is the readable copy.</summary>
    private static string Failures(string text, int maxChars)
    {
        var blocks = new List<List<string>>();
        List<string>? block = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("[xUnit.net", StringComparison.Ordinal)) continue;
            if (Opens(trimmed))
            {
                if (block != null) blocks.Add(block);
                block = new List<string> { trimmed };
                continue;
            }
            if (block == null) continue;
            if (Closes(trimmed) || Totals(trimmed)) { blocks.Add(block); block = null; continue; }
            block.Add(line);
        }
        if (block != null) blocks.Add(block);
        if (blocks.Count == 0) return "";

        // Added one at a time so the count is of what actually went in: joining the first five and clipping the
        // result would let the note promise five failures and deliver two.
        var detail = new StringBuilder();
        var kept = 0;
        foreach (var candidate in blocks)
        {
            if (kept >= MaxFailures) break;
            var body = string.Join("\n", candidate);
            if (kept > 0 && detail.Length + body.Length + 2 > Math.Max(1, maxChars)) break;
            if (kept > 0) detail.Append("\n\n");
            detail.Append(body);
            kept++;
        }
        var result = ChatStateLimits.Clip(detail.ToString(), Math.Max(1, maxChars));
        if (kept < blocks.Count)
            result += "\n\n(" + kept + " of " + blocks.Count + " failing tests shown)";
        return result;
    }

    /// <summary>A line that names a failing test opens a block. The count line "Test Run Failed." opens
    /// nothing: it does not begin with the word, and a block whose opening line is not a test name would be
    /// reported as a failure with no name.</summary>
    private static bool Opens(string trimmed) => trimmed.StartsWith("Failed ", StringComparison.Ordinal);

    /// <summary>A line that names another test — failed, passed or skipped — ends the block above it. Those
    /// lines are written at the runner's own indentation, under the same convention as the opening line, so
    /// assertion text that happens to mention a skipped test is not mistaken for one.</summary>
    private static bool Closes(string trimmed) =>
        trimmed.StartsWith("Passed ", StringComparison.Ordinal) || trimmed.StartsWith("Skipped ", StringComparison.Ordinal);

    /// <summary>Whether a line carries the run's totals rather than a failure. It ends a block for the same
    /// reason the lines above do: the result line follows the last failure with nothing between them, and
    /// leaving it there puts the counts at the bottom of the model's failure text as if it were a stack
    /// frame.</summary>
    private static bool Totals(string trimmed) => IsResult(trimmed) || Block.IsMatch(trimmed) || trimmed.StartsWith("Test Run ", StringComparison.Ordinal);

    /// <summary>The pair line: the one place all three counts appear together, which is what keeps a test whose
    /// name contains the word "Failed:" from being read as one.</summary>
    private static bool IsResult(string line) =>
        line.IndexOf("Total:", StringComparison.Ordinal) >= 0
        && line.IndexOf("Failed:", StringComparison.Ordinal) >= 0
        && line.IndexOf("Passed:", StringComparison.Ordinal) >= 0;

    private static string Count(int value, string noun) => value + " " + noun + (value == 1 ? "" : "s");

    private static readonly Regex Pairs = new Regex(@"(Failed|Passed|Skipped|Total):\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex Block = new Regex(@"^(Total tests|Passed|Failed|Skipped):\s*(\d+)$", RegexOptions.Compiled);
}
