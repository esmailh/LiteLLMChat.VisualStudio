using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

public enum RtkMode { Auto, Off }

public sealed class RtkOutput
{
    public string Text { get; private set; } = "";
    public string Notice { get; private set; } = "";
    public string Raw { get; private set; } = "";

    // Do not guess at shell syntax, pipelines, machine-readable formats, or arbitrary executable paths.
    public static string? FilterFor(string command)
    {
        if (command.IndexOfAny(new[] { '&', '|', ';', '\r', '\n', '`', '$', '>', '<', '"', '\'' }) >= 0) return null;
        if (command.Contains("--porcelain") || command.Contains("--format") || command.Contains("--pretty") || command.Contains("--numstat") || command.Contains("--raw")) return null;
        var match = Regex.Match(command.Trim(), @"^git\s+(status|log|diff)(?:\s|$)", RegexOptions.CultureInvariant);
        return match.Success ? "git-" + match.Groups[1].Value : null;
    }

    public static async Task<RtkOutput> CompressAsync(string command, CommandExecutionResult result, RtkMode mode,
        Func<string> resolve, Func<string, string, string, CancellationToken, Task<CommandExecutionResult>> filter, CancellationToken token)
    {
        var value = new RtkOutput { Text = result.StandardOutput };
        var name = FilterFor(command);
        if (mode == RtkMode.Off || name == null || result.StandardOutput.Length < 1000) return value;
        if (result.ExitCode != 0 || result.Cancelled || result.TimedOut || result.OutputTruncated)
        { value.Notice = "RTK: raw output retained (command unsuccessful or incomplete)."; return value; }
        try
        {
            var compressed = await filter(resolve(), name, result.StandardOutput, token).ConfigureAwait(false);
            if (compressed.ExitCode != 0 || compressed.Cancelled || compressed.TimedOut || compressed.OutputTruncated ||
                string.IsNullOrWhiteSpace(compressed.StandardOutput) || !string.IsNullOrWhiteSpace(compressed.StandardError))
                value.Notice = "RTK: filter unavailable or incomplete; raw output retained. Command was not rerun.";
            else if (compressed.StandardOutput.Length + 150 < result.StandardOutput.Length)
            {
                value.Raw = result.StandardOutput;
                value.Text = compressed.StandardOutput;
                value.Notice = "RTK: stdout compressed from " + value.Raw.Length + " to " + value.Text.Length + " characters; some detail omitted. Raw output is available in the tool row. stderr and exit status are unchanged.";
            }
            else value.Notice = "RTK: no useful reduction; raw output retained.";
        }
        catch (OperationCanceledException) { value.Notice = "RTK: filtering cancelled; raw output retained."; }
        catch (Exception) { value.Notice = "RTK: unavailable; raw output retained. Command was not rerun."; }
        return value;
    }
}
