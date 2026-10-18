using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpPolicyTests
{
    [Fact]
    public void Decide_DeniesAToolWhoseServerIsOff()
    {
        var tool = Tool();
        tool.Server.Enabled = false;

        Assert.Equal(McpDecision.Deny, McpPolicy.Decide(tool, allowAll: true, autoApproveReadOnly: true));
    }

    [Fact]
    public void Decide_DeniesAToolTheServerListedAsNeverAllowed()
    {
        var tool = Tool();
        tool.Server.DisabledTools.Add("read");

        Assert.Equal(McpDecision.Deny, McpPolicy.Decide(tool, allowAll: true, autoApproveReadOnly: true));
    }

    [Fact]
    public void Decide_DeniesAToolThatIsNotInTheSnapshot()
    {
        Assert.Equal(McpDecision.Deny, McpPolicy.Decide(null, allowAll: true, autoApproveReadOnly: true));
    }

    [Fact]
    public void Decide_AllowAllBypassesEverything()
    {
        var tool = Tool();
        tool.Annotations = new JObject { ["destructiveHint"] = true, ["openWorldHint"] = true };

        Assert.Equal(McpDecision.Allow, McpPolicy.Decide(tool, allowAll: true, autoApproveReadOnly: false));
    }

    [Fact]
    public void Decide_AllowsAToolOnTheServersAlwaysAllowList()
    {
        var tool = Tool();
        tool.Server.AllowedTools.Add("read");

        Assert.Equal(McpDecision.Allow, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: false));
    }

    [Fact]
    public void Decide_AllowsAReadOnlyToolOnlyWhenBothTogglesAreOn()
    {
        var tool = Tool();
        tool.Annotations = new JObject { ["readOnlyHint"] = true };
        tool.Server.AutoApproveReadOnly = true;

        Assert.Equal(McpDecision.Allow, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: true));
        Assert.Equal(McpDecision.Ask, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: false));

        tool.Server.AutoApproveReadOnly = false;
        Assert.Equal(McpDecision.Ask, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: true));
    }

    [Fact]
    public void Decide_StillAsksForAReadOnlyToolThatIsDestructiveOrOpenWorld()
    {
        var tool = Tool();
        tool.Server.AutoApproveReadOnly = true;
        tool.Annotations = new JObject { ["readOnlyHint"] = true, ["destructiveHint"] = true };
        Assert.Equal(McpDecision.Ask, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: true));

        tool.Annotations = new JObject { ["readOnlyHint"] = true, ["openWorldHint"] = true };
        Assert.Equal(McpDecision.Ask, McpPolicy.Decide(tool, allowAll: false, autoApproveReadOnly: true));
    }

    [Fact]
    public void Decide_AsksForAnOrdinaryTool()
    {
        Assert.Equal(McpDecision.Ask, McpPolicy.Decide(Tool(), allowAll: false, autoApproveReadOnly: true));
    }

    private static McpToolInfo Tool() => new McpToolInfo
    {
        Server = new McpServerConfiguration { Name = "Files", Command = "fake" },
        Name = "read"
    };
}
