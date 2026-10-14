using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Folding the older part of an over-budget transcript into a summary. The part worth checking is not the
/// wording of the briefing but that what comes out is still a transcript a server will accept: a prefix replaced
/// by one message is only safe while it ends on a round boundary, and everything here exists to keep it there.</summary>
public sealed class ContextSummaryTests
{
    /// <summary>A transcript shaped the way the loop builds one: a message from the user, then one round per
    /// tool call — the assistant announcing it, then the result answering it.</summary>
    private static List<ChatMessage> Transcript(int rounds, int resultChars = 60)
    {
        var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "the first request" } };
        for (var round = 1; round <= rounds; round++)
        {
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "round " + round,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call" + round, Name = "read_file", Arguments = "{\"path\":\"src/File" + round + ".cs\"}" } }
            });
            messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call" + round, ToolName = "read_file", Content = "marker" + round + " " + new string('x', resultChars) });
        }
        return messages;
    }

    [Fact]
    public void ContextSummary_SaysNothingAboutAShortTranscript()
    {
        // Below the threshold there is nothing to save: a call spent here would make the request no smaller.
        Assert.False(ContextSummary.TryBuildRequest(Transcript(2), 0, null, 2, out _, out _));
        Assert.False(ContextSummary.TryBuildRequest(new List<ChatMessage>(), 0, null, 2, out _, out _));
        Assert.False(ContextSummary.TryBuildRequest(null!, 0, null, 2, out _, out _));
    }

    [Fact]
    public void ContextSummary_ReusesTheSummaryUntilEnoughNewMaterialHasPiledUp()
    {
        var messages = Transcript(12);   // 25 messages; the last two rounds are the ones being worked in
        var end = ContextBudget.RecentFrom(messages, 2);
        Assert.Equal(21, end);

        // Nothing covered yet: everything before the recent rounds is worth folding in.
        Assert.True(ContextSummary.TryBuildRequest(messages, 0, null, 2, out _, out var through));
        Assert.Equal(end, through);

        // Almost everything already covered: rewriting the same briefing every round would cost a call a round.
        Assert.False(ContextSummary.TryBuildRequest(messages, end - 2, "briefing", 2, out _, out _));

        // Enough has happened since to be worth it, and the new request carries the old briefing to update.
        Assert.True(ContextSummary.TryBuildRequest(messages, 8, "the earlier briefing", 2, out var request, out _));
        Assert.Contains("the earlier briefing", request[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSummary_FoldsInWhatWasNotCoveredYet()
    {
        var messages = Transcript(12);
        Assert.True(ContextSummary.TryBuildRequest(messages, 8, null, 2, out var request, out _));

        // The covered part is not sent again — that is what the previous briefing already stands for.
        Assert.DoesNotContain("the first request", request[1].Content, StringComparison.Ordinal);
        Assert.Contains("marker4", request[1].Content, StringComparison.Ordinal);
        Assert.Equal(ContextSummary.Instruction, request[0].Content);
        Assert.Equal("system", request[0].Role);
        Assert.Equal("user", request[1].Role);
    }

    [Fact]
    public void ContextSummary_NamesTheCallsAndTheResultsInTheBriefingRequest()
    {
        Assert.True(ContextSummary.TryBuildRequest(Transcript(12), 0, null, 2, out var request, out _));
        var body = request[1].Content;

        Assert.Contains("read_file", body, StringComparison.Ordinal);
        Assert.Contains("src/File1.cs", body, StringComparison.Ordinal);   // what was asked for
        Assert.Contains("marker1", body, StringComparison.Ordinal);        // what came back
        Assert.Contains("[user]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSummary_StopsOnARoundBoundaryWhateverTheSpanBudgetIs()
    {
        var messages = Transcript(12, 120);
        // The budget is what decides how much fits, so if the cut is allowed to land between a call and its
        // result the merged transcript carries a result whose call has gone — which the server refuses outright.
        foreach (var budget in new[] { 150, 300, 500, 900, 1500, 4000 })
        {
            Assert.True(ContextSummary.TryBuildRequest(messages, 0, null, 2, out _, out var through, budget));
            Assert.True(through > 0, "nothing was covered at a budget of " + budget);
            Assert.NotEqual("tool", messages[through].Role);
            Assert.True(messages[through].Role == "assistant" || messages[through].Role == "user", "boundary landed on " + messages[through].Role);
        }
    }

    [Fact]
    public void ContextSummary_NeverClaimsMoreMessagesThanItActuallySent()
    {
        // A span budget too small for the whole span. The messages the summary claims to stand for have to be the
        // ones the summarizer was shown: claiming one more than was sent would drop it from the transcript without
        // it ever having been written down anywhere.
        var messages = Transcript(12, 400);
        Assert.True(ContextSummary.TryBuildRequest(messages, 0, null, 2, out var request, out var through, 1000));
        var end = ContextBudget.RecentFrom(messages, 2);

        // Every rendered message opens its own line with its role in brackets, so this counts what was sent.
        var rendered = Regex.Matches(request[1].Content, @"^\[", RegexOptions.Multiline).Count;
        Assert.Equal(through, rendered);
        Assert.True(through < end, "this case was meant to be bounded by the span budget, not the round boundary");
        Assert.True(through > 0);
    }

    [Fact]
    public void ContextSummary_MergesThePrefixIntoOneBriefingAndKeepsEveryPairAfterIt()
    {
        var messages = Transcript(12);
        var merged = ContextSummary.Merge(messages, 21, "the briefing");

        Assert.Equal("user", merged[0].Role);
        Assert.Contains("the briefing", merged[0].Content, StringComparison.Ordinal);
        Assert.Equal(messages.Count - 21 + 1, merged.Count);
        Assert.Same(messages[21], merged[1]);   // the tail is handed back, not rebuilt

        // The invariant the whole design is arranged around: a transcript whose calls and results still match.
        // Sanitize would silently repair a broken one, so what is asserted is that it has nothing to repair.
        var sanitized = ChatStateLimits.Sanitize(merged);
        Assert.Equal(merged.Count, sanitized.Count);
        Assert.Equal("user", sanitized[0].Role);

        var announced = new HashSet<string>(sanitized.SelectMany(m => m.ToolCalls).Select(c => c.Id), StringComparer.Ordinal);
        var answered = new HashSet<string>(sanitized.Where(m => m.Role == "tool").Select(m => m.ToolCallId), StringComparer.Ordinal);
        Assert.Equal(announced, answered);   // no call without its result, and no result without its call
    }

    [Fact]
    public void ContextSummary_ReadsAsABriefingRatherThanAsARequest()
    {
        // A briefing that reads like an instruction gets answered instead of continued from.
        var replacement = ContextSummary.Replacement("the user asked for X; src/Foo.cs was changed");

        Assert.Equal("user", replacement.Role);
        Assert.Contains("briefing", replacement.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Foo.cs was changed", replacement.Content, StringComparison.Ordinal);
        Assert.Contains("Continue", replacement.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSummary_StartsOverWhenTheStoredCountNoLongerDescribesThisTranscript()
    {
        // A transcript saved by the store is sliced from the head, so a count from an earlier turn can name
        // messages that are no longer there. Trusting it would silently stop summarizing for good, so the count
        // is dropped and the whole span is folded in again — and the old briefing goes with it, because it is the
        // only record left of the messages the slicing took away.
        var messages = Transcript(12);
        Assert.True(ContextSummary.TryBuildRequest(messages, 900, "a briefing for a longer conversation", 2, out var request, out var through));
        Assert.Equal(ContextBudget.RecentFrom(messages, 2), through);
        Assert.Contains("a briefing for a longer conversation", request[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSummary_GivesTheUtilityCallItsOwnBudgetAndNoTools()
    {
        var settings = ContextSummary.UtilitySettings(new UserSettings { MaxTokens = 8192, Temperature = .7, RequestTimeoutSeconds = 30, EnableToolCalling = true });

        Assert.Equal(ContextSummary.UtilityTokens, settings.MaxTokens);
        Assert.True(settings.MaxTokens < 8192, "the summary would have spent the session's whole budget");
        Assert.Equal(.7, settings.Temperature);
        Assert.Equal(30, settings.RequestTimeoutSeconds);
        Assert.False(settings.EnableToolCalling);
    }

    [Fact]
    public void ContextSummary_ReportsWhatWasReplaced()
    {
        var report = ContextSummary.Report(12, "the briefing text");

        Assert.Equal(12, report.Messages);
        Assert.Equal("Context summarized · 12 messages → summary", report.Summary);
        Assert.Equal("the briefing text", report.Detail);
        Assert.Equal("Context summarized · 1 message → summary", ContextSummary.Label(1));
        // One line, because the row it labels is one line.
        Assert.DoesNotContain("\n", report.Summary);
    }
}
