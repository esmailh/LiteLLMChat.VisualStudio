using System;
using System.IO;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class ChatSessionStoreTests
{
    [Theory]
    [InlineData(ChatTurnStatus.Pending)]
    [InlineData(ChatTurnStatus.Streaming)]
    public void SavePreservesLiveStatusAndLoadRecoversInterruptedTurn(ChatTurnStatus status)
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            var turn = new ChatTurnState { Status = status };
            turn.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            session.Turns.Add(turn);
            var document = new ChatSessionDocument { ActiveSessionId = session.Id };
            document.Sessions.Add(session);
            var store = new ChatSessionStore(path);

            store.Save(document);

            Assert.Equal(status, turn.Status);
            Assert.True(string.IsNullOrEmpty(turn.Error));
            Assert.Null(turn.CompletedUtc);
            var recovered = Assert.Single(Assert.Single(store.Load().Sessions).Turns);
            Assert.Equal(ChatTurnStatus.Cancelled, recovered.Status);
            Assert.NotNull(recovered.CompletedUtc);
            Assert.Equal(status, turn.Status);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CheckpointSurvivesARoundTripWithItsBoundary()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            var turn = new ChatTurnState { Status = ChatTurnStatus.Complete };
            turn.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            turn.Compaction = new CompactionCheckpoint { Covered = 1, Text = "## Objective\n- finish the release" };
            turn.Summaries.Add(new CompactionReport { Messages = 1, Summary = "Compacted · 1 message → checkpoint", Detail = "## Objective\n- finish the release", Trigger = "Manual" });
            session.Turns.Add(turn);
            session.MeasuredPromptTokens = 1200;
            session.MeasuredPromptChars = 2400;
            session.MeasuredPromptModel = "server/model";
            var document = new ChatSessionDocument { ActiveSessionId = session.Id };
            document.Sessions.Add(session);

            var store = new ChatSessionStore(path);
            store.Save(document);
            var loaded = Assert.Single(Assert.Single(store.Load().Sessions).Turns);

            Assert.NotNull(loaded.Compaction);
            Assert.Equal(1, loaded.Compaction!.Covered);
            Assert.Contains("## Objective", loaded.Compaction.Text, StringComparison.Ordinal);
            Assert.Equal("Manual", Assert.Single(loaded.Summaries).Trigger);
            var reloaded = Assert.Single(store.Load().Sessions);
            Assert.Equal(1200, reloaded.MeasuredPromptTokens);
            Assert.Equal(2400, reloaded.MeasuredPromptChars);
            Assert.Equal("server/model", reloaded.MeasuredPromptModel);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnEmptyCheckpointIsDroppedRatherThanSentAsAnEmptyMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            var turn = new ChatTurnState { Status = ChatTurnStatus.Complete };
            turn.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            // A model that answered with nothing must not leave an empty message at the head of every later request.
            turn.Compaction = new CompactionCheckpoint { Covered = 1, Text = "" };
            session.Turns.Add(turn);
            var document = new ChatSessionDocument { ActiveSessionId = session.Id };
            document.Sessions.Add(session);

            var store = new ChatSessionStore(path);
            store.Save(document);

            Assert.Null(Assert.Single(Assert.Single(store.Load().Sessions).Turns).Compaction);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AHeadSlicedTurnDropsTheCheckpointBoundaryAndKeepsTheText()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = ChatSessionStore.NewSession();
            var turn = new ChatTurnState { Status = ChatTurnStatus.Complete };
            for (var i = 0; i < ChatStateLimits.MaxMessagesPerTurn + 40; i++)
                turn.Messages.Add(new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = "m" + i });
            turn.Compaction = new CompactionCheckpoint { Covered = 100, Text = "## Objective\n- finish the release" };
            session.Turns.Add(turn);
            var document = new ChatSessionDocument { ActiveSessionId = session.Id };
            document.Sessions.Add(session);

            var store = new ChatSessionStore(path);
            store.Save(document);
            var loaded = Assert.Single(Assert.Single(store.Load().Sessions).Turns);

            // The boundary counted messages that are no longer on disk. The text is the only record of them, so it
            // stays; the index is dropped so the next turn folds the conversation again from its beginning.
            Assert.Equal(0, loaded.Compaction!.Covered);
            Assert.Contains("## Objective", loaded.Compaction.Text, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
