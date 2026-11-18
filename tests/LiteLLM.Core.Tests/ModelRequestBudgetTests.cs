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
    // What the gateway actually answers: the window flat on the entry, not nested in model_info. Unread, every
    // model fell back to FallbackContextTokens and the MCP schemas never fit — see ReadContextTokens.
    [InlineData("{\"id\":\"cx/gpt-5.6-sol\",\"object\":\"model\",\"max_input_tokens\":372000,\"max_output_tokens\":372000}", 372000)]
    [InlineData("{\"max_output_tokens\":4096}", 0)]
    [InlineData("{\"context_window\":-1}", 0)]
    [InlineData("{\"context_window\":\"unknown\"}", 0)]
    public void ReadsOptionalMetadataWithoutGuessingNames(string json, int expected) =>
        Assert.Equal(expected, ModelRequestBudget.ReadContextTokens(JObject.Parse(json)));

    /// <summary>The failing turn, in the units it failed in: 372 000 tokens is a budget that holds a real server's
    /// sixty-five tool schemas, and the forty-thousand fallback that was used instead does not — 55 616 characters
    /// against the 73 047 the Grafana definitions serialize to. That is the second half of "the model has no MCP
    /// tools": the first half was the per-turn cap.</summary>
    [Fact]
    public void AWindowTheServerReportsLeavesRoomForARealServersToolSchemas()
    {
        var settings = new UserSettings { MaxTokens = 8192, FallbackContextTokens = 40000 };
        var reported = new ModelInfo { Id = "cx/gpt-5.6-sol", ContextWindowTokens = 372000 };
        var unknown = new ModelInfo { Id = "cx/gpt-5.6-sol" };

        Assert.True(ModelRequestBudget.Limit(reported, settings) > 73047);
        Assert.True(ModelRequestBudget.Limit(unknown, settings) < 73047);
    }
}
