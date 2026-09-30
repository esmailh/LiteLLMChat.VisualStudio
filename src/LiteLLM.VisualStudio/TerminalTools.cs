using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

internal sealed class CommandProposal
{
    internal CommandShell Shell;
    internal string Command = "";
}

internal static class TerminalTools
{
    internal static Func<CommandProposal, Task<bool>>? Confirm;

    internal static readonly IReadOnlyList<ToolDefinition> Definitions = new[]
    {
        Define("run_powershell", "Run one non-interactive PowerShell command from the solution directory and return its real exit code, stdout, and stderr."),
        Define("run_cmd", "Run one non-interactive cmd.exe command from the solution directory and return its real exit code, stdout, and stderr.")
    };

    internal static bool Handles(string name) => name == "run_powershell" || name == "run_cmd";

    internal static async Task<ToolResult> ExecuteAsync(string root, string name, JObject args, int timeoutSeconds, CancellationToken token)
    {
        var command = (string?)args["command"] ?? "";
        if (string.IsNullOrWhiteSpace(command)) return Fail("A command is required.", "command · missing");
        var shell = name == "run_powershell" ? CommandShell.PowerShell : CommandShell.Cmd;
        if (Confirm != null && !await Confirm(new CommandProposal { Shell = shell, Command = command }).ConfigureAwait(true))
            return Fail("The user declined this command.", "command · skipped");
        try
        {
            var result = await CommandExecutor.ExecuteAsync(shell, command, root,
                TimeSpan.FromSeconds(Math.Max(1, Math.Min(3600, timeoutSeconds))), token).ConfigureAwait(false);
            var text = new StringBuilder();
            text.AppendLine("Working directory: " + root);
            text.AppendLine("Exit code: " + (result.ExitCode?.ToString() ?? "unavailable"));
            text.AppendLine("Duration: " + result.Duration.TotalSeconds.ToString("0.###") + " seconds");
            if (result.TimedOut) text.AppendLine("Status: timed out");
            else if (result.Cancelled) text.AppendLine("Status: cancelled");
            if (result.OutputTruncated) text.AppendLine("Output was truncated.");
            text.AppendLine("stdout:"); text.AppendLine(result.StandardOutput.Length == 0 ? "(empty)" : result.StandardOutput);
            text.AppendLine("stderr:"); text.AppendLine(result.StandardError.Length == 0 ? "(empty)" : result.StandardError);
            var ok = !result.TimedOut && !result.Cancelled && result.ExitCode == 0;
            var status = result.TimedOut ? "timed out" : result.Cancelled ? "cancelled" : "exit " + (result.ExitCode?.ToString() ?? "?");
            return new ToolResult { Content = text.ToString(), Summary = name + " · " + status, Ok = ok };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, name + " · failed");
        }
    }

    private static ToolResult Fail(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = false };
    private static ToolDefinition Define(string name, string description) => new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["command"] = new JObject { ["type"] = "string", ["description"] = "Command text to execute." } },
                ["required"] = new JArray("command")
            }
        }
    };
}
