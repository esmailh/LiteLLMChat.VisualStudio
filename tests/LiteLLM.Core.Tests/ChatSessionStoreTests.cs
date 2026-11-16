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
}
