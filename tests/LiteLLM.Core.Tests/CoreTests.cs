using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class CoreTests
{
    [Fact] public async Task SseParser_HandlesCommentsMultilineAndFinalEvent()
    {
        var values = new List<string>(); var input = ": keepalive\r\ndata: first\r\ndata: second\r\n\r\ndata: last";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(input))) await SseParser.ReadAsync(stream, x => { values.Add(x); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { "first\nsecond", "last" }, values);
    }
    [Fact] public void StreamParser_AccumulatesToolFragmentsAndReasoning()
    {
        var calls = new Dictionary<int, ToolCall>();
        var one = ResponseParser.ParseStreamChunk("{\"choices\":[{\"delta\":{\"reasoning_content\":\"think\",\"tool_calls\":[{\"index\":0,\"id\":\"call_\",\"function\":{\"name\":\"get_\",\"arguments\":\"{\\\"x\\\":\"}}]}}]}", calls);
        var two = ResponseParser.ParseStreamChunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"1\",\"function\":{\"name\":\"file\",\"arguments\":\"1}\"}}]},\"finish_reason\":\"tool_calls\"}]}", calls);
        Assert.Equal("think", one.ReasoningDelta); Assert.Equal("call_1", calls[0].Id); Assert.Equal("get_file", calls[0].Name); Assert.Equal("{\"x\":1}", calls[0].Arguments); Assert.Equal("tool_calls", two.FinishReason);
    }
    [Fact] public void CompletionParser_ReadsContentReasoningAndTools()
    {
        var result = ResponseParser.ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"answer\",\"reasoning_content\":\"why\",\"tool_calls\":[{\"id\":\"a\",\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]}}]}");
        Assert.Equal("answer", result.Content); Assert.Equal("why", result.Reasoning); Assert.Equal("read", Assert.Single(result.ToolCalls).Name);
    }
    [Fact] public void Catalog_PreservesDuplicateModelNamesAcrossServers()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var catalog = new ModelCatalog(); catalog.Put(a, new[] { new ModelInfo { Id = "gpt", ServerId = a, ServerName = "A" } }); catalog.Put(b, new[] { new ModelInfo { Id = "gpt", ServerId = b, ServerName = "B" } });
        var models = catalog.GetAll(new[] { a, b }); Assert.Equal(2, models.Count); Assert.NotEqual(models[0].Key, models[1].Key); Assert.Equal("B", catalog.Match(models[1].Key)?.ServerName);
    }
    [Fact] public void SettingsStore_RoundTripsWithoutPlaintextKey()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json"); try { var protectedKey = new ApiKeyProtector().Protect("secret-value"); var settings = new UserSettings(); settings.Servers.Add(new ServerConfiguration { ProtectedApiKey = protectedKey }); var store = new SettingsStore(file); store.Save(settings); Assert.DoesNotContain("secret-value", File.ReadAllText(file)); Assert.Equal(protectedKey, store.Load().Servers[0].ProtectedApiKey); } finally { if (File.Exists(file)) File.Delete(file); }
    }
    [Fact] public void ChatSessionStore_NormalizesInFlightTurnsAndBoundsHistory()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            session.Turns.Add(new ChatTurnState { Status = ChatTurnStatus.Streaming, Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hello" } } });
            var document = new ChatSessionDocument { ActiveSessionId = session.Id, Sessions = new List<ChatSessionState> { session } };
            var store = new ChatSessionStore(file); store.Save(document); var loaded = store.Load();
            Assert.Equal(ChatTurnStatus.Cancelled, loaded.Sessions[0].Turns[0].Status);
            Assert.Contains("previous Visual Studio session", loaded.Sessions[0].Turns[0].Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
    [Fact] public void ChatSessionStore_DeleteMovesTheActivePointerAndNeverEmptiesTheList()
    {
        var older = ChatSessionStore.NewSession(); var newer = ChatSessionStore.NewSession();
        older.Turns.Add(new ChatTurnState { Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "the older one" } } });
        newer.Turns.Add(new ChatTurnState { Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "the newer one" } } });
        var document = new ChatSessionDocument { ActiveSessionId = older.Id, Sessions = new List<ChatSessionState> { older, newer } };

        ChatSessionStore.Delete(document, older.Id);
        Assert.Equal(newer.Id, Assert.Single(document.Sessions).Id);
        Assert.Equal(newer.Id, document.ActiveSessionId);   // the pointer follows the conversation that is left

        // Deleting the last one is a conversation with nothing in it, not a document with no conversations in it: the
        // chat pane always has something to render, and the store always has something to save.
        ChatSessionStore.Delete(document, newer.Id);
        Assert.Equal("New chat", Assert.Single(document.Sessions).Title);
        Assert.Equal(document.Sessions[0].Id, document.ActiveSessionId);
        Assert.Empty(document.Sessions[0].Turns);

        // A conversation that is not there is not an error, and is not allowed to take the list with it.
        ChatSessionStore.Delete(document, Guid.NewGuid());
        Assert.Single(document.Sessions);
    }

    [Fact]
    public void ChatSessionStore_TitleIsOneLineHoweverItWasStored()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            // Exactly what an older build wrote to disk: 42 characters of first message, then Clip's truncation marker,
            // whose opening line break is what put a long conversation on two rows of the history drop-down. The store has
            // to repair it on load rather than pass it through, because nothing downstream can tell it apart from a title
            // someone meant to have two lines.
            var stored = new string('a', 42) + "\n… [history truncated]";
            Assert.Equal(64, stored.Length);
            var session = new ChatSessionState { Title = stored };
            var document = new ChatSessionDocument { ActiveSessionId = session.Id, Sessions = new List<ChatSessionState> { session } };
            new ChatSessionStore(file).Save(document);

            var title = Assert.Single(new ChatSessionStore(file).Load().Sessions).Title;
            Assert.Equal(new string('a', 42), title);   // the 42 characters, with the marker taken off and the break closed
            Assert.DoesNotContain("history truncated", title, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', title);
            Assert.DoesNotContain('\r', title);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Theory]
    [InlineData("first line\nsecond line", "first line second line")]   // a first message written in paragraphs
    [InlineData("  padded\t\tand  spaced  ", "padded and spaced")]      // indentation is whitespace too, so it collapses
    [InlineData("one\r\ntwo", "one two")]
    [InlineData("", "")]
    [InlineData("   \n\t ", "")]
    public void ChatStateLimits_OneLineCollapsesWhitespace(string input, string expected) => Assert.Equal(expected, ChatStateLimits.OneLine(input, 120));

    [Fact] public void ChatStateLimits_OneLineTruncatesWithoutAClipMarker()
    {
        var title = ChatStateLimits.OneLine(new string('x', 400), 42);
        Assert.Equal(43, title.Length);   // 42 characters and one ellipsis; the transcript's marker is not a label's ending
        Assert.DoesNotContain("history truncated", title, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', title);
    }

    [Fact] public void ChatStateLimits_CopyMessageClipsToolResults()
    {
        var copy = ChatStateLimits.CopyMessage(new ChatMessage { Role = "tool", Content = new string('x', ChatStateLimits.MaxToolResultChars + 50) });
        Assert.True(copy.Content.Length < ChatStateLimits.MaxToolResultChars + 50);
        Assert.Contains("history truncated", copy.Content, StringComparison.OrdinalIgnoreCase);
    }
    [Theory, InlineData(HttpStatusCode.Unauthorized, "401"), InlineData((HttpStatusCode)429, "rate limit"), InlineData(HttpStatusCode.InternalServerError, "server")] public void Errors_AreActionable(HttpStatusCode status, string expected) => Assert.Contains(expected, ErrorMapper.ForStatus(status), StringComparison.OrdinalIgnoreCase);

    [Fact] public void ToolRounds_GrowByHalfAndStopAtTheCeiling()
    {
        var limit = ChatStateLimits.DefaultToolRounds; var steps = new List<int>();
        while (!ChatStateLimits.AtToolRoundsCeiling(limit)) { limit = ChatStateLimits.ExtendToolRounds(limit); steps.Add(limit); }
        Assert.Equal(new[] { 38, 57, 86, 129, 194, 200 }, steps);   // the last step is clamped, not 291
        Assert.Equal(ChatStateLimits.MaxToolRoundsCeiling, ChatStateLimits.ExtendToolRounds(ChatStateLimits.MaxToolRoundsCeiling));
    }

    [Theory, InlineData(0, 1), InlineData(-5, 1), InlineData(25, 25), InlineData(9999, 200)] public void ToolRounds_ClampKeepsAHandEditedSettingUsable(int stored, int expected) => Assert.Equal(expected, ChatStateLimits.ClampToolRounds(stored));

    [Fact] public void Sanitize_DropsCallsNeverAnsweredAndResultsThatAnswerNothing()
    {
        var clean = ChatStateLimits.Sanitize(new[]
        {
            new ChatMessage { Role = "user", Content = "hi" },
            new ChatMessage { Role = "assistant", Content = "reading", ToolCalls = new List<ToolCall> { new ToolCall { Id = "a", Name = "read_file" } } },
            new ChatMessage { Role = "tool", ToolCallId = "b", Content = "answers nothing" }
        });
        Assert.Single(clean); Assert.Equal("user", clean[0].Role);
    }

    [Fact] public void Sanitize_KeepsACompleteExchangeAndDropsATrimmedHead()
    {
        var complete = new[]
        {
            new ChatMessage { Role = "user", Content = "hi" },
            new ChatMessage { Role = "assistant", Content = "", ToolCalls = new List<ToolCall> { new ToolCall { Id = "a", Name = "read_file" } } },
            new ChatMessage { Role = "tool", ToolCallId = "a", Content = "result" },
            new ChatMessage { Role = "assistant", Content = "done" }
        };
        Assert.Equal(4, ChatStateLimits.Sanitize(complete).Count);
        Assert.Equal(new[] { "assistant", "tool", "assistant" }, ChatStateLimits.Sanitize(complete.Skip(1)).Select(m => m.Role));
        Assert.Equal("done", Assert.Single(ChatStateLimits.Sanitize(new[] { complete[2], complete[3] })).Content);   // a result whose call was trimmed away answers nothing
    }

    [Fact] public void Settings_OlderFileWithoutToolRoundsGetsTheDefault()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { File.WriteAllText(file, "{\"Temperature\":0.5}"); Assert.Equal(ChatStateLimits.DefaultToolRounds, new SettingsStore(file).Load().MaxToolRounds); }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
