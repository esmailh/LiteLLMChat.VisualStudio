using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class GlabCommandTests
{
    [Theory]
    [InlineData("mr")]
    [InlineData("issue")]
    [InlineData("ci")]
    [InlineData("api")]
    [InlineData("release")]
    public void PreservesAllCommandFamilies(string command)
    {
        var args = GlabCommand.ReadArguments(new JArray(command, "list", "--repo", "group/project"));
        Assert.Equal(command, args[0]);
        Assert.Equal("group/project", args[3]);
    }

    [Fact]
    public void RejectsWrongTypes()
    {
        Assert.Throws<ArgumentException>(() => GlabCommand.ReadArguments(new JArray("mr", 42)));
        Assert.Throws<ArgumentException>(() => GlabCommand.ReadArguments(new JValue("mr create")));
        Assert.Throws<ArgumentException>(() => GlabCommand.ReadArguments(new JArray()));
    }

    [Fact]
    public void ClaudeEditingAddsWritesWithoutBroadShellPermission()
    {
        var tools = ClaudeToolPolicy.WithFileEditing("Read,Grep,Glob");
        ClaudeToolPolicy.Validate(tools, true, false, false, true);
        Assert.Contains("Edit", ClaudeToolPolicy.ParseAllowedToolNames(tools));
        Assert.Contains("Write", ClaudeToolPolicy.ParseAllowedToolNames(tools));
        Assert.DoesNotContain("Bash", ClaudeToolPolicy.ParseAllowedToolNames(tools));
        Assert.Equal(tools, ClaudeToolPolicy.WithFileEditing(tools));
    }
}
