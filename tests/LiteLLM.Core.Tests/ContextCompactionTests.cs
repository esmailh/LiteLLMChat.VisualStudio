using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Compacting a conversation the way opencode does: a token ceiling decides when, a template decides what
/// the checkpoint says, a verbatim tail stays beside it, and an existing checkpoint is updated rather than replaced.
///
/// What is checked here is not the wording the model is asked for but the three things that decide whether the
/// result is still a transcript a server will accept: the cut never lands inside a tool round, the boundary never
/// claims less than the summarizer was shown, and the request being answered is never lost.</summary>
public sealed class ContextCompactionTests
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

    /// <summary>A multi-turn conversation: each turn is the user asking, the assistant calling a tool, and the
    /// result answering it. Used where a flattened span has more than one user message to show.</summary>
    private static List<ChatMessage> Conversation(int turns, int resultChars = 400)
    {
        var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "request 1" } };
        for (var turn = 1; turn <= turns; turn++)
        {
            if (turn > 1) messages.Add(new ChatMessage { Role = "user", Content = "request " + turn });
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "working on " + turn,
                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call" + turn, Name = "read_file", Arguments = "{\"path\":\"src/File" + turn + ".cs\"}" } }
            });
            messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call" + turn, ToolName = "read_file", Content = "marker" + turn + " " + new string('x', resultChars) });
        }
        return messages;
    }

    private static string Folded(List<ChatMessage> request) =>
        request.First(m => m.Content.StartsWith("Conversation to fold", StringComparison.Ordinal)).Content;

    private static List<ChatMessage> Request(int rounds = 12, int covered = 0, string? previous = null)
    {
        var messages = Transcript(rounds);
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);
        Assert.True(ContextCompaction.TryBuildRequest(messages, covered, keepFrom, previous, out var request, out _));
        return request;
    }

    // ---------------------------------------------------------------------------------------------------------
    // When
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ContextCompaction_CountsTwoCharactersPerToken()
    {
        Assert.Equal(0, ContextCompaction.Tokens(0));
        Assert.Equal(1, ContextCompaction.Tokens(1));
        Assert.Equal(1, ContextCompaction.Tokens(2));
        Assert.Equal(2, ContextCompaction.Tokens(3));
        Assert.Equal(1000, ContextCompaction.Chars(500));
        Assert.Equal(30000, ContextCompaction.Chars(ContextCompaction.DefaultKeepTokens));
        Assert.Equal(ContextCompaction.DefaultKeepTokens, ContextCompaction.Tokens(30000));
    }

    [Fact]
    public void ContextCompaction_LeavesRoomBelowTheWindowItKnows()
    {
        // An unknown window cannot be over: only the provider's own rejection can then limit the request.
        Assert.Equal(int.MaxValue, ContextCompaction.Ceiling(0));

        // opencode's default buffer is the larger of 15% of the window and 16 000 tokens.
        Assert.Equal(128000 - 19200, ContextCompaction.Ceiling(128000));
        Assert.Equal(32000 - 16000, ContextCompaction.Ceiling(32000));
        Assert.Equal(20000 - 3000, ContextCompaction.Ceiling(20000));

        // A configured buffer is used when it leaves room, and ignored when it would make the request impossible.
        Assert.Equal(124000, ContextCompaction.Ceiling(128000, 4000));
        Assert.Equal(128000 - 19200, ContextCompaction.Ceiling(128000, 200000));
        Assert.Equal(1000 - 150, ContextCompaction.Ceiling(1000, 5000));
    }

    [Fact]
    public void ContextCompaction_EstimatesFromTheMeasuredCountAndTheGrowthSince()
    {
        // No measurement: the whole request, at two characters per token.
        Assert.Equal(500, ContextCompaction.Estimate(0, 0, 1000));

        // A measurement and no growth is the measurement. Growth is what was added since it was taken.
        Assert.Equal(9000, ContextCompaction.Estimate(9000, 1000, 1000));
        Assert.Equal(9500, ContextCompaction.Estimate(9000, 1000, 2000));

        // A request that shrank — a fold, fewer tools — is never estimated below what the server already counted.
        Assert.Equal(100, ContextCompaction.Estimate(100, 5000, 1000));

        // A nonsensical size is clamped rather than wrapped into a number that would refuse the turn.
        Assert.Equal(int.MaxValue, ContextCompaction.Estimate(int.MaxValue, 0, 100));
        Assert.Equal(0, ContextCompaction.Estimate(0, 0, -5));
    }

    [Fact]
    public void ContextCompaction_IsDueOnlyAtTheCeiling()
    {
        Assert.True(ContextCompaction.Due(108800, 108800));
        Assert.False(ContextCompaction.Due(108799, 108800));
        // No ceiling means nothing to compare against, and saying "due" would spend a call on every turn.
        Assert.False(ContextCompaction.Due(1, 0));
    }

    [Fact]
    public void ContextCompaction_AimsFurtherBelowTheSizeThatWasRefusedEachTime()
    {
        Assert.Equal(85000, ContextCompaction.OverflowBudget(100000, 108800, 0));
        Assert.Equal(70000, ContextCompaction.OverflowBudget(100000, 108800, 1));
        Assert.Equal(50000, ContextCompaction.OverflowBudget(100000, 108800, 2));
        Assert.Equal(35000, ContextCompaction.OverflowBudget(100000, 108800, 3));
        // Past the last step the smallest one keeps being used: it is the only answer left, and it is not a crash.
        Assert.Equal(35000, ContextCompaction.OverflowBudget(100000, 108800, 9));

        // Never above the ceiling, and a request already under it is left alone.
        Assert.Equal(50000, ContextCompaction.OverflowBudget(100000, 50000, 0));
        Assert.Equal(85000, ContextCompaction.OverflowBudget(100000, int.MaxValue, 0));
    }

    [Fact]
    public void ContextCompaction_ClampsAHandEditedKeep()
    {
        Assert.Equal(0, ContextCompaction.EffectiveKeep(0));
        Assert.Equal(ContextCompaction.DefaultKeepTokens, ContextCompaction.EffectiveKeep(-5));
        Assert.Equal(200000, ContextCompaction.EffectiveKeep(300000));
        Assert.Equal(15000, ContextCompaction.EffectiveKeep(15000));

        // The overflow tail has to leave room for the checkpoint and the instructions beside it.
        Assert.Equal(15000, ContextCompaction.OverflowKeep(0, 15000));
        Assert.Equal(10000, ContextCompaction.OverflowKeep(30000, 15000));
        Assert.Equal(1000, ContextCompaction.OverflowKeep(3000, 15000));
        Assert.Equal(2000, ContextCompaction.OverflowKeep(60000, 2000));
    }

    // ---------------------------------------------------------------------------------------------------------
    // What stays
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ContextCompaction_KeepsTheNewestTailAndNeverOpensOnAResult()
    {
        var messages = Transcript(6);

        // The whole conversation fits inside the default keep, so there is nothing to compact.
        Assert.Equal(0, ContextCompaction.KeepFrom(messages, ContextCompaction.DefaultKeepTokens));
        Assert.Equal(0, ContextCompaction.KeepFrom(new List<ChatMessage>(), 100));
        Assert.Equal(0, ContextCompaction.KeepFrom(null!, 100));

        // A small keep starts somewhere in the transcript, and a cut that lands on a result moves back to the
        // assistant message that announced the call it answers.
        foreach (var keep in new[] { 1, 5, 20, 60, 120 })
        {
            var from = ContextCompaction.KeepFrom(messages, keep);
            Assert.True(from > 0, "nothing was kept at a keep of " + keep);
            Assert.True(from < messages.Count);
            Assert.NotEqual("tool", messages[from].Role);
            Assert.True(messages[from].Role == "assistant" || messages[from].Role == "user",
                "the tail opened on " + messages[from].Role);
        }

        // Zero keeps nothing verbatim: every message is folded, and the request that follows is the checkpoint.
        Assert.Equal(messages.Count, ContextCompaction.KeepFrom(messages, 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // What is written
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ContextCompaction_SaysNothingWhenThereIsNothingNewToFold()
    {
        var messages = Transcript(12);
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);

        Assert.False(ContextCompaction.TryBuildRequest(new List<ChatMessage>(), 0, 1, null, out _, out _));
        Assert.False(ContextCompaction.TryBuildRequest(null!, 0, 1, null, out _, out _));
        // The checkpoint already stands for everything that is not in the tail.
        Assert.False(ContextCompaction.TryBuildRequest(messages, keepFrom, keepFrom, null, out _, out _));
    }

    [Fact]
    public void ContextCompaction_FoldsOnlyWhatTheCheckpointDoesNotCover()
    {
        var messages = Transcript(12);
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);
        Assert.True(ContextCompaction.TryBuildRequest(messages, 8, keepFrom, null, out var request, out var through));
        var body = Folded(request);

        // The covered part is not sent again — the checkpoint in hand already stands for it.
        Assert.DoesNotContain("the first request", body, StringComparison.Ordinal);
        Assert.DoesNotContain("marker3", body, StringComparison.Ordinal);
        Assert.Contains("marker4", body, StringComparison.Ordinal);
        Assert.Equal(keepFrom, through);
        Assert.Equal("system", request[0].Role);
        Assert.Equal("user", request[1].Role);
    }

    [Fact]
    public void ContextCompaction_AsksForAnUpdateOfTheCheckpointInHand()
    {
        var request = Request(previous: "## Objective\n- finish the release");

        // The checkpoint takes the place of the messages it stands for in the request that rewrites it, exactly as
        // opencode rebuilds its history from the latest one.
        Assert.StartsWith("<conversation-checkpoint>", request[1].Content, StringComparison.Ordinal);
        Assert.Contains("## Objective", request[1].Content, StringComparison.Ordinal);
        Assert.Contains("Update the existing checkpoint", request[request.Count - 1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("You MUST summarize", request[request.Count - 1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCompaction_RewritesAProseBriefingInTheCurrentSections()
    {
        // A conversation written by an earlier build carries a prose briefing. Updating it must not carry the prose
        // forward, and must keep the requirements it is the only record of.
        var request = Request(previous: "The user asked for the release notes to be finished.");

        Assert.Contains("was written as prose", request[request.Count - 1].Content, StringComparison.Ordinal);
        Assert.Contains("Keep its requirements, decisions, and open questions", request[request.Count - 1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCompaction_NamesTheCallsAndTheResultsInTheRequest()
    {
        var body = Folded(Request());

        Assert.Contains("read_file", body, StringComparison.Ordinal);
        Assert.Contains("src/File1.cs", body, StringComparison.Ordinal);   // what was asked for
        Assert.Contains("marker1", body, StringComparison.Ordinal);        // what came back
        Assert.Contains("[user]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCompaction_FoldsASpanThatFitsWholeAndNamesEveryMessage()
    {
        var messages = Conversation(12, 3000);
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);
        // The span is bigger than the conversation should ever have to be, so outputs are replaced by their length;
        // every message still gets its own line, and the boundary is the whole span.
        Assert.True(ContextCompaction.TryBuildRequest(messages, 0, keepFrom, null, out var request, out var through, 8000));
        Assert.Equal(keepFrom, through);

        var rendered = Regex.Matches(Folded(request), @"^\[", RegexOptions.Multiline).Count;
        Assert.Equal(through, rendered);
    }

    [Fact]
    public void ContextCompaction_CutsASpanThatIsTooBigEvenFlattened()
    {
        // The flattest level still has to fit: a conversation whose user messages alone exceed the span budget is
        // folded as far as the budget allows, and the rest waits for the next round.
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 500; i++)
        {
            messages.Add(new ChatMessage { Role = "user", Content = "request " + i });
            messages.Add(new ChatMessage { Role = "assistant", Content = "answer " + i });
        }
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);
        Assert.Equal(messages.Count, keepFrom);

        Assert.True(ContextCompaction.TryBuildRequest(messages, 0, keepFrom, null, out var request, out var through, 8000));
        var body = Folded(request);

        Assert.True(through > 0);
        Assert.True(through < keepFrom, "the span was not bounded by the budget");
        Assert.NotEqual("tool", messages[through].Role);
        Assert.Contains("request 0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("request 499", body, StringComparison.Ordinal);
        // Whatever was claimed, no claimed message was left out of the request as a whole: the checkpoint may
        // flatten a span — that is what its levels are for — but it may not sit in front of a message the
        // summarizer was told to cover and never saw.
        var rendered = Regex.Matches(body, @"^\[", RegexOptions.Multiline).Count;
        Assert.True(rendered <= through, rendered + " messages were sent but " + through + " were claimed");
    }

    [Fact]
    public void ContextCompaction_StartsOverWhenTheBoundaryNoLongerDescribesThisTranscript()
    {
        // A transcript saved by the store is sliced from the head, so a boundary from an earlier turn can name
        // messages that are no longer there. Trusting it would skip the messages it never stood for, so it is
        // dropped and the whole span is folded in again — and the old checkpoint goes with it, because it is the
        // only record left of the messages the slicing took away.
        var messages = Transcript(12);
        var keepFrom = ContextCompaction.KeepFrom(messages, 0);
        Assert.True(ContextCompaction.TryBuildRequest(messages, 900, keepFrom, "a checkpoint for a longer conversation",
            out var request, out var through));

        Assert.Equal(keepFrom, through);
        Assert.Contains("a checkpoint for a longer conversation", request[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCompaction_ReplacesThePrefixAndKeepsEveryPairAfterIt()
    {
        var messages = Transcript(12);
        var merged = ContextCompaction.Merge(messages, 21, "the checkpoint");

        Assert.Equal("user", merged[0].Role);
        Assert.Contains("<conversation-checkpoint>", merged[0].Content, StringComparison.Ordinal);
        Assert.Contains("the checkpoint", merged[0].Content, StringComparison.Ordinal);
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
    public void ContextCompaction_KeepsTheRequestEvenWhenTheCheckpointLeavesItOut()
    {
        // The failure this guards: a checkpoint that never mentions what the user asked for, and a request that was
        // folded into it, leave the next round with an attached file and no question. The request has to be handed
        // back verbatim, in front of the rounds the fold kept.
        var messages = Transcript(12);
        var merged = ContextCompaction.Merge(messages, 21, "the assistant was investigating the configuration", messages[0]);

        Assert.Equal("user", merged[0].Role);
        Assert.Same(messages[0], merged[1]);
        Assert.Contains("the first request", merged[1].Content, StringComparison.Ordinal);
        Assert.Same(messages[21], merged[2]);

        // Two user messages in a row are valid, and the opening message is still the checkpoint.
        var sanitized = ChatStateLimits.Sanitize(merged);
        Assert.Equal(merged.Count, sanitized.Count);
    }

    [Fact]
    public void ContextCompaction_DoesNotRepeatARequestItDidNotFold()
    {
        var messages = Transcript(12);
        var merged = ContextCompaction.Merge(messages, 21, "the checkpoint", messages[21]);

        Assert.Equal(messages.Count - 21 + 1, merged.Count);
        Assert.Same(messages[21], merged[1]);
        Assert.Equal(1, merged.Count(m => ReferenceEquals(m, messages[21])));
    }

    [Fact]
    public void ContextCompaction_PinsTheLastThingTheUserTyped()
    {
        var messages = Transcript(2);
        Assert.Same(messages[0], ContextCompaction.RequestMessage(messages));

        var second = new ChatMessage { Role = "user", Content = "the second request" };
        messages.Add(second);
        Assert.Same(second, ContextCompaction.RequestMessage(messages));

        Assert.Null(ContextCompaction.RequestMessage(new List<ChatMessage> { new ChatMessage { Role = "assistant" } }));
        Assert.Null(ContextCompaction.RequestMessage(null));
        Assert.Null(ContextCompaction.RequestMessage(new List<ChatMessage>()));
    }

    [Fact]
    public void ContextCompaction_ReadsAsEstablishedContextRatherThanAsARequest()
    {
        // A checkpoint that reads like an instruction gets answered instead of continued from.
        var replacement = ContextCompaction.Replacement("the user asked for X; src/Foo.cs was changed");

        Assert.Equal("user", replacement.Role);
        Assert.Contains("<conversation-checkpoint>", replacement.Content, StringComparison.Ordinal);
        Assert.Contains("the user asked for X; src/Foo.cs was changed", replacement.Content, StringComparison.Ordinal);
        Assert.Contains("stands in for the earlier conversation", replacement.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Here is a summary of the conversation: it was about a release.", false)]
    [InlineData("## Objective\n- finish the release", true)]
    [InlineData("  ## Objective  \n- finish the release", true)]
    [InlineData("### Completed\n- the build passes", true)]
    public void ContextCompaction_AcceptsOnlyATemplateAsACheckpoint(string? text, bool filled) =>
        Assert.Equal(filled, ContextCompaction.Filled(text));

    [Fact]
    public void ContextCompaction_GivesTheUtilityCallItsOwnBudgetAndNoTools()
    {
        var settings = ContextCompaction.UtilitySettings(new UserSettings { MaxTokens = 8192, Temperature = .7, RequestTimeoutSeconds = 30, EnableToolCalling = true });

        Assert.Equal(ContextCompaction.UtilityTokens, settings.MaxTokens);
        Assert.True(settings.MaxTokens < 8192, "the checkpoint would have spent the session's whole budget");
        Assert.Equal(.7, settings.Temperature);
        Assert.Equal(30, settings.RequestTimeoutSeconds);
        Assert.False(settings.EnableToolCalling);
        // A null source is the settings of a build that had none, not a crash.
        Assert.Equal(.2, ContextCompaction.UtilitySettings(null).Temperature);
    }

    // ---------------------------------------------------------------------------------------------------------
    // What is said
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ContextCompaction_DescribesWhatTheFoldedPartContained()
    {
        var messages = Transcript(3);
        var text = ContextCompaction.Describe(messages, 3);   // the user's message, then one whole tool round

        Assert.Contains("first 3 messages", text, StringComparison.Ordinal);
        Assert.Contains("1 message from you", text, StringComparison.Ordinal);
        Assert.Contains("1 reply", text, StringComparison.Ordinal);
        Assert.Contains("1 tool result", text, StringComparison.Ordinal);
        Assert.Contains("read_file", text, StringComparison.Ordinal);
        // One line, because the row it fills is a caption under an expander header.
        Assert.DoesNotContain("\n", text);

        // Nothing was folded, so there is nothing to explain — and a null transcript must not throw.
        Assert.Equal("", ContextCompaction.Describe(messages, 0));
        Assert.Equal("", ContextCompaction.Describe(null, 3));
    }

    [Fact]
    public void ContextCompaction_SaysWhichOfTheThreeDecidedIt()
    {
        var messages = Transcript(2);

        Assert.Contains("Automatic compaction", ContextCompaction.Describe(messages, 3), StringComparison.Ordinal);
        Assert.Contains("rejected the request as too long",
            ContextCompaction.Describe(messages, 3, CompactionTrigger.Overflow), StringComparison.Ordinal);
        Assert.Contains("Compaction folded", ContextCompaction.Describe(messages, 3, CompactionTrigger.Manual), StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCompaction_KeepsTheCoverageSentenceWithTheReport()
    {
        var report = ContextCompaction.Report(4, "the checkpoint", "what it covered");

        Assert.Equal("what it covered", report.Covered);
        Assert.Equal("Auto", report.Trigger);
        Assert.Equal("Overflow", ContextCompaction.Report(4, "the checkpoint", "", CompactionTrigger.Overflow).Trigger);
        // A report built without one is still valid: a row written by an older stored build has no sentence to say.
        Assert.Equal("", ContextCompaction.Report(4, "the checkpoint").Covered);
    }

    [Fact]
    public void ContextCompaction_ReportsWhatWasReplacedAndWhy()
    {
        var report = ContextCompaction.Report(12, "the checkpoint text", "", CompactionTrigger.Overflow);

        Assert.Equal(12, report.Messages);
        Assert.Equal("Compacted · the request was too long · 12 messages → checkpoint", report.Summary);
        Assert.Equal("the checkpoint text", report.Detail);
        Assert.Equal("Compacted · 1 message → checkpoint", ContextCompaction.Label(1));
        Assert.Equal("Compacted by hand · 2 messages → checkpoint", ContextCompaction.Label(2, CompactionTrigger.Manual));
        // One line, because the row it labels is one line.
        Assert.DoesNotContain("\n", report.Summary);
    }
}
