using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteLLM.Core;

namespace LiteLLM.GitWrapper;

internal static class Program
{
    [STAThread] private static int Main(string[] args) { try { return Run(args).GetAwaiter().GetResult(); } catch (Exception e) { MessageBox.Show(GitSupport.RedactCredentials(e.Message), "LiteLLM Git", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; } }
    private static async Task<int> Run(string[] args)
    {
        var root = ValidateWorkingDirectory(Environment.GetEnvironmentVariable("LITELLM_GIT_WORKING_DIRECTORY") ?? Environment.CurrentDirectory);
        var gitArgs = ValidateArguments(args);
        var display = "git " + GitSupport.BuildCommandLine(gitArgs);
        if (MessageBox.Show(display, "Run Git command?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 3;
        var orchestrator = new GitOrchestrator((request, timeout, token) => CommandExecutor.ExecuteFileAsync(request.Executable, request.Arguments, root, timeout, token));
        var outcome = await orchestrator.ExecuteAsync(gitArgs, TimeSpan.FromMinutes(10), message => Task.FromResult(MessageBox.Show(message, "Git browser login?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK), CancellationToken.None).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(outcome.Result.StandardOutput)) Console.Out.Write(outcome.Result.StandardOutput);
        if (!string.IsNullOrEmpty(outcome.Result.StandardError)) Console.Error.Write(outcome.Result.StandardError);
        return outcome.Result.ExitCode ?? 1;
    }
    internal static string ValidateWorkingDirectory(string path) { if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Working directory is required."); if (!Path.IsPathRooted(path)) throw new ArgumentException("Working directory must be an existing absolute directory."); var full=Path.GetFullPath(path); if (!Directory.Exists(full)) throw new ArgumentException("Working directory must be an existing absolute directory."); return full; }
    internal static IReadOnlyList<string> ValidateArguments(string[] args) { if (args == null || args.Length == 0) throw new ArgumentException("Git arguments are required."); GitSupport.ValidateArguments(args); return args; }
}
