using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class RtkOutputTests
{
    [Theory]
    [InlineData("git status", "git-status")]
    [InlineData("git diff --stat", "git-diff")]
    [InlineData("git log -5", "git-log")]
    [InlineData("git status --porcelain", null)]
    [InlineData("git diff | more", null)]
    [InlineData("git status; Remove-Item x", null)]
    [InlineData("git log --format=json", null)]
    [InlineData("git status\nwhoami", null)]
    [InlineData("git diff 'a b'", null)]
    [InlineData("glab mr create", null)]
    [InlineData("vstest.console.exe tests.dll", null)]
    public void PlansOnlySimpleSupportedCommands(string command, string? expected) => Assert.Equal(expected, RtkOutput.FilterFor(command));

    [Fact]
    public async Task CompressionKeepsOriginalResultAndBoundedRaw()
    {
        var original = Result(new string('x', 2000));
        original.StandardError = "warning";
        var output = await RtkOutput.CompressAsync("git diff", original, RtkMode.Auto, () => "rtk",
            (exe, filter, input, ct) => Task.FromResult(Result("short summary")), CancellationToken.None);
        Assert.Equal("short summary", output.Text);
        Assert.Equal(original.StandardOutput, output.Raw);
        Assert.Contains("compressed", output.Notice);
        Assert.Equal(0, original.ExitCode);
        Assert.Equal("warning", original.StandardError);
        Assert.Equal(2000, original.StandardOutput.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrBrokenFilterReturnsRawWithoutRetry(bool missing)
    {
        var original = Result(new string('x', 2000));
        var calls = 0;
        var output = await RtkOutput.CompressAsync("git status", original, RtkMode.Auto,
            () => missing ? throw new InvalidOperationException() : "rtk",
            (exe, filter, input, ct) => { calls++; return Task.FromResult(new CommandExecutionResult { ExitCode = 1 }); }, CancellationToken.None);
        Assert.Equal(original.StandardOutput, output.Text);
        Assert.Equal(missing ? 0 : 1, calls);
        Assert.Contains("not rerun", output.Notice);
    }

    [Fact]
    public async Task OffNeverResolvesOrFilters()
    {
        var original = Result(new string('x', 2000));
        var output = await RtkOutput.CompressAsync("git status", original, RtkMode.Off,
            () => throw new Exception(), (e, f, i, ct) => throw new Exception(), CancellationToken.None);
        Assert.Equal(original.StandardOutput, output.Text);
        Assert.Empty(output.Notice);
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, true)]
    public async Task UnsuccessfulOrIncompleteResultsNeverFilter(int exit, bool timeout, bool cancelled, bool truncated)
    {
        var original = Result(new string('x', 2000));
        original.ExitCode = exit; original.TimedOut = timeout; original.Cancelled = cancelled; original.OutputTruncated = truncated;
        var output = await RtkOutput.CompressAsync("git diff", original, RtkMode.Auto,
            () => throw new Exception(), (e, f, i, ct) => throw new Exception(), CancellationToken.None);
        Assert.Equal(original.StandardOutput, output.Text);
    }

    [Fact]
    public async Task CancellationDuringFilterRetainsCompletedCommandOutput()
    {
        var original = Result(new string('x', 2000));
        var output = await RtkOutput.CompressAsync("git diff", original, RtkMode.Auto, () => "rtk",
            (e, f, i, ct) => throw new OperationCanceledException(), CancellationToken.None);
        Assert.Equal(original.StandardOutput, output.Text);
        Assert.Contains("cancelled", output.Notice);
    }

    [Fact]
    public async Task EmptyFilterNeverHidesOutput()
    {
        var original = Result(new string('x', 2000));
        var output = await RtkOutput.CompressAsync("git diff", original, RtkMode.Auto, () => "rtk",
            (e, f, i, ct) => Task.FromResult(Result("")), CancellationToken.None);
        Assert.Equal(original.StandardOutput, output.Text);
    }

    private static CommandExecutionResult Result(string text) => new CommandExecutionResult { ExitCode = 0, StandardOutput = text };
}
