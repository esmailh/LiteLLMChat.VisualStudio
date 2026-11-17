using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>A request is budgeted before it exists, so the last word on whether it may be sent is the size that is
/// actually serialized. These tests are about that measurement: what gives way when the estimate was a little
/// short, and what the user is told when nothing can.</summary>
public sealed class RequestFittingTests
{
    [Fact]
    public void ARequestInsideTheBudgetIsSentAsItIs()
    {
        var messages = Conversation("small result", rounds: 3);
        var fit = RequestFitting.Fit(messages, Tools(200), 100000);

        Assert.True(fit.Fits);
        Assert.Equal(0, fit.Shortened);
        Assert.Equal(0, fit.Passes);
        Assert.Same(messages, fit.Messages);
    }

    [Fact]
    public void WhatIsOverTheBudgetComesOutOfOldToolResults()
    {
        var messages = Conversation(new string('x', 6000), rounds: 8);
        var fit = RequestFitting.Fit(messages, Tools(200), 20000);

        Assert.True(fit.Fits);
        Assert.True(fit.Shortened > 0);
        // Nothing is dropped, because a result that answers no call is refused by the whole request.
        Assert.Equal(messages.Count, fit.Messages.Count);
        var shortened = fit.Messages.First(m => m.Role == "tool" && m.Content.Contains("dropped to save context"));
        Assert.True(shortened.Content.Length < 6000);
    }

    [Fact]
    public void TheRoundsTheModelIsWorkingOnAreNeverShortened()
    {
        // One round, so every result is current: there is nothing old to give way, and the request is reported
        // as not fitting rather than quietly mangled.
        var messages = Conversation(new string('y', 40000), rounds: 1);
        var fit = RequestFitting.Fit(messages, Tools(100), 20000);

        Assert.False(fit.Fits);
        Assert.Equal(0, fit.Shortened);
        Assert.Equal(40000, fit.Messages.First(m => m.Role == "tool").Content.Length);
    }

    [Fact]
    public void TheToolSchemasAreMeasuredAndCanBeTheDifference()
    {
        // The same transcript and the same budget: only the schemas change. This is what lets a turn continue
        // without MCP rather than end, so the two answers must differ.
        var messages = Conversation(new string('z', 3000), rounds: 6);
        var withSchemas = RequestFitting.Fit(messages, Tools(12000), 20000);
        var withoutSchemas = RequestFitting.Fit(messages, Tools(100), 20000);

        Assert.False(withSchemas.Fits);
        Assert.True(withoutSchemas.Fits);
        Assert.True(withSchemas.ToolChars > withoutSchemas.ToolChars);
    }

    [Fact]
    public void ARefusalSaysWhatTheRequestIsMadeOf()
    {
        var fit = RequestFitting.Fit(Conversation(new string('q', 40000), rounds: 1), Tools(5000), 20000);

        Assert.False(fit.Fits);
        var text = fit.Describe();
        Assert.Contains("20000-character budget", text);
        Assert.Contains("tool schemas", text);
        Assert.Contains("No request was sent", text);
    }

    [Fact]
    public void TwoPassesAreEnoughForTheMeasurementsToAgree()
    {
        // Several old results and a budget that only just covers the conversation: the first pass spends the
        // estimate, the second spends the measurement. Either way the answer is reached inside the pass bound.
        var messages = Conversation(new string('w', 4000), rounds: 12);
        var fit = RequestFitting.Fit(messages, Tools(300), 30000);

        Assert.True(fit.Fits);
        Assert.InRange(fit.Passes, 1, RequestFitting.MaxPasses);
    }

    /// <summary>A transcript of whole rounds — the assistant announcing a call and the tool answering it — so a
    /// trimming pass sees the pairing it has to keep.</summary>
    private static List<ChatMessage> Conversation(string result, int rounds)
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = new string('i', 2000) },
            new ChatMessage { Role = "user", Content = "look at the file" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call" + i, Name = "read_file", Arguments = "{\"path\":\"a.cs\"}" } }
            });
            messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call" + i, ToolName = "read_file", Content = result });
        }
        return messages;
    }

    /// <summary>Tool schemas of a given weight, which is all the fitting cares about.</summary>
    private static List<ToolDefinition> Tools(int payloadChars) => new List<ToolDefinition>
    {
        new ToolDefinition
        {
            Function = new JObject { ["name"] = "read_file", ["description"] = new string('d', payloadChars) }
        }
    };
}
