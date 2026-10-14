using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The budget that keeps one request sendable. Each case is one that happens in a long turn: a
/// transcript that outgrew the window, a result that has to keep answering the call it belongs to, and a
/// caller's transcript that must come back in full because it is the one that gets saved and shown.</summary>
public sealed class ContextBudgetTests
{
    private const int Body = 2000;

    /// <summary>A transcript of <paramref name="rounds"/> tool rounds, each of them reading a big file.</summary>
    private static List<ChatMessage> Transcript(int rounds)
    {
        var messages = new List<ChatMessage> { User("please refactor src/Foo.cs") };
        for (var round = 1; round <= rounds; round++)
        {
            messages.Add(Assistant("reading", ("c" + round, "read_file", "{\"path\":\"src/File" + round + ".cs\"}")));
            messages.Add(Result("c" + round, "read_file", new string('x', Body)));
        }
        return messages;
    }

    private static ChatMessage User(string content) => new ChatMessage { Role = "user", Content = content };

    private static ChatMessage Assistant(string content, params (string Id, string Name, string Arguments)[] calls) =>
        new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = calls.Select(c => new ToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments }).ToList()
        };

    private static ChatMessage Result(string id, string name, string content) =>
        new ChatMessage { Role = "tool", ToolCallId = id, ToolName = name, Content = content };

    [Fact]
    public void ContextBudget_LeavesATranscriptThatFitsAlone()
    {
        var messages = Transcript(3);
        var trimmed = ContextBudget.Trim(messages, 1000000, 2, out var shortened);
        Assert.Equal(0, shortened);
        Assert.Equal(messages.Count, trimmed.Count);
        Assert.All(trimmed, m => Assert.DoesNotContain("dropped to save context", m.Content));
    }

    [Fact]
    public void ContextBudget_ShortensAnOldResultWithoutBreakingTheCallItAnswers()
    {
        var trimmed = ContextBudget.Trim(Transcript(6), 5000, 2, out var shortened);

        Assert.Equal(4, shortened);   // rounds 1 to 4 are older than the two the model is working in
        var stubbed = trimmed.Single(m => m.ToolCallId == "c1");
        Assert.Equal("tool", stubbed.Role);
        Assert.Equal("read_file", stubbed.ToolName);
        Assert.Contains("src/File1.cs", stubbed.Content);   // a note that cannot name the call is worth little
        Assert.Contains("dropped to save context", stubbed.Content);

        // What comes out is still a transcript an OpenAI-compatible server accepts: the call survives with the
        // note that answers it. This is why the result is shortened rather than dropped.
        var sent = ChatStateLimits.Sanitize(trimmed);
        Assert.Contains(sent, m => m.Role == "assistant" && m.ToolCalls.Any(c => c.Id == "c1"));
        Assert.Contains(sent, m => m.Role == "tool" && m.ToolCallId == "c1");
    }

    [Fact]
    public void ContextBudget_NeverShortensTheRoundsTheModelIsWorkingIn()
    {
        var trimmed = ContextBudget.Trim(Transcript(6), 1000, 2, out _);
        foreach (var id in new[] { "c5", "c6" })
            Assert.Equal(new string('x', Body), trimmed.Single(m => m.ToolCallId == id).Content);
    }

    [Fact]
    public void ContextBudget_KeepsTheUserMessageTheTurnIsAbout()
    {
        var trimmed = ContextBudget.Trim(Transcript(6), 200, 2, out _);
        Assert.Equal("please refactor src/Foo.cs", trimmed.First(m => m.Role == "user").Content);
    }

    [Fact]
    public void ContextBudget_LeavesTheCallersTranscriptInFull()
    {
        var messages = Transcript(6);
        ContextBudget.Trim(messages, 1000, 2, out _);
        Assert.All(messages.Where(m => m.Role == "tool"), m => Assert.Equal(new string('x', Body), m.Content));
    }

    [Fact]
    public void ContextBudget_ShortensNothingWhenEveryRoundIsRecent()
    {
        // Nothing is old enough to be protected *from*: the transcript holds fewer rounds than the keep count,
        // so the whole of it is what the model is working in and none of it may be shortened.
        ContextBudget.Trim(Transcript(2), 10, 2, out var shortened);
        Assert.Equal(0, shortened);
    }

    [Fact]
    public void ContextBudget_NamesTheToolWhenItsArgumentsAreGone()
    {
        // A result whose call is not in the transcript at all - a hand-edited session, or a round cut in half.
        // The note still has to say what was dropped, so it falls back to the tool's name.
        var messages = new List<ChatMessage> { User("q"), Result("gone", "search_files", new string('y', 5000)) };
        var trimmed = ContextBudget.Trim(messages, 100, 0, out var shortened);

        Assert.Equal(1, shortened);
        Assert.Contains("search_files", trimmed[1].Content);
        Assert.Contains("dropped to save context", trimmed[1].Content);
    }
}
