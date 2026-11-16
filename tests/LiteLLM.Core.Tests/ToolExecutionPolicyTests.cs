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

    [Theory]
    [InlineData("run_cmd")]
    [InlineData("run_powershell")]
    [InlineData("run_git")]
    [InlineData("run_glab")]
    public void DisabledTerminalRefusesCalls(string name) => Assert.NotNull(ToolExecutionPolicy.Refusal(name, true, true, false));

    [Fact]
    public void DisabledToolsRefuseReads() => Assert.NotNull(ToolExecutionPolicy.Refusal("read_file", false, true, true));

    [Fact]
    public void EnabledToolsProceed() => Assert.Null(ToolExecutionPolicy.Refusal("create_file", true, true, true));
}
