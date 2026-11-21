using System;
using System.Collections.Generic;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>What the model is told about a shell. The start text is the only thing standing in for an exit code, so
/// it has to carry the id, the pid and the log; the completion block is a message of the extension's own, so it has
/// to be recognizable as one and readable apart from the conversation.</summary>
public sealed class ShellNoticeTests
{
    [Fact]
    public void StartedNamesTheShellItsProcessAndItsLog()
    {
        var start = new ShellStart
        {
            Id = "shell-2",
            ProcessId = 4242,
            LogPath = @"C:\Users\me\AppData\Local\LiteLLM.VisualStudio\shells\20260101-101010-abcdef\shell-2.log",
            Shell = CommandShell.PowerShell,
            Command = "dotnet run",
            WorkingDirectory = @"F:\work\api"
        };

        var text = ShellNotice.Started(start);

        Assert.Contains("shell-2", text, StringComparison.Ordinal);
        Assert.Contains("4242", text, StringComparison.Ordinal);
        Assert.Contains(start.LogPath, text, StringComparison.Ordinal);
        Assert.Contains("read_file", text, StringComparison.Ordinal);
        Assert.Contains("taskkill /PID 4242 /T /F", text, StringComparison.Ordinal);
        Assert.Contains(@"F:\work\api", text, StringComparison.Ordinal);
        // A model told "started" without being told the log may be incomplete waits on a file it never re-reads.
        Assert.Contains("still running", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedCarriesTheExitCodeAndTheDurationAsAttributes()
    {
        var completion = new ShellCompletion
        {
            Id = "shell-1",
            ProcessId = 77,
            ExitCode = 3,
            Duration = TimeSpan.FromSeconds(12.5),
            LogPath = @"C:\logs\shell-1.log",
            Shell = CommandShell.Cmd,
            Owner = "chat"
        };

        var text = ShellNotice.Completed(completion);

        Assert.Contains("id=\"shell-1\"", text, StringComparison.Ordinal);
        Assert.Contains("pid=\"77\"", text, StringComparison.Ordinal);
        Assert.Contains("state=\"completed\"", text, StringComparison.Ordinal);
        Assert.Contains("exit=\"3\"", text, StringComparison.Ordinal);
        Assert.Contains("after=\"12.5s\"", text, StringComparison.Ordinal);
        Assert.Contains(@"log=""C:\logs\shell-1.log""", text, StringComparison.Ordinal);
        Assert.Contains("shell=\"Cmd\"", text, StringComparison.Ordinal);
    }

    /// <summary>A shell that ended by a signal reports no exit code. Saying "unknown" is the honest reading; a
    /// zero would be a claim the shell exited cleanly.</summary>
    [Fact]
    public void AnEndedShellWithoutAnExitCodeSaysUnknown()
    {
        var text = ShellNotice.Completed(new ShellCompletion { Id = "shell-1", ProcessId = 1, ExitCode = null });
        Assert.Contains("exit=\"unknown\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBlockIsNothingAtAll()
    {
        Assert.Equal("", ShellNotice.Block(null));
        Assert.Equal("", ShellNotice.Block(new List<ShellCompletion>()));
    }

    [Fact]
    public void TheBlockWrapsEveryCompletionInOneElement()
    {
        var block = ShellNotice.Block(new List<ShellCompletion>
        {
            new ShellCompletion { Id = "shell-1", ProcessId = 1, ExitCode = 0, LogPath = @"C:\logs\a.log" },
            new ShellCompletion { Id = "shell-2", ProcessId = 2, ExitCode = 1, LogPath = @"C:\logs\b.log" }
        });

        Assert.StartsWith("<shells>", block, StringComparison.Ordinal);
        Assert.EndsWith("</shells>", block, StringComparison.Ordinal);
        Assert.Contains("shell-1", block, StringComparison.Ordinal);
        Assert.Contains("shell-2", block, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(block, "state=\"completed\""));
    }

    /// <summary>The block goes into the system prompt, which is not markup-escaped anywhere else. A log path with an
    /// ampersand in it — a user folder named "R&D" — must not turn the element into something else.</summary>
    [Fact]
    public void APathWithMarkupCharactersIsEscaped()
    {
        var text = ShellNotice.Completed(new ShellCompletion
        {
            Id = "shell-1",
            ProcessId = 1,
            ExitCode = 0,
            LogPath = @"C:\R&D\<logs>\""quoted"".log"
        });

        Assert.Contains("R&amp;D", text, StringComparison.Ordinal);
        Assert.Contains("&lt;logs&gt;", text, StringComparison.Ordinal);
        Assert.Contains("&quot;quoted&quot;", text, StringComparison.Ordinal);
    }

    /// <summary>The prompt block is the extension's own report and the model is told so, which is what keeps it
    /// from being read as something the user asked for.</summary>
    [Fact]
    public void ThePromptBlockIsLabeledAsTheExtensionsReport()
    {
        var prompt = AgentPrompt.Instructions("agent", true, true, true, shellNotices: "<shells>\n<shell id=\"shell-1\" state=\"completed\" exit=\"0\" />\n</shells>");

        Assert.Contains("reported by the extension, not by the user", prompt, StringComparison.Ordinal);
        Assert.Contains("state=\"completed\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoShellNotesChangesNothingInTheInstructions()
    {
        Assert.Equal(AgentPrompt.Instructions("agent", true, true, true),
            AgentPrompt.Instructions("agent", true, true, true, shellNotices: ""));
        Assert.Equal(AgentPrompt.Instructions("agent", true, true, true),
            AgentPrompt.Instructions("agent", true, true, true, shellNotices: null));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
