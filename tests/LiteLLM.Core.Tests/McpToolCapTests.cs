using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpToolCapTests
{
    /// <summary>The failing case in one line: a saved sixteen was the old default, every save wrote it down, and
    /// kept it would go on hiding every tool past a real server's alphabetical first sixteen.</summary>
    [Fact]
    public void Upgrade_LiftsTheOldDefaultOnce()
    {
        Assert.Equal(McpToolSnapshot.DefaultAutomaticTools, McpToolCap.Upgrade(16, alreadyUpgraded: false));
    }

    /// <summary>Once the move is recorded, sixteen is a choice: lowering the offer is what the setting is for.</summary>
    [Fact]
    public void Upgrade_KeepsSixteenAfterTheMove()
    {
        Assert.Equal(16, McpToolCap.Upgrade(16, alreadyUpgraded: true));
    }

    [Fact]
    public void Upgrade_LeavesAnyOtherValueAlone()
    {
        Assert.Equal(40, McpToolCap.Upgrade(40, alreadyUpgraded: false));
        Assert.Equal(200, McpToolCap.Upgrade(200, alreadyUpgraded: true));
    }

    /// <summary>A file edited by hand could carry a zero, and "offer no MCP tool at all" is what the feature switch
    /// is for, not a cap nobody can see the consequence of.</summary>
    [Fact]
    public void Normalize_ReadsANonPositiveCapAsOne()
    {
        Assert.Equal(1, McpToolCap.Normalize(0));
        Assert.Equal(1, McpToolCap.Normalize(-5));
        Assert.Equal(128, McpToolCap.Normalize(128));
    }

    /// <summary>The number the two copies of the setting have to agree on.</summary>
    [Fact]
    public void TheOldDefaultIsWhatTheOldBuildWrote()
    {
        Assert.Equal(16, McpToolCap.LegacyAutomaticTools);
        Assert.True(McpToolSnapshot.DefaultAutomaticTools > McpToolCap.LegacyAutomaticTools);
    }
}
