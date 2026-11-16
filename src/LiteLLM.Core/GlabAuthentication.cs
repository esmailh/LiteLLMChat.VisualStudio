using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

/// <summary>One shared sign-in gate for both backends. Login has its own visible console: redirecting it
/// hides prompts and can prevent the browser flow from ever starting.</summary>
public static class GlabAuthentication
{
    public static IReadOnlyList<string> LoginArguments(string host, string protocol)
    {
        if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) ||
            uri.Authority != host || host.IndexOfAny(new[] { '/', '\\', '@', '?', '#', ' ', '\r', '\n' }) >= 0)
            throw new ArgumentException("A valid GitLab hostname is required.");
        return new[] { "auth", "login", "--hostname", host, "--web", "--api-host", host,
            "--api-protocol", "https", "--git-protocol", protocol == "ssh" ? "ssh" : "https",
            "--ssh-hostname", uri.Host, "--container-registry-domains", uri.Host };
    }

    public static async Task<int> LoginAsync(string executable, string root, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, GitSupport.BuildCommandLine(arguments))
            {
                WorkingDirectory = root, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal
            },
            EnableRaisingEvents = true
        })
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(process.ExitCode);
            process.Start();
            if (process.HasExited) exited.TrySetResult(process.ExitCode);
            using (deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                exited.TrySetCanceled();
            }))
                return await exited.Task.ConfigureAwait(false);
        }
    }
}

public sealed class GlabOrchestrator
{
    private readonly Func<GitProcessRequest, CancellationToken, Task<CommandExecutionResult>> _run;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<int>> _login;
    private readonly string _glab;
    private readonly string _git;

    public GlabOrchestrator(string glab, string git,
        Func<GitProcessRequest, CancellationToken, Task<CommandExecutionResult>> run,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>> login)
    { _glab = glab; _git = git; _run = run; _login = login; }

    public async Task<CommandExecutionResult> ExecuteAsync(IReadOnlyList<string> args, Func<string, Task<bool>> confirmLogin, CancellationToken token)
    {
        if (args.Count == 0) throw new ArgumentException("GitLab arguments are required.");
        // Local help/config/auth commands must remain usable even without an account or a repository.
        if (args[0] == "auth" && args.Count > 1 && args[1] == "login")
        {
            var target = await TargetAsync(args, token).ConfigureAwait(false);
            return await SignInAsync(target, confirmLogin, token).ConfigureAwait(false)
                ? new CommandExecutionResult { ExitCode = 0, StandardOutput = "GitLab browser sign-in completed and credentials verified." }
                : Failure("GitLab sign-in was declined or did not complete. No command was run.");
        }
        if (args[0] == "auth" || args[0] == "config" || args[0] == "version" || args[0] == "help" || Has(args, "--help") || Has(args, "--version"))
            return await Run(_glab, args, token).ConfigureAwait(false);

        var remote = await TargetAsync(args, token).ConfigureAwait(false);
        var status = await Run(_glab, new[] { "auth", "status", "--hostname", remote.Host }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (status.TimedOut || status.Cancelled) return Failure("GitLab authentication could not be checked. No command was run.");
        if (status.ExitCode != 0 && !await SignInAsync(remote, confirmLogin, token).ConfigureAwait(false))
            return Failure("GitLab sign-in was declined or failed. No command was run.");
        // Do not retry a mutation after an ambiguous API failure: preflight login avoids duplicate MRs.
        return await Run(_glab, args, token).ConfigureAwait(false);
    }

    private async Task<bool> SignInAsync(GitRemoteInfo target, Func<string, Task<bool>> confirm, CancellationToken token)
    {
        if (!await confirm("Sign in to GitLab at " + target.Host + "? A visible sign-in window will open and launch your browser. Complete any prompts there. The original command runs only after sign-in is verified.").ConfigureAwait(false)) return false;
        if (await _login(GlabAuthentication.LoginArguments(target.Host, target.Protocol), token).ConfigureAwait(false) != 0) return false;
        var status = await Run(_glab, new[] { "auth", "status", "--hostname", target.Host }, token).ConfigureAwait(false);
        return status.ExitCode == 0 && !status.Cancelled && !status.TimedOut;
    }

    private async Task<GitRemoteInfo> TargetAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i];
            if ((value == "--hostname" || value == "--repo" || value == "-R") && i + 1 < args.Count)
            {
                var explicitValue = args[++i];
                if (value == "--hostname") return Host(explicitValue);
                if (GitSupport.TryParseRemote(explicitValue, out var parsed)) return parsed;
                if (explicitValue.Split('/').Length >= 3) return Host(explicitValue.Split('/')[0]);
            }
            else if (value.StartsWith("--hostname=", StringComparison.Ordinal)) return Host(value.Substring(11));
            else if (value.StartsWith("--repo=", StringComparison.Ordinal))
            {
                var repo = value.Substring(7);
                if (GitSupport.TryParseRemote(repo, out var parsed)) return parsed;
                if (repo.Split('/').Length >= 3) return Host(repo.Split('/')[0]);
            }
        }
        var environmentHost = Environment.GetEnvironmentVariable("GITLAB_HOST");
        if (!string.IsNullOrWhiteSpace(environmentHost)) return Host(environmentHost!);
        var remote = await Run(_git, new[] { "remote", "get-url", "origin" }, token).ConfigureAwait(false);
        if (remote.ExitCode == 0 && GitSupport.TryParseRemote(remote.StandardOutput.Trim(), out var target))
        {
            if (target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This repository points to GitHub, not GitLab. Select a GitLab repository with --repo https://your-gitlab-host/group/project.");
            return target;
        }
        throw new InvalidOperationException("No GitLab host could be detected. Open the GitLab repository or specify --repo https://your-gitlab-host/group/project.");
    }

    private static GitRemoteInfo Host(string host)
    {
        GlabAuthentication.LoginArguments(host, "https");
        return new GitRemoteInfo(host, "https", host);
    }
    private static bool Has(IReadOnlyList<string> args, string value) { foreach (var arg in args) if (arg == value) return true; return false; }
    private Task<CommandExecutionResult> Run(string executable, IReadOnlyList<string> args, CancellationToken token) => _run(new GitProcessRequest(executable, args), token);
    private static CommandExecutionResult Failure(string message) => new CommandExecutionResult { ExitCode = 1, StandardError = message };
}
