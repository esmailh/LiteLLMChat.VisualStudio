using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

public sealed class GitProcessRequest
{
    public GitProcessRequest(string executable, IReadOnlyList<string> arguments) { Executable = executable; Arguments = arguments; }
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
}

public sealed class GitOrchestrationResult
{
    public CommandExecutionResult Result { get; internal set; }
    public string Authentication { get; internal set; } = "not needed";
    public bool Retried { get; internal set; }
}

/// <summary>Backend-independent Git authentication and exact-one-retry workflow.</summary>
public sealed class GitOrchestrator
{
    private readonly Func<GitProcessRequest, TimeSpan, CancellationToken, Task<CommandExecutionResult>> _execute;
    private readonly Func<string, string> _resolveExecutable;

    public GitOrchestrator(Func<GitProcessRequest, TimeSpan, CancellationToken, Task<CommandExecutionResult>> execute)
        : this(execute, new WindowsExecutableResolver().Resolve) { }

    public GitOrchestrator(Func<GitProcessRequest, TimeSpan, CancellationToken, Task<CommandExecutionResult>> execute, Func<string, string> resolveExecutable)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _resolveExecutable = resolveExecutable ?? throw new ArgumentNullException(nameof(resolveExecutable));
    }

    public async Task<GitOrchestrationResult> ExecuteAsync(IReadOnlyList<string> arguments, TimeSpan timeout, Func<string, Task<bool>> confirmLogin, CancellationToken token)
    {
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        GitSupport.ValidateArguments(arguments);
        var git = Resolve("git.exe");
        var result = await Run(git, arguments, timeout, token).ConfigureAwait(false);
        var outcome = new GitOrchestrationResult { Result = result };
        if (Succeeded(result) || result.TimedOut || result.Cancelled || !GitSupport.IsAuthenticationFailure((result.StandardOutput ?? "") + "\n" + (result.StandardError ?? ""))) return outcome;
        var target = await ResolveTarget(git, arguments, timeout, token).ConfigureAwait(false);
        if (target == null) { outcome.Authentication = "credential failure detected, but no hosted remote could be resolved"; return outcome; }
        var provider = await DetectProvider(target, timeout, token).ConfigureAwait(false);
        if (provider == null) { outcome.Authentication = "credential failure detected, but the remote host was not conservatively identified as GitHub or GitLab"; return outcome; }
        var loginArgs = new List<string> { "auth", "login", "--hostname", target.Host, "--web" };
        if (Path.GetFileName(provider).Equals("gh.exe", StringComparison.OrdinalIgnoreCase)) { loginArgs.Add("--git-protocol"); loginArgs.Add(target.Protocol == "ssh" ? "ssh" : "https"); }
        var display = provider + " " + GitSupport.BuildCommandLine(loginArgs);
        if (confirmLogin == null || !await confirmLogin("Authenticate for " + target.Host + " using browser login:\n" + display).ConfigureAwait(false)) { outcome.Authentication = "browser login declined; command was not retried"; return outcome; }
        var login = await Run(provider, loginArgs, TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        if (!Succeeded(login)) { outcome.Authentication = "browser login failed; command was not retried"; return outcome; }
        outcome.Authentication = "browser login completed; exact command retried once";
        outcome.Retried = true;
        outcome.Result = await Run(git, arguments, timeout, token).ConfigureAwait(false);
        return outcome;
    }

    private string Resolve(string executable) => _resolveExecutable(executable);
    private Task<CommandExecutionResult> Run(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token) => _execute(new GitProcessRequest(executable, args), timeout, token);
    private async Task<GitRemoteInfo> ResolveTarget(string git, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token)
    {
        var op = GitSupport.GetOperation(args); if (op != "push" && op != "pull" && op != "fetch") return null;
        var candidate = GitSupport.GetExplicitRemote(args); GitRemoteInfo direct;
        if (!string.IsNullOrWhiteSpace(candidate) && GitSupport.TryParseRemote(candidate, out direct)) return direct;
        if (candidate == ".") return null;
        var branch = await Query(git, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, timeout, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate) && op == "push" && branch.Length > 0) candidate = await Query(git, new[] { "config", "--get", "branch." + branch + ".pushRemote" }, timeout, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate) && op == "push") candidate = await Query(git, new[] { "config", "--get", "remote.pushDefault" }, timeout, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate) && branch.Length > 0) candidate = await Query(git, new[] { "config", "--get", "branch." + branch + ".remote" }, timeout, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate)) candidate = "origin"; if (candidate == ".") return null;
        var urlArgs = new List<string> { "remote", "get-url" }; if (op == "push") urlArgs.Add("--push"); urlArgs.Add(candidate);
        var url = await Query(git, urlArgs, timeout, token).ConfigureAwait(false); GitRemoteInfo parsed; return GitSupport.TryParseRemote(url, out parsed) ? parsed : null;
    }
    private async Task<string> DetectProvider(GitRemoteInfo target, TimeSpan timeout, CancellationToken token)
    {
        if (target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return TryResolve("gh.exe");
        if (target.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase)) return TryResolve("glab.exe");
        var gh = TryResolve("gh.exe"); if (gh != null && await Probe(gh, target.Host, timeout, token).ConfigureAwait(false)) return gh;
        var glab = TryResolve("glab.exe"); if (glab != null && await Probe(glab, target.Host, timeout, token).ConfigureAwait(false)) return glab;
        return null;
    }
    private string TryResolve(string executable) { try { return Resolve(executable); } catch (Win32Exception) { return null; } }
    private async Task<bool> Probe(string exe, string host, TimeSpan timeout, CancellationToken token) { try { return Succeeded(await Run(exe, new[] { "auth", "status", "--hostname", host }, timeout, token).ConfigureAwait(false)); } catch { return false; } }
    private async Task<string> Query(string git, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token) { try { var r = await Run(git, args, timeout, token).ConfigureAwait(false); return Succeeded(r) ? (r.StandardOutput ?? "").Trim() : ""; } catch { return ""; } }
    private static bool Succeeded(CommandExecutionResult r) => r.ExitCode == 0 && !r.TimedOut && !r.Cancelled;
}
