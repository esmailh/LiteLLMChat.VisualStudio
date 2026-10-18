using System;
using System.Collections.Generic;
using System.IO;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class UsageLedgerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "litellm-usage-" + Guid.NewGuid().ToString("N") + ".json");

    private static TokenUsage Usage(int prompt, int completion) =>
        new TokenUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion };

    private static ChatSessionState SessionWith(params ChatTurnState[] turns) =>
        new ChatSessionState { Turns = new List<ChatTurnState>(turns) };

    [Fact]
    public void Record_AddsEachTurnToItsModelAndToTheGrandTotal()
    {
        var ledger = new UsageLedger(TempPath());
        var start = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        ledger.Record("server:model-a", Usage(100, 20), start);
        ledger.Record("server:model-a", Usage(50, 5), start.AddDays(1));
        ledger.Record("server:model-b", Usage(10, 1), start.AddDays(2));

        var a = ledger.For("server:model-a");
        Assert.NotNull(a);
        Assert.Equal(2, a!.Turns);
        Assert.Equal(150, a.PromptTokens);
        Assert.Equal(25, a.CompletionTokens);
        Assert.Equal(175, a.TotalTokens);
        Assert.Equal(start, a.FirstUtc);
        Assert.Equal(start.AddDays(1), a.LastUtc);

        Assert.Equal(160, ledger.PromptTokens);
        Assert.Equal(26, ledger.CompletionTokens);
        Assert.Equal(186, ledger.TotalTokens);
        Assert.True(ledger.Any);
        Assert.Equal(2, ledger.Entries.Count);
    }

    [Fact]
    public void Record_SkipsATurnThatReportedNothing()
    {
        var ledger = new UsageLedger(TempPath());
        ledger.Record("server:model", null, DateTime.UtcNow);
        ledger.Record("server:model", new TokenUsage(), DateTime.UtcNow);
        Assert.False(ledger.Any);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void Record_TreatsAMissingTotalAsTheSumOfItsParts()
    {
        var ledger = new UsageLedger(TempPath());
        ledger.Record("server:model", new TokenUsage { PromptTokens = 40, CompletionTokens = 2 }, DateTime.UtcNow);
        Assert.Equal(42, ledger.TotalTokens);
    }

    [Fact]
    public void SaveAndLoad_KeepTheTotalsByModel()
    {
        var path = TempPath();
        try
        {
            var ledger = new UsageLedger(path);
            ledger.Record("server:model-a", Usage(1000, 200), new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
            ledger.Record("server:model-b", Usage(30, 4), new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));
            ledger.Save();

            var loaded = new UsageLedger(path);
            Assert.Equal(1234, loaded.TotalTokens);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal(1200, loaded.For("server:model-a")!.TotalTokens);
            Assert.Contains("1 turn", loaded.For("server:model-a")!.Detail);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Load_AnUnreadableLedgerStartsEmptyInsteadOfThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not json");
            var ledger = new UsageLedger(path);
            Assert.False(ledger.Any);
            Assert.Empty(ledger.Entries);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Seed_TakesStoredTurnUsageOnce()
    {
        var ledger = new UsageLedger(TempPath());
        var sessions = new[]
        {
            SessionWith(
                new ChatTurnState { ModelKey = "server:a", Usage = Usage(100, 10), StartedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
                new ChatTurnState { ModelKey = "server:b", Usage = Usage(20, 2), StartedUtc = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) }),
            SessionWith(new ChatTurnState { ModelKey = "server:a", Usage = Usage(5, 1), StartedUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc) })
        };

        Assert.True(ledger.Seed(sessions));
        Assert.Equal(138, ledger.TotalTokens);
        Assert.Equal(2, ledger.For("server:a")!.Turns);

        // The marker is what keeps a later launch from adding the same stored turns again.
        Assert.False(ledger.Seed(sessions));
        Assert.Equal(138, ledger.TotalTokens);
    }

    [Fact]
    public void Seed_SkipsTurnsWithNoModelOrNoUsage()
    {
        var ledger = new UsageLedger(TempPath());
        var sessions = new[]
        {
            SessionWith(
                new ChatTurnState { ModelKey = "", Usage = Usage(100, 10) },
                new ChatTurnState { ModelKey = "server:a", Usage = null },
                new ChatTurnState { ModelKey = "server:a", Usage = Usage(7, 3) })
        };

        Assert.True(ledger.Seed(sessions));
        Assert.Equal(10, ledger.TotalTokens);
        Assert.Single(ledger.Entries);
        Assert.Equal("server:a", ledger.Entries[0].ModelKey);
    }

    [Fact]
    public void Seed_MarksTheDocumentEvenWhenThereIsNothingToRead()
    {
        var ledger = new UsageLedger(TempPath());
        Assert.True(ledger.Seed(null));
        Assert.False(ledger.Seed(null));
        Assert.False(ledger.Any);
    }

    [Fact]
    public void Summary_SaysWhatTheTotalCovers()
    {
        var ledger = new UsageLedger(TempPath());
        Assert.Equal("No server-reported token usage recorded yet", ledger.Summary);

        ledger.Record("server:model", Usage(1234, 56), DateTime.UtcNow);
        Assert.Equal("Used so far · 1,290 tokens · 1,234 in / 56 out", ledger.Summary);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
