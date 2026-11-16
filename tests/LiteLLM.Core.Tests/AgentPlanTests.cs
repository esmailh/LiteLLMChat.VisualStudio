using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class AgentPlanTests
{
    [Fact]
    public void ReadsStructuredProgress()
    {
        Assert.True(AgentPlan.TryRead("{\"explanation\":\"Start\",\"plan\":[{\"step\":\"Inspect\",\"status\":\"completed\"},{\"step\":\"Fix\",\"status\":\"in_progress\"}]}", out var plan, out _));
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("in_progress", plan.Steps[1].Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"plan\":[]}")]
    [InlineData("{\"plan\":[{\"step\":5,\"status\":\"pending\"}]}")]
    [InlineData("{\"plan\":[{\"step\":\"A\",\"status\":\"done\"}]}")]
    [InlineData("{\"plan\":[{\"step\":\"A\",\"status\":\"in_progress\"},{\"step\":\"B\",\"status\":\"in_progress\"}]}")]
    public void RefusesMalformedOrAmbiguousPlans(string json)
    {
        Assert.False(AgentPlan.TryRead(json, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ClaudeTodoWriteIsObservedWithoutParsingProse()
    {
        var parser = new ClaudeStreamParser();
        parser.Accept("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[{\"content\":\"Inspect files\",\"status\":\"in_progress\"}]}}]}}");
        Assert.NotNull(parser.Plan);
        Assert.Equal("Inspect files", parser.Plan!.Steps[0].Step);
        parser.Accept("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"All steps complete\"}]}}");
        Assert.Equal("in_progress", parser.Plan.Steps[0].Status);
    }
}
