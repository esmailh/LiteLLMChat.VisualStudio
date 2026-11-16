using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class RequestBudgetTests
{
    [Fact]
    public void CountsInstructionsAndSchemas()
    {
        var messages = new[] { new ChatMessage { Role = "system", Content = new string('x', 200) } };
        var tools = new[] { new ToolDefinition { Function = new JObject { ["description"] = new string('y', 200) } } };
        Assert.True(RequestBudget.Size(messages, tools) > 400);
        Assert.Throws<InvalidOperationException>(() => RequestBudget.EnsureFits(messages, tools, 300));
        RequestBudget.EnsureFits(messages, tools, 2000);
    }
}
