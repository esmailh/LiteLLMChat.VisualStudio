using System;
using System.Collections.Generic;
using System.IO;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class BuildTests
{
    private static readonly string Sample = string.Join("\n", new[]
    {
        "error: CS1002: ; expected — C:\\sol\\src\\Foo.cs:42",
        "warning: CS0168: The variable 'x' is declared but never used — C:\\sol\\src\\Foo.cs:7",
        "error: CS0246: The type or namespace name 'Bar' could not be found — C:\\sol\\src\\Baz.cs:3",
        "message: some informational entry"
    });

    [Fact] public void BuildDiagnostics_CountsErrorsAndWarningsFromTheErrorListText()
    {
        var report = BuildDiagnostics.Summarize(Sample);
        Assert.Equal(2, report.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Equal("Build · 2 errors, 1 warning", report.Summary);
        Assert.Contains("Baz.cs", report.Detail, StringComparison.Ordinal);
    }

    [Fact] public void BuildDiagnostics_TreatsACleanOrUnavailableListAsNoErrors()
    {
        // A description that merely mentions an error is not an entry: only the severity before the first colon
        // counts, which is what keeps a message like the one below from being read as a failure.
        var quiet = BuildDiagnostics.Summarize("message: this error was already fixed");
        Assert.Equal(0, quiet.Errors);
        Assert.Equal(0, quiet.Warnings);
        Assert.Equal("Build · no errors or warnings", quiet.Summary);

        var empty = BuildDiagnostics.Summarize("");
        Assert.Equal(0, empty.Errors);
        Assert.Equal("", empty.Detail);
    }

    [Fact] public void BuildDiagnostics_BoundsTheDetailSoABuildLogCannotBloatTheTurn()
    {
        var huge = new string('x', BuildDiagnostics.MaxDetailChars + 500);
        var report = BuildDiagnostics.Summarize("error: " + huge);
        Assert.Equal(BuildDiagnostics.MaxDetailChars + ChatStateLimits.TruncationMarker.Length, report.Detail.Length);
    }

    [Fact] public void BuildDiagnostics_ModelNoteSaysTheBuildWasAutomaticAndCarriesTheDetail()
    {
        var note = BuildDiagnostics.ToModelNote(BuildDiagnostics.Summarize(Sample));
        // The model has to know it does not need to build the solution itself, or it spends a round doing so.
        Assert.Contains("built", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 errors", note, StringComparison.Ordinal);
        Assert.Contains("Baz.cs", note, StringComparison.Ordinal);
    }

    [Fact] public void BuildDiagnostics_RepairReminderRequiresAnotherEditAndBuild()
    {
        Assert.Contains("still failing", BuildDiagnostics.RepairReminder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edit", BuildDiagnostics.RepairReminder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verification", BuildDiagnostics.RepairReminder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void BuildDiagnostics_StandingNoteCarriesTheCountsTheBeginningAndWhatIsNotTheModelsFault()
    {
        var note = BuildDiagnostics.ToStandingNote(Sample);

        Assert.Contains("2 errors", note, StringComparison.Ordinal);
        Assert.Contains("1 warning", note, StringComparison.Ordinal);
        Assert.Contains("Baz.cs", note, StringComparison.Ordinal);
        // It has to say it was read rather than built: this block goes out on every round, including rounds where
        // nothing was built, and an entry from last week must not read as the result of what the model just changed.
        Assert.Contains("not built for this request", note, StringComparison.Ordinal);
        Assert.Contains("if an entry is not caused by your change, say so plainly and leave it alone", note, StringComparison.Ordinal);
        // The post-edit note's opening would be a lie here, so the two notes must not be the same text.
        Assert.DoesNotContain("built for you after your edits", note, StringComparison.Ordinal);
    }

    [Fact] public void BuildDiagnostics_StandingNoteSaysNothingForACleanSolution()
    {
        // A solution that has never been built lists nothing, and that must cost the system prompt nothing at all
        // rather than announcing an empty list on every round of every turn.
        Assert.Equal("", BuildDiagnostics.ToStandingNote(""));
        Assert.Equal("", BuildDiagnostics.ToStandingNote(null));
        Assert.Equal("", BuildDiagnostics.ToStandingNote("message: this error was already fixed"));
    }

    [Fact] public void BuildDiagnostics_StandingNoteShowsTheBeginningOfALongListAndSaysHowMuchIsMissing()
    {
        var entries = new List<string>();
        for (var i = 1; i <= 60; i++) entries.Add("error: CS000" + i + ": failure " + i + " — C:\\sol\\f" + i + ".cs:1");
        var note = BuildDiagnostics.ToStandingNote(string.Join("\n", entries));

        Assert.Contains("60 errors", note, StringComparison.Ordinal);
        Assert.Contains("Showing the first 20 of 60 lines", note, StringComparison.Ordinal);
        Assert.Contains("failure 20", note, StringComparison.Ordinal);
        Assert.DoesNotContain("failure 21", note, StringComparison.Ordinal);
        Assert.DoesNotContain("failure 60", note, StringComparison.Ordinal);
    }

    [Fact] public void BuildDiagnostics_StandingNoteNeverClaimsMoreLinesThanItShows()
    {
        // Found by running the note over a real-shaped list: the first version said "showing the first 20 lines"
        // and then cut the detail at the character cap after nine. A count the model cannot see is worse than no
        // count, because it reads the list as smaller than it is and stops looking. Long lines, so the character
        // cap is the one that binds here and the line count is not.
        var entries = new List<string>();
        for (var i = 1; i <= 40; i++)
            entries.Add("error: CS0246: The type or namespace name 'LiteLLM' could not be found (are you missing a using directive or an assembly reference?) — C:\\sol\\ChatControl.xaml.cs:" + (100 + i));
        var note = BuildDiagnostics.ToStandingNote(string.Join("\n", entries));

        const string marker = "Showing the first ";
        var at = note.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the note did not say how much of the list it was showing");
        var claimed = int.Parse(note.Substring(at + marker.Length).Split(' ')[0]);

        var shown = 0;
        foreach (var line in note.Split('\n')) if (line.StartsWith("error:", StringComparison.Ordinal)) shown++;
        Assert.Equal(claimed, shown);
        Assert.True(claimed < BuildDiagnostics.MaxStandingLines, "this case was meant to be bounded by characters, not by lines");
        // Bounded by whole lines, so nothing was cut in half and there is no truncation marker to explain.
        Assert.DoesNotContain(ChatStateLimits.TruncationMarker, note, StringComparison.Ordinal);
    }

    [Fact] public void BuildDiagnostics_StandingNoteBoundsASingleEnormousEntry()
    {
        // One entry can be arbitrarily long — a template error names every type it tried — so the line limit alone
        // is not a bound. The list has to stay bounded whatever shape it arrives in.
        var note = BuildDiagnostics.ToStandingNote("error: " + new string('x', 9000));

        Assert.Contains(ChatStateLimits.TruncationMarker, note, StringComparison.Ordinal);
        var kept = note.Split('x').Length - 1;
        Assert.True(kept <= BuildDiagnostics.MaxStandingChars, "the note carried " + kept + " characters of the entry");
    }

    [Fact] public void Settings_VerifyBuildAfterEditsDefaultsOnAndSurvivesAnOlderFile()
    {
        Assert.True(new UserSettings().VerifyBuildAfterEdits);

        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            // A settings file written before the setting existed keeps the default rather than reading as off.
            File.WriteAllText(file, "{\"Temperature\":0.5}");
            Assert.True(new SettingsStore(file).Load().VerifyBuildAfterEdits);

            new SettingsStore(file).Save(new UserSettings { VerifyBuildAfterEdits = false });
            Assert.False(new SettingsStore(file).Load().VerifyBuildAfterEdits);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact] public void ChatSessionStore_BoundsAnOversizedBuildDetail()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            session.Turns.Add(new ChatTurnState
            {
                Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "fix the build" } },
                Build = new BuildReport { Errors = 1, Summary = "Build · 1 error", Detail = new string('x', BuildDiagnostics.MaxDetailChars + 500) }
            });
            var store = new ChatSessionStore(file);
            store.Save(new ChatSessionDocument { ActiveSessionId = session.Id, Sessions = new List<ChatSessionState> { session } });

            var build = store.Load().Sessions[0].Turns[0].Build;
            Assert.NotNull(build);
            Assert.Equal(1, build!.Errors);
            Assert.Equal(BuildDiagnostics.MaxDetailChars + ChatStateLimits.TruncationMarker.Length, build.Detail.Length);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
