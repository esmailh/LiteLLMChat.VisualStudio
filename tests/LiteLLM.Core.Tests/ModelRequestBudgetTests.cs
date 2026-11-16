using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class ModelRequestBudgetTests
{
    [Fact]
    public void ReservesResponseAndMarginForSmallModels()
    {
        var settings = new UserSettings { MaxTokens = 8192 };
        Assert.Equal(42600, ModelRequestBudget.Limit(new ModelInfo { ContextWindowTokens = 32768 }, settings));
        Assert.Equal(0, ModelRequestBudget.Limit(new ModelInfo { ContextWindowTokens = 4096 }, settings));
        Assert.Equal(300000, ModelRequestBudget.Limit(new ModelInfo { ContextWindowTokens = 1000000 }, settings));
    }

    [Fact]
    public void ExplicitModelOverrideWinsOverMetadataAndUnknownUsesFallback()
    {
        var settings = new UserSettings { MaxTokens = 1000, FallbackContextTokens = 16000, ModelContextOverrides = "small=8000" };
        Assert.Equal(26800, ModelRequestBudget.Limit(new ModelInfo { Id = "unknown" }, settings));
        Assert.Equal(11952, ModelRequestBudget.Limit(new ModelInfo { Id = "small", ContextWindowTokens = 128000 }, settings));
    }

    [Fact]
    public void ToolSchemasReduceHistoryAllowanceBeforeCompaction()
    {
        var fixedMessages = new[] { new ChatMessage { Role = "system", Content = "instructions" } };
        var tools = new[] { new ToolDefinition { Function = new JObject { ["description"] = new string('x', 20000) } } };
        Assert.True(ModelRequestBudget.HistoryAllowance(50000, fixedMessages, tools) < ModelRequestBudget.HistoryAllowance(50000, fixedMessages, null) - 19000);
        Assert.Equal(0, ModelRequestBudget.HistoryAllowance(1000, fixedMessages, tools));
    }

    [Theory]
    [InlineData("{\"context_window\":32768}", 32768)]
    [InlineData("{\"model_info\":{\"max_input_tokens\":128000}}", 128000)]
    [InlineData("{\"context_window\":-1}", 0)]
    [InlineData("{\"context_window\":\"unknown\"}", 0)]
    public void ReadsOptionalMetadataWithoutGuessingNames(string json, int expected) =>
        Assert.Equal(expected, ModelRequestBudget.ReadContextTokens(JObject.Parse(json)));
}
