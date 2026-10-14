using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;

/// <summary>Runs the open solution's tests after a build has passed, and reads the result.
///
/// The counterpart of <see cref="SolutionBuild"/>, and deliberately a command line where that one goes through
/// Visual Studio: there is no "run the tests" command in the automation model, and a test run has no project
/// system to ask — one runner reads every framework's assemblies. What it does share with the build check is
/// the rule that silence is never a pass: a run that could not be started, timed out or reported nothing comes
/// back as unverified with the reason, and the caller says so in the transcript.</summary>
internal static class SolutionTests
{
    /// <summary>Longest a test run is waited on. Past this it is reported as unverified rather than waited
    /// out: the turn is blocked on it, and a hung runner is a thing that happens.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Output kept from the runner. Generous, because the failure text is the part that gets handed
    /// to the model and the runner prints nothing else at the verbosity asked for below.</summary>
    private const int MaxOutputChars = 200000;

    /// <summary>Runs the solution's tests against the assemblies its projects have built.
    ///
    /// A null report means nothing was verified. <paramref name="problem"/> then says why in one line, except
    /// when it is empty — that is the one case where there is nothing to report at all, because the solution
    /// holds no test project and the check does not apply. Drawing a row every round for a solution that has no
    /// tests would be noise, and noise is what makes a real warning unreadable.</summary>
    internal static async Task<(BuildReport? Report, string Problem)> RunAsync(CancellationToken token)
    {
        string runner;
        try { runner = new WindowsExecutableResolver().Resolve("vstest.console.exe"); }
        catch (Exception) { return (null, "vstest.console.exe was not found, so the tests could not be run"); }

        List<string> projects;
        try { projects = await ProjectFilesAsync().ConfigureAwait(true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (null, "the solution's projects could not be read"); }

        var (assemblies, testProjects) = await Task.Run(() => Assemblies(projects), token).ConfigureAwait(true);
        if (testProjects == 0) return (null, "");   // no test project in this solution
        if (assemblies.Count == 0) return (null, "no built test assembly was found — build the solution first");

        // One invocation for every assembly, so the runner writes one summary rather than one per project: the
        // report the turn keeps is a single check, and merging several would be this class inventing a total.
        var arguments = new List<string>(assemblies) { "/logger:console;verbosity=minimal" };
        CommandExecutionResult result;
        try
        {
            result = await CommandExecutor.ExecuteFileAsync(runner, arguments, Path.GetDirectoryName(assemblies[0])!, Timeout, token, MaxOutputChars).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, "the test run could not be started: " + ex.Message); }

        if (result.Cancelled) { token.ThrowIfCancellationRequested(); return (null, "the test run was cancelled"); }
        if (result.TimedOut) return (null, "the test run did not finish within " + (int)Timeout.TotalMinutes + " minutes");

        var report = TestDiagnostics.Summarize(result.StandardOutput);
        if (report == null) return (null, Reason(result));
        // Counts without their failure text would leave the model knowing a test failed and not which or why,
        // so the two travel together; an empty detail means the runner reported failures it never described.
        return (report, "");
    }

    /// <summary>Every project file the solution holds. Read from the solution rather than by walking the disk,
    /// so a project outside it is not run because it happens to sit in the same folder. Only the paths are
    /// gathered here, because the solution's own model is main-thread state; the files themselves are read
    /// below, off it.</summary>
    private static async Task<List<string>> ProjectFilesAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var files = new List<string>();
        if (await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE)) is not DTE dte) return files;
        if (dte.Solution == null || !dte.Solution.IsOpen) return files;
        foreach (Project project in dte.Solution.Projects) Collect(project, files, 0);
        return files;
    }

    /// <summary>A project file, and those of the projects under it: a solution folder is itself a project whose
    /// items are the projects inside it, so the real ones are one level down.</summary>
    private static void Collect(Project project, List<string> files, int depth)
    {
        ThreadHelper.ThrowIfNotOnUIThread();   // the solution model is not safe to walk from a pool thread
        if (project == null || depth > 4) return;
        try
        {
            var path = project.FullName;
            if (!string.IsNullOrEmpty(path)) files.Add(path);
            // Also a path for the solution file itself, which is not a project and is dropped by the read below.
            if (project.ProjectItems == null) return;
            foreach (ProjectItem item in project.ProjectItems)
            {
                var sub = item?.SubProject;
                if (sub != null) Collect(sub, files, depth + 1);
            }
        }
        // A project the automation model will not open is one this check skips. The alternative — failing the
        // run — would take the whole check away because of one project that is not a test project anyway.
        catch (Exception) { }
    }

    /// <summary>The test assemblies to run, and how many test projects were found. Each project file is read
    /// once: the same text decides whether it is a test project and what its assembly is called.</summary>
    private static (List<string> Assemblies, int TestProjects) Assemblies(List<string> projectFiles)
    {
        var assemblies = new List<string>();
        var testProjects = 0;
        foreach (var file in projectFiles)
        {
            string text;
            try { text = File.ReadAllText(file); } catch (Exception) { continue; }
            if (!TestProject.IsTestProject(text)) continue;
            testProjects++;
            if (assemblies.Count >= TestProject.MaxAssemblies) continue;
            var assembly = TestProject.FindBuiltAssembly(file, text);
            if (assembly != null) assemblies.Add(assembly);
        }
        return (assemblies, testProjects);
    }

    /// <summary>Why a run that finished said nothing usable. The runner's own complaint comes first when it
    /// made one — "the test source file was not found" is worth more than anything this class could say — and
    /// the fallback covers a run killed before it printed a summary, where no line explains it.</summary>
    private static string Reason(CommandExecutionResult result)
    {
        var line = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput);
        if (line != null) return "the test run reported no result — " + line;
        return result.OutputTruncated
            ? "the test run printed more output than could be read"
            : "the test run reported no result";
    }

    private static string? FirstLine(string? text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            // The runner's per-diagnostic echo of a failure duplicates the block below it, and a reason drawn
            // from it would name a failure as the reason there is no result.
            if (line.Length == 0 || line.StartsWith("[xUnit.net", StringComparison.Ordinal)) continue;
            return ChatStateLimits.OneLine(line, 160);
        }
        return null;
    }
}
