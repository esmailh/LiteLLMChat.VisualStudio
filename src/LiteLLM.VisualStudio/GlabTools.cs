using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

internal static class GlabTools
{
    internal static readonly ToolDefinition Definition = new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = "run_glab",
            ["description"] = "Run the GitLab CLI with confirmation. Supports mr, issue, ci, release and api. The GitLab host is detected from the repository; missing authentication opens a separately approved browser sign-in window automatically. Use exact noninteractive arguments. Never put tokens in arguments.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["arguments"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } } },
                ["required"] = new JArray("arguments")
            }
        }
    };

    internal static async Task<ToolResult> ExecuteAsync(string root, JObject args, int timeoutSeconds, CancellationToken token)
    {
        try
        {
            var arguments = GlabCommand.ReadArguments(args["arguments"]);
            var confirm = TerminalTools.Confirm;
            if (confirm == null) return Fail("GitLab command confirmation is unavailable.");
            var executable = new WindowsExecutableResolver().Resolve("glab.exe");
            var display = "glab " + GitSupport.BuildCommandLine(arguments);
            if (!await confirm(new CommandProposal { Shell = CommandShell.Cmd, Command = GitSupport.RedactCredentials(display) }).ConfigureAwait(true))
                return Fail("The user declined the GitLab command.");
            var resolver = new WindowsExecutableResolver();
            var flow = new GlabOrchestrator(executable, resolver.Resolve("git.exe"),
                (request, ct) => CommandExecutor.ExecuteFileAsync(request.Executable, request.Arguments, root,
                    TimeSpan.FromSeconds(Math.Max(1, Math.Min(3600, timeoutSeconds))), ct),
                (login, ct) => GlabAuthentication.LoginAsync(executable, root, login, ct));
            var result = await flow.ExecuteAsync(arguments,
                message => Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    return await confirm(new CommandProposal { Shell = CommandShell.Cmd, Command = message }).ConfigureAwait(true);
                }).Task, token).ConfigureAwait(true);
            return new ToolResult
            {
                Ok = result.ExitCode == 0 && !result.TimedOut && !result.Cancelled,
                Summary = "glab · " + (result.TimedOut ? "timed out" : result.Cancelled ? "cancelled" : "exit " + result.ExitCode),
                Content = "Exit code: " + result.ExitCode + "\n" + (result.OutputTruncated ? "Output was truncated.\n" : "") +
                    GitSupport.RedactCredentials(result.StandardOutput + "\n" + result.StandardError)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(GitSupport.RedactCredentials(ex.Message)); }
    }

    private static ToolResult Fail(string reason) => new ToolResult { Ok = false, Summary = "glab · not run", Content = reason };
}
