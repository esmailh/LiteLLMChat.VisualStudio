using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class ToolExecutionPolicyTests
{
    [Theory]
    [InlineData("apply_patch")]
    [InlineData("create_file")]
    [InlineData("replace_string_in_file")]
    [InlineData("multi_replace_string_in_file")]
    [InlineData("create_directory")]
    public void DisabledEditingRefusesCalls(string name) => Assert.NotNull(ToolExecutionPolicy.Refusal(name, true, false, true));

    /// <summary>The background half of a command is the same tool, so there is no name here for it: a shell that
    /// stays up is started by <c>run_powershell</c> or <c>run_cmd</c> with <c>background</c>, and the switch that
    /// gates a command gates what the command leaves running.</summary>
    [Theory]
    [InlineData("run_cmd")]
    [InlineData("run_powershell")]
    [InlineData("run_git")]
    [InlineData("run_glab")]
    public void DisabledTerminalRefusesCalls(string name) => Assert.NotNull(ToolExecutionPolicy.Refusal(name, true, true, false));

    /// <summary>The four process tools and <c>run_http_request</c> were removed in favour of the command tools with
    /// <c>background</c>. A policy that still named them would be refusing calls no tool can make.</summary>
    [Theory]
    [InlineData("start_process")]
    [InlineData("read_process")]
    [InlineData("stop_process")]
    [InlineData("list_processes")]
    [InlineData("run_http_request")]
    public void RemovedToolsAreNotGatedByName(string name) =>
        Assert.Null(ToolExecutionPolicy.Refusal(name, true, true, false));

    [Fact]
    public void DisabledToolsRefuseReads() => Assert.NotNull(ToolExecutionPolicy.Refusal("read_file", false, true, true));

    [Fact]
    public void EnabledToolsProceed() => Assert.Null(ToolExecutionPolicy.Refusal("create_file", true, true, true));
}
