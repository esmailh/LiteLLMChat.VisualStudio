using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class GlabAuthenticationTests
{
    [Fact]
    public async Task MissingLoginOpensFlowVerifiesAndRunsMutationOnce()
    {
        var commands = new List<GitProcessRequest>();
        var signedIn = false;
        IReadOnlyList<string>? loginArgs = null;
        var flow = new GlabOrchestrator("glab", "git", (request, ct) =>
        {
            commands.Add(request);
            if (request.Executable == "git") return Done(0, "git@gitlab.company.test:group/project.git");
            if (request.Arguments[0] == "auth") return Done(signedIn ? 0 : 1);
            return Done(0, "https://gitlab.company.test/group/project/-/merge_requests/1");
        }, (args, ct) => { loginArgs = args; signedIn = true; return Task.FromResult(0); });
        var arguments = new[] { "mr", "create", "--title", "test" };
        var result = await flow.ExecuteAsync(arguments, message => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(loginArgs);
        Assert.Contains("gitlab.company.test", loginArgs!);
        Assert.Contains("--web", loginArgs!);
        Assert.Contains("ssh", loginArgs!);
        Assert.Single(commands.FindAll(c => c.Arguments[0] == "mr"));
        Assert.Same(arguments, commands[commands.Count - 1].Arguments);
    }

    [Fact]
    public async Task DeclineNeverRunsCommandOrLogin()
    {
        var login = false;
        var mutation = false;
        var flow = new GlabOrchestrator("glab", "git", (request, ct) =>
        {
            if (request.Arguments[0] == "mr") mutation = true;
            return Done(1);
        }, (args, ct) => { login = true; return Task.FromResult(0); });
        var result = await flow.ExecuteAsync(new[] { "mr", "create", "--repo", "https://gitlab.test/group/repo" }, _ => Task.FromResult(false), CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(login);
        Assert.False(mutation);
    }

    [Fact]
    public async Task ExistingLoginDoesNotAskAndDoesNotRetryApiFailure()
    {
        var calls = 0;
        var flow = new GlabOrchestrator("glab", "git", (request, ct) =>
        {
            if (request.Arguments[0] == "auth") return Done(0);
            calls++;
            return Done(1);
        }, (args, ct) => throw new Exception("must not log in"));
        await flow.ExecuteAsync(new[] { "mr", "create", "--repo", "gitlab.test/group/repo" }, _ => throw new Exception("must not ask"), CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LoginExitZeroWithoutValidCredentialsDoesNotRunMutation()
    {
        var mutation = false;
        var flow = new GlabOrchestrator("glab", "git", (request, ct) =>
        {
            if (request.Arguments[0] == "mr") mutation = true;
            return Done(1);
        }, (args, ct) => Task.FromResult(0));
        var result = await flow.ExecuteAsync(new[] { "mr", "create", "--repo=gitlab.test/group/repo" }, _ => Task.FromResult(true), CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(mutation);
    }

    [Theory]
    [InlineData("host/path")]
    [InlineData("host --token bad")]
    [InlineData("user@host")]
    public void RefusesInvalidHosts(string host) => Assert.Throws<ArgumentException>(() => GlabAuthentication.LoginArguments(host, "ssh"));

    private static Task<CommandExecutionResult> Done(int code, string output = "") =>
        Task.FromResult(new CommandExecutionResult { ExitCode = code, StandardOutput = output });
}
