using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitOrchestratorNonParallelCollection
{
    public const string Name = "GitOrchestrator non-parallel tests";
}

[Collection(GitOrchestratorNonParallelCollection.Name)]
public sealed class GitOrchestratorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(17);

    [Fact]
    public async Task MissingLoginCallbackAfterAuthenticationFailureDoesNotLoginOrRetry()
    {
        var originalArguments = (IReadOnlyList<string>)new[]
        {
            "push", "https://github.com/example/repository.git", "main"
        };
        var requests = new List<GitProcessRequest>();
        var resolved = new List<string>();
        var orchestrator = new GitOrchestrator(
            (request, _, __) =>
            {
                requests.Add(request);
                return Task.FromResult(AuthenticationFailure());
            },
            executable =>
            {
                resolved.Add(executable);
                return executable == "git.exe" ? @"X:\pinned\git.exe" : @"X:\pinned\gh.exe";
            });

        var outcome = await orchestrator.ExecuteAsync(originalArguments, Timeout, null!, CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal(@"X:\pinned\git.exe", request.Executable);
        Assert.Same(originalArguments, request.Arguments);
        Assert.Equal(new[] { "git.exe", "gh.exe" }, resolved);
        Assert.False(outcome.Retried);
        Assert.Same(requests[0], request);
        Assert.Equal("browser login declined; command was not retried", outcome.Authentication);
    }

    [Fact]
    public async Task PinnedExecutablesAreUsedForGitQueriesProviderLoginAndRetryWithOriginalArgumentsReference()
    {
        const string gitPath = @"Q:\portable\git\bin\git.exe";
        const string providerPath = @"Q:\portable\github-cli\gh.exe";
        var originalArguments = (IReadOnlyList<string>)new List<string> { "push", "origin", "main" };
        var requests = new List<GitProcessRequest>();
        var confirmations = new List<string>();
        var originalCommandExecutions = 0;

        var orchestrator = new GitOrchestrator(
            (request, _, __) =>
            {
                requests.Add(request);
                if (ReferenceEquals(request.Arguments, originalArguments))
                {
                    originalCommandExecutions++;
                    return Task.FromResult(originalCommandExecutions == 1
                        ? AuthenticationFailure()
                        : Success());
                }

                if (request.Arguments.SequenceEqual(new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }))
                    return Task.FromResult(Success("main\n"));
                if (request.Arguments.SequenceEqual(new[] { "remote", "get-url", "--push", "origin" }))
                    return Task.FromResult(Success("https://github.com/example/repository.git\n"));
                if (request.Arguments.SequenceEqual(new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https" }))
                    return Task.FromResult(Success());

                throw new Xunit.Sdk.XunitException("Unexpected command: " + request.Executable + " " + string.Join(" ", request.Arguments));
            },
            executable => executable == "git.exe"
                ? gitPath
                : executable == "gh.exe"
                    ? providerPath
                    : throw new Xunit.Sdk.XunitException("Unexpected executable resolution: " + executable));

        var outcome = await orchestrator.ExecuteAsync(
            originalArguments,
            Timeout,
            message =>
            {
                confirmations.Add(message);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.True(outcome.Retried);
        Assert.Equal("browser login completed; exact command retried once", outcome.Authentication);
        Assert.Equal(2, originalCommandExecutions);
        Assert.Same(originalArguments, requests.First().Arguments);
        Assert.Same(originalArguments, requests.Last().Arguments);

        var gitRequests = requests.Where(request => request.Executable == gitPath).ToList();
        Assert.Equal(4, gitRequests.Count);
        Assert.DoesNotContain(requests, request => request.Executable == "git.exe");

        var providerRequest = Assert.Single(requests, request => request.Executable == providerPath);
        Assert.Equal(new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https" }, providerRequest.Arguments);
        Assert.DoesNotContain(requests, request => request.Executable == "gh.exe");

        var confirmation = Assert.Single(confirmations);
        Assert.Contains(providerPath, confirmation);
    }

    private static CommandExecutionResult AuthenticationFailure() => new CommandExecutionResult
    {
        ExitCode = 128,
        StandardError = "fatal: Authentication failed for 'https://github.com/example/repository.git'"
    };

    private static CommandExecutionResult Success(string standardOutput = "") => new CommandExecutionResult
    {
        ExitCode = 0,
        StandardOutput = standardOutput
    };
}
