using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class ClaudeProgressTests
{
    [Fact]
    public void AuthenticationRetryReportsActionableFailureWithoutInventingAnswer()
    {
        var parser = new ClaudeStreamParser();
        Assert.False(parser.Accept("{\"type\":\"system\",\"subtype\":\"api_retry\",\"error\":\"authentication_failed\"}"));
        Assert.Contains("authentication failed", parser.Failure);
        Assert.Empty(parser.Text);
    }

    [Fact]
    public void StartupAndToolActivityAreVisibleWithoutExposingThinking()
    {
        var parser = new ClaudeStreamParser();
        parser.Accept("{\"type\":\"system\",\"subtype\":\"hook_started\"}");
        Assert.Contains("startup hook", parser.Status);
        parser.Accept("{\"type\":\"system\",\"subtype\":\"init\"}");
        Assert.Contains("connected", parser.Status);
        parser.Accept("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}");
        Assert.Contains("Read", parser.Status);
        Assert.Empty(parser.Text);
        Assert.Empty(parser.Failure);
    }
}
