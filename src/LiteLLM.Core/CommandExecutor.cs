using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

public enum CommandShell { PowerShell, Cmd }

public sealed class CommandExecutionResult
{
    public int? ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public bool OutputTruncated { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>Runs one non-interactive Windows command with redirected, bounded output.</summary>
public static class CommandExecutor
{
    public const int DefaultMaxOutputChars = 60000;

    public static Task<CommandExecutionResult> ExecuteAsync(
        CommandShell shell, string command, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken, int maxOutputChars = DefaultMaxOutputChars)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));
        return ExecuteProcessAsync(StartInfo(shell, command, workingDirectory), timeout, cancellationToken, maxOutputChars);
    }

    /// <summary>Runs an executable directly, quoting each argument for the Windows process command line.</summary>
    public static Task<CommandExecutionResult> ExecuteFileAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken, int maxOutputChars = DefaultMaxOutputChars)
    {
        return ExecuteFileAsync(fileName, GitSupport.BuildCommandLine(arguments), workingDirectory, timeout,
            cancellationToken, maxOutputChars);
    }

    /// <summary>Runs an executable directly, without a command shell interpreting its arguments.</summary>
    public static Task<CommandExecutionResult> ExecuteFileAsync(
        string fileName, string arguments, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken, int maxOutputChars = DefaultMaxOutputChars)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("An executable is required.", nameof(fileName));
        return ExecuteProcessAsync(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? "",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }, timeout, cancellationToken, maxOutputChars);
    }

    private static async Task<CommandExecutionResult> ExecuteProcessAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken, int maxOutputChars)
    {
        if (!Directory.Exists(startInfo.WorkingDirectory)) throw new DirectoryNotFoundException("Working directory not found: " + startInfo.WorkingDirectory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputChars < 1000) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));

        var output = new BoundedText(maxOutputChars);
        var error = new BoundedText(maxOutputChars);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        using (var process = new Process())
        {
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => exited.TrySetResult(true);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            if (!process.Start()) throw new InvalidOperationException("The command process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) exited.TrySetResult(true);

            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = CancellationTask(cancellationToken);
            var winner = await Task.WhenAny(exited.Task, timeoutTask, cancellationTask).ConfigureAwait(false);
            var timedOut = winner == timeoutTask;
            var cancelled = winner == cancellationTask;
            if (timedOut || cancelled)
            {
                KillTree(process);
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            try { if (process.HasExited) process.WaitForExit(); } catch (InvalidOperationException) { }
            stopwatch.Stop();
            return new CommandExecutionResult
            {
                ExitCode = process.HasExited ? (int?)process.ExitCode : null,
                StandardOutput = output.Text,
                StandardError = error.Text,
                TimedOut = timedOut,
                Cancelled = cancelled,
                OutputTruncated = output.Truncated || error.Truncated,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private static ProcessStartInfo StartInfo(CommandShell shell, string command, string workingDirectory)
    {
        var powerShell = shell == CommandShell.PowerShell;
        return new ProcessStartInfo
        {
            FileName = powerShell ? "powershell.exe" : "cmd.exe",
            Arguments = powerShell
                ? "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))
                : "/d /s /c \"" + command + "\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static Task CancellationTask(CancellationToken token)
    {
        if (!token.CanBeCanceled) return Task.Delay(Timeout.Infinite);
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => source.TrySetResult(true));
        return source.Task;
    }

    private static void KillTree(Process process)
    {
        try
        {
            using (var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/PID " + process.Id + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true
            })) killer?.WaitForExit(5000);
        }
        catch { try { process.Kill(); } catch { } }
    }

    private sealed class BoundedText
    {
        private readonly int _max;
        private readonly StringBuilder _text = new StringBuilder();
        internal bool Truncated { get; private set; }
        internal BoundedText(int max) { _max = max; }
        internal void AppendLine(string value)
        {
            lock (_text)
            {
                var remaining = _max - _text.Length;
                if (remaining <= 0) { Truncated = true; return; }
                if (value.Length + Environment.NewLine.Length > remaining)
                {
                    _text.Append(value, 0, Math.Min(value.Length, remaining));
                    Truncated = true;
                    return;
                }
                _text.AppendLine(value);
            }
        }
        internal string Text { get { lock (_text) return _text.ToString(); } }
    }
}
