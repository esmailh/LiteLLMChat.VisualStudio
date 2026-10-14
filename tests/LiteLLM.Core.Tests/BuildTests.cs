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
