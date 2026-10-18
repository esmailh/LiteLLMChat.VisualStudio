using System;
using System.Collections.Generic;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class AgentPromptMcpTests
{
    private const string Selection = "MCP tools selected for this turn\nThe user selected search_customers.";

    [Fact]
    public void Build_LeavesTheSystemMessageAloneWhenNothingWasSelected()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hello" } };

        var built = AgentPrompt.Build(history, null, "agent", true, true, false, null, null, null, null);

        Assert.Equal(AgentPrompt.Instructions("agent", true, true, false), built[0].Content);
    }

    [Fact]
    public void Instructions_AddsTheSelectionAfterTheExtensionsOwnRules()
    {
        var text = AgentPrompt.Instructions("agent", true, true, false, null, null, null, Selection);

        Assert.Contains(Selection, text, StringComparison.Ordinal);
        Assert.True(text.IndexOf(Selection, StringComparison.Ordinal)
            > text.IndexOf("Editing files", StringComparison.Ordinal));
    }

    [Fact]
    public void Instructions_KeepsTheSelectionBeforeTheRepositoryAndServerGuidance()
    {
        // A repository file or a server's text may describe the work; neither may redefine which tools the turn has.
        var text = AgentPrompt.Instructions("agent", true, true, false,
            "From AGENTS.md:\nUse MSBuild.", null, "Files server: search finds files.", Selection);

        var selection = text.IndexOf(Selection, StringComparison.Ordinal);
        Assert.True(selection > 0);
        Assert.True(selection < text.IndexOf("Repository instructions", StringComparison.Ordinal));
        Assert.True(selection < text.IndexOf("MCP server guidance", StringComparison.Ordinal));
    }

    [Fact]
    public void Instructions_PutsTheCurrentErrorsAfterTheSelection()
    {
        var text = AgentPrompt.Instructions("agent", true, true, false, null,
            "error: CS0246", null, Selection);

        Assert.True(text.IndexOf(Selection, StringComparison.Ordinal)
            < text.IndexOf("Current errors in the solution", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PutsTheSelectionInTheSystemMessageAndNeverInTheTranscript()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "run it" } };

        var built = AgentPrompt.Build(history, null, "agent", true, true, false, null, null, null, Selection);

        Assert.Equal(2, built.Count);
        Assert.Contains(Selection, built[0].Content, StringComparison.Ordinal);
        Assert.Equal("run it", built[1].Content);
    }

    [Fact]
    public void Build_SuppliesTheSelectionToTheModelFromTheTurnOnly()
    {
        var history = new List<ChatMessage>
        {
            new ChatMessage { Role = "user", Content = "earlier" },
            new ChatMessage { Role = "assistant", Content = "done" },
            new ChatMessage { Role = "user", Content = "now" }
        };

        var built = AgentPrompt.Build(history, null, "agent", true, true, false, null, null, null, Selection);

        Assert.Contains(Selection, built[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(Selection, built[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(Selection, built[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseInstructions_SayOnlyAToolResultProvesACallRan()
    {
        var text = AgentPrompt.Instructions(true, true);

        Assert.Contains("only proof that a tool ran", text, StringComparison.Ordinal);
        Assert.Contains("do not substitute a different tool", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseInstructions_DoNotClaimToolResultsWhenNoToolIsOffered()
    {
        var text = AgentPrompt.Instructions(false, false);

        Assert.DoesNotContain("only proof that a tool ran", text, StringComparison.Ordinal);
    }
}
