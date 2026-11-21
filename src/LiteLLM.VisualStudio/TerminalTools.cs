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
    /// <summary>True when the command is started in the background: the card says so, because "run this" and "leave
    /// this running" are different answers for the user to give.</summary>
    internal bool Background;
}

/// <summary>The command tools. One call runs a command to its end and returns its output; the same call with
/// <c>background</c> starts it, hands back its id, its pid and its log file, and returns at once.
///
/// The background half is not a second tool because it is not a second decision: it is the same command, approved
/// the same way, with its output going to a file instead of to the reply. A server is then read with read_file and
/// tested with curl through the very same tools, which is what makes "run the project and try it" one loop rather
/// than a set of features.</summary>
internal static class TerminalTools
{
    internal static Func<CommandProposal, Task<bool>>? Confirm;

    internal static readonly IReadOnlyList<ToolDefinition> Definitions = new[]
    {
        Define("run_powershell", "Run a non-interactive PowerShell command from the workspace and return its real exit code, stdout and stderr. With \"background\": true the command is started instead: the call returns at once with a shell id, a pid and the path of its log file, and the program keeps running after the call (use that for a web app, an API or anything else that does not exit on its own).", "powershell"),
        Define("run_cmd", "Run a non-interactive cmd.exe command from the workspace and return its real exit code, stdout and stderr. With \"background\": true the command is started instead: the call returns at once with a shell id, a pid and the path of its log file, and the program keeps running after the call. curl.exe for testing a local service belongs here.", "cmd")
    };

    internal static bool Handles(string name) => name == "run_powershell" || name == "run_cmd";

    internal static async Task<ToolResult> ExecuteAsync(Workspace workspace, BackgroundShells shells, string? owner,
        string name, JObject args, int timeoutSeconds, CancellationToken token)
    {
        if (!ToolArguments.TryText(args, "command", out var command, out var error)) return Fail(error, "command · invalid arguments");
        if (string.IsNullOrWhiteSpace(command)) return Fail("A command is required.", "command · missing");
        if (!ToolArguments.TryText(args, "workdir", out var workdir, out error)) return Fail(error, "command · invalid arguments");
        if (!ToolArguments.TryFlag(args, "background", false, out var background, out error)) return Fail(error, "command · invalid arguments");
        // The setting is the default, not the ceiling: a build the model knows is slow may ask for longer, and the
        // schema promises it can. It is still clamped where the wait is made.
        if (!ToolArguments.TryNumber(args, "timeout_seconds", timeoutSeconds, out var requested, out error)) return Fail(error, "command · invalid arguments");

        string directory;
        try
        {
            // The workspace decides which folders a command may run in, exactly as it does for a file: an absolute
            // path inside a root, an added project's "Label/..." path, or a plain relative path in the solution.
            directory = workdir.Trim().Length == 0
                ? workspace.Primary ?? ""
                : workspace.Resolve(workdir);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
        {
            return Fail(ex.Message, "command · " + name);
        }
        if (directory.Length == 0)
            return Fail("No solution or active document is available for the command working directory. Pass workdir for a folder inside the workspace.", "command · no working directory");
        if (!System.IO.Directory.Exists(directory))
            return Fail("Working directory not found: " + directory, "command · no working directory");

        var shell = name == "run_powershell" ? CommandShell.PowerShell : CommandShell.Cmd;
        var confirm = Confirm;
        if (confirm == null) return Fail("Command confirmation is unavailable.", "command · confirmation unavailable");
        if (!await confirm(new CommandProposal { Shell = shell, Command = command, Background = background }).ConfigureAwait(true))
            return Fail("The user declined this command.", "command · skipped");

        return background
            ? StartInBackground(shells, owner, shell, command, directory)
            : await RunToEndAsync(shell, command, directory, requested, token).ConfigureAwait(false);
    }

    /// <summary>What the model gets instead of an exit code: the id it can read the log by, the pid it can stop, and
    /// the file the output will be in. The sentence is <see cref="ShellNotice"/>'s, so the chat's own row and the
    /// tool result cannot describe the same shell differently.</summary>
    private static ToolResult StartInBackground(BackgroundShells shells, string? owner, CommandShell shell, string command, string directory)
    {
        try
        {
            var started = shells.Start(shell, command, directory, owner);
            return new ToolResult
            {
                Content = ShellNotice.Started(started),
                Summary = "background · " + started.Id + " · pid " + started.ProcessId
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, "background · failed");
        }
    }

    private static async Task<ToolResult> RunToEndAsync(CommandShell shell, string command, string directory,
        int timeoutSeconds, CancellationToken token)
    {
        try
        {
            var result = await CommandExecutor.ExecuteAsync(shell, command, directory,
                TimeSpan.FromSeconds(Math.Max(1, Math.Min(3600, timeoutSeconds))), token).ConfigureAwait(false);
            var text = new StringBuilder();
            var filtered = await RtkTerminalOutput.CompressAsync(command, result, token).ConfigureAwait(false);
            text.AppendLine("Working directory: " + directory);
            text.AppendLine("Exit code: " + (result.ExitCode?.ToString() ?? "unavailable"));
            text.AppendLine("Duration: " + result.Duration.TotalSeconds.ToString("0.###") + " seconds");
            if (result.TimedOut) text.AppendLine("Status: timed out");
            else if (result.Cancelled) text.AppendLine("Status: cancelled");
            if (result.OutputTruncated) text.AppendLine("Output was truncated.");
            if (filtered.Notice.Length > 0) text.AppendLine(filtered.Notice);
            text.AppendLine("stdout:"); text.AppendLine(filtered.Text.Length == 0 ? "(empty)" : filtered.Text);
            text.AppendLine("stderr:"); text.AppendLine(result.StandardError.Length == 0 ? "(empty)" : result.StandardError);
            var ok = !result.TimedOut && !result.Cancelled && result.ExitCode == 0;
            var status = result.TimedOut ? "timed out" : result.Cancelled ? "cancelled" : "exit " + (result.ExitCode?.ToString() ?? "?");
            return new ToolResult { Content = text.ToString(), Summary = "command · " + status + (filtered.Raw.Length > 0 ? " · RTK" : filtered.Notice.Length > 0 ? " · RTK kept raw" : ""), Detail = filtered.Notice.Length > 0 ? filtered.Notice + "\nRaw stdout (bounded):\n" + result.StandardOutput : null, Ok = ok };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, "command · failed");
        }
    }

    private static ToolResult Fail(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = false };

    private static ToolDefinition Define(string name, string description, string shellName) => new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["command"] = new JObject { ["type"] = "string", ["description"] = "Command text to run as it would be typed in " + shellName + "." },
                    ["workdir"] = new JObject { ["type"] = "string", ["description"] = "Working directory: relative to the solution, ProjectName/... for an added project, or an absolute path inside the workspace. Default the solution directory." },
                    ["background"] = new JObject { ["type"] = "boolean", ["description"] = "Start the command and return at once with a shell id, a pid and the path of its log file. Use it for anything that does not exit on its own." },
                    ["timeout_seconds"] = new JObject { ["type"] = "integer", ["description"] = "How long to wait for a foreground command, 1 to 3600. A timeout stops the command. Ignored with background." }
                },
                ["required"] = new JArray("command")
            }
        }
    };
}
