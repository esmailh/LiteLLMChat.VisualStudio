using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class BuildVerificationTests
{
    [Theory]
    [InlineData(BuildVerificationStatus.Failed)]
    [InlineData(BuildVerificationStatus.Cancelled)]
    [InlineData(BuildVerificationStatus.Unavailable)]
    [InlineData(BuildVerificationStatus.TimedOut)]
    public void NonSuccessNeverAllowsTests(BuildVerificationStatus status)
    {
        var result = new BuildVerification { Status = status };
        Assert.False(result.CanRunTests);
        if (status == BuildVerificationStatus.Failed) Assert.True(result.Report()!.Errors > 0);
        else Assert.Null(result.Report());
    }

    [Fact]
    public void ExplicitSuccessAllowsTests()
    {
        var result = new BuildVerification { Status = BuildVerificationStatus.Succeeded };
        Assert.True(result.CanRunTests);
        Assert.Equal(0, result.Report()!.Errors);
    }

    [Fact]
    public void ContradictoryDiagnosticsPreventTests()
    {
        var result = new BuildVerification { Status = BuildVerificationStatus.Succeeded, Diagnostics = "error: project failed" };
        Assert.False(result.CanRunTests);
        Assert.False(new BuildVerification().CanRunTests);
    }
}
