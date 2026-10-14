using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

/// <summary>Shell-free, provider-neutral Git execution with an explicitly approved browser-login fallback.</summary>
internal static class GitTools
{
    internal static readonly ToolDefinition Definition = new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = "run_git",
            ["description"] = "Run Git from the solution directory. Commands require confirmation. On a recognized GitHub/GitLab credential failure, a separately confirmed browser login may be offered and the exact command retried once.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["arguments"] = new JObject
                    {
                        ["type"] = new JArray("array", "string"),
                        ["items"] = new JObject { ["type"] = "string" },
                        ["description"] = "Preferred: an array of exact arguments after git, e.g. [\"push\",\"origin\",\"main\"]. A legacy command-line string is also accepted."
                    }
                },
                ["required"] = new JArray("arguments")
            }
        }
    };

    internal static async Task<ToolResult> ExecuteAsync(string root, JObject args, int timeoutSeconds, CancellationToken token)
    {
        IReadOnlyList<string> arguments;
        try { arguments = ReadArguments(args["arguments"]); }
        catch (Exception ex) { return Fail(ex.Message, "git - invalid arguments"); }
        if (arguments.Count == 0) return Fail("Git arguments are required.", "git - missing arguments");
        try { GitSupport.ValidateArguments(arguments); }
        catch (ArgumentException ex) { return Fail(ex.Message, "git - invalid arguments"); }
        // Capture one stable delegate so unload cannot turn missing consent into authorization.
        var confirm = TerminalTools.Confirm;
        if (confirm == null) return Fail("Git confirmation is unavailable.", "git - confirmation unavailable");
        var timeout = TimeSpan.FromSeconds(Math.Max(1, Math.Min(3600, timeoutSeconds)));
        try
        {
            var resolver = new WindowsExecutableResolver();
            var gitPath = resolver.Resolve("git.exe");
            var display = gitPath + " " + GitSupport.BuildCommandLine(arguments);
            if (!await confirm(new CommandProposal { Shell = CommandShell.Cmd, Command = Sanitize(display) }).ConfigureAwait(true))
                return Fail("The user declined this Git command.", "git - skipped");
            var orchestrator = new GitOrchestrator(
                (request, limit, ct) => CommandExecutor.ExecuteFileAsync(request.Executable, request.Arguments, root, limit, ct),
                name => name.Equals("git.exe", StringComparison.OrdinalIgnoreCase) ? gitPath : resolver.Resolve(name));
            var outcome = await orchestrator.ExecuteAsync(arguments, timeout,
                message => confirm(new CommandProposal { Shell = CommandShell.Cmd, Command = message }), token).ConfigureAwait(false);
            return Format(outcome.Result, root, display, outcome.Authentication);
        }
        catch (Exception ex) { return Fail(Sanitize(ex.Message), "git - failed"); }
    }
    private static IReadOnlyList<string> ReadArguments(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return Array.Empty<string>();
        if (token.Type == JTokenType.String) return GitSupport.ParseCommandLine(token.Value<string>()!.Trim());
        if (token.Type != JTokenType.Array) throw new ArgumentException("arguments must be an array of strings (or a legacy command-line string).");
        var values = new List<string>();
        foreach (var item in (JArray)token)
        {
            if (item.Type != JTokenType.String) throw new ArgumentException("Every Git argument must be a string.");
            values.Add(item.Value<string>()!);
        }
        return values;
    }

    private static ToolResult Format(CommandExecutionResult result, string root, string display, string auth)
    {
        var text = new StringBuilder();
        text.AppendLine("Working directory: " + root);
        text.AppendLine("Command: " + Sanitize(display));
        text.AppendLine("Exit code: " + (result.ExitCode?.ToString() ?? "unavailable"));
        text.AppendLine("Authentication: " + auth);
        text.AppendLine("Duration: " + result.Duration.TotalSeconds.ToString("0.###") + " seconds");
        if (result.TimedOut) text.AppendLine("Status: timed out"); else if (result.Cancelled) text.AppendLine("Status: cancelled");
        if (result.OutputTruncated) text.AppendLine("Output was truncated.");
        text.AppendLine("stdout:"); text.AppendLine(result.StandardOutput.Length == 0 ? "(empty)" : Sanitize(result.StandardOutput));
        text.AppendLine("stderr:"); text.AppendLine(result.StandardError.Length == 0 ? "(empty)" : Sanitize(result.StandardError));
        var ok = Succeeded(result);
        return new ToolResult { Content = text.ToString(), Summary = "git - " + (ok ? "exit 0" : result.TimedOut ? "timed out" : result.Cancelled ? "cancelled" : "exit " + (result.ExitCode?.ToString() ?? "?")), Ok = ok };
    }

    private static string Sanitize(string value)
    {
        value = GitSupport.RedactCredentials(value ?? "");
        value = Regex.Replace(value, @"(?i)(authorization\s*:\s*(?:bearer|basic)\s+)[^\s]+", "$1***");
        value = Regex.Replace(value, @"(?i)((?:token|password|oauth_token)\s*[=:]\s*)[^\s]+", "$1***");
        return value;
    }

    private static bool Succeeded(CommandExecutionResult result) =>
        !result.TimedOut && !result.Cancelled && result.ExitCode == 0;

    private static ToolResult Fail(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = false };
}

