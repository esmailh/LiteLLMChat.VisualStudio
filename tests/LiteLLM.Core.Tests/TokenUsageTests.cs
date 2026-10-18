using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class TokenUsageTests
{
    [Fact]
    public void Read_TakesTheThreeCountsTheServerReported()
    {
        var usage = TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":1234,\"completion_tokens\":56,\"total_tokens\":1290}"));
        Assert.NotNull(usage);
        Assert.Equal(1234, usage!.PromptTokens);
        Assert.Equal(56, usage.CompletionTokens);
        Assert.Equal(1290, usage.TotalTokens);
        Assert.Equal("Tokens · 1,234 in / 56 out · 1,290 total", usage.Line);
    }

    [Fact]
    public void Read_ComputesTheTotalWhenTheServerLeavesItOut()
    {
        var usage = TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":10,\"completion_tokens\":4}"));
        Assert.NotNull(usage);
        Assert.Equal(14, usage!.TotalTokens);
    }

    [Fact]
    public void Read_ReportsNothingRatherThanZero()
    {
        Assert.Null(TokenUsage.Read(null));
        Assert.Null(TokenUsage.Read(JObject.Parse("{}")));
        Assert.Null(TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":0,\"completion_tokens\":0}")));
        Assert.Null(TokenUsage.Read(new JValue("not an object")));
    }

    [Fact]
    public void Add_SumsEveryFieldAndIgnoresACallThatReportedNothing()
    {
        var total = new TokenUsage { PromptTokens = 10, CompletionTokens = 2, TotalTokens = 12 };
        total.Add(new TokenUsage { PromptTokens = 30, CompletionTokens = 7, TotalTokens = 37 });
        total.Add(null);
        Assert.Equal(40, total.PromptTokens);
        Assert.Equal(9, total.CompletionTokens);
        Assert.Equal(49, total.TotalTokens);
        Assert.True(total.Any);
    }

    [Fact]
    public void Read_ToleratesACountLargerThanAnInt()
    {
        // Some servers report a count int cannot hold. It must clamp, not throw on its way to the transcript.
        var usage = TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":2147483648}"));
        Assert.NotNull(usage);
        Assert.Equal(int.MaxValue, usage!.PromptTokens);
        Assert.Equal(int.MaxValue, usage.TotalTokens);
    }

    [Fact]
    public void Read_ToleratesAnIntegerTooLargeEvenForLong()
    {
        // Newtonsoft parses this as a BigInteger, which cannot be converted to long. It reads as unreported.
        Assert.Null(TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":99999999999999999999999999}")));
    }

    [Fact]
    public void Read_ClampsANegativeCountToZero()
    {
        var usage = TokenUsage.Read(JObject.Parse("{\"prompt_tokens\":-3,\"completion_tokens\":5}"));
        Assert.NotNull(usage);
        Assert.Equal(0, usage!.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
        Assert.Equal(5, usage.TotalTokens);
    }

    [Fact]
    public void Add_SaturatesInsteadOfWrappingToANegativeTotal()
    {
        var total = new TokenUsage { PromptTokens = int.MaxValue, CompletionTokens = 1, TotalTokens = int.MaxValue };
        total.Add(new TokenUsage { PromptTokens = int.MaxValue, CompletionTokens = 1, TotalTokens = int.MaxValue });
        Assert.Equal(int.MaxValue, total.PromptTokens);
        Assert.Equal(2, total.CompletionTokens);
        Assert.Equal(int.MaxValue, total.TotalTokens);
        Assert.True(total.Any);
    }

    [Fact]
    public void Any_IsFalseForAFreshTotal()
    {
        Assert.False(new TokenUsage().Any);
    }

    [Fact]
    public void ParseCompletion_ReturnsUsageBesideTheMessage()
    {
        var message = ResponseParser.ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"answer\"}}],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":8,\"total_tokens\":128}}", out var usage);
        Assert.Equal("answer", message.Content);
        Assert.NotNull(usage);
        Assert.Equal(120, usage!.PromptTokens);
        Assert.Equal(8, usage.CompletionTokens);
    }

    [Fact]
    public void ParseCompletion_LeavesUsageNullWhenTheServerDidNotReportIt()
    {
        ResponseParser.ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}", out var usage);
        Assert.Null(usage);
    }

    [Fact]
    public void ParseStreamChunk_ReadsTheFinalUsageOnlyChunk()
    {
        // OpenAI-compatible servers send the counts last, in a chunk whose "choices" array is empty.
        var update = ResponseParser.ParseStreamChunk("{\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":21,\"total_tokens\":821}}");
        Assert.NotNull(update.Usage);
        Assert.Equal(800, update.Usage!.PromptTokens);
        Assert.Equal(21, update.Usage.CompletionTokens);
        Assert.Equal("", update.ContentDelta);
    }

    [Fact]
    public void ParseStreamChunk_LeavesUsageNullOnATextChunk()
    {
        var update = ResponseParser.ParseStreamChunk("{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}");
        Assert.Null(update.Usage);
        Assert.Equal("hi", update.ContentDelta);
    }
}
