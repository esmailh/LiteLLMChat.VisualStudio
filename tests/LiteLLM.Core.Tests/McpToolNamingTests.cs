using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpToolNamingTests
{
    [Fact]
    public void Exposed_CarriesTheServerAndSanitizesBothNames()
    {
        // Case is kept: the exposed name stays recognizable, and a name that differs only in case is a different tool.
        Assert.Equal("mcp__My_Server__read_file", McpToolNaming.Exposed("My Server", "read.file"));
    }

    [Fact]
    public void Exposed_KeepsTheResultWithinTheFunctionNameLimit()
    {
        var exposed = McpToolNaming.Exposed(new string('s', 80), new string('t', 80));

        Assert.True(exposed.Length <= McpToolNaming.MaxLength);
        Assert.StartsWith("mcp__", exposed);
        // The suffix is a hash, so two different long names cannot collapse into the same truncated name.
        Assert.NotEqual(exposed, McpToolNaming.Exposed(new string('s', 80), new string('t', 79) + "u"));
    }

    [Fact]
    public void Exposed_FallsBackToAReadableNameWhenTheInputHasNoAllowedCharacters()
    {
        Assert.Equal("mcp__tool__tool", McpToolNaming.Exposed("!!!", "???"));
    }

    [Fact]
    public void Assign_GivesACollidingNameItsOwnEntry()
    {
        var server = new McpServerConfiguration { Name = "s" };
        var assigned = McpToolNaming.Assign(new[]
        {
            (server, "tool"), (server, "tool"), (new McpServerConfiguration { Name = "s" }, "tool")
        });

        Assert.Equal(3, assigned.Count);
        Assert.Equal(3, assigned.Select(item => item.ExposedName).Distinct().Count());
        Assert.Equal("mcp__s__tool", assigned[0].ExposedName);
        Assert.Equal("mcp__s__tool_2", assigned[1].ExposedName);
        Assert.Equal("mcp__s__tool_3", assigned[2].ExposedName);
    }

    [Fact]
    public void Assign_LeavesTheOriginalNameForTheUi()
    {
        var assigned = McpToolNaming.Assign(new[] { (new McpServerConfiguration { Name = "Files" }, "read.file") });

        Assert.Equal("read.file", assigned[0].OriginalName);
        Assert.Equal("mcp__Files__read_file", assigned[0].ExposedName);
    }
}
