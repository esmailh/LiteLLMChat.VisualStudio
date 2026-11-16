using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LiteLLM.VisualStudio;

/// <summary>Builds the open solution the way Visual Studio's own Build Solution does, then reads the Error
/// List. Going through Visual Studio rather than an external MSBuild call is what makes this work for a
/// solution of any kind: each project is built by its own project system, where a command line would only
/// ever cover the MSBuild-based ones.</summary>
internal static class SolutionBuild
{
    /// <summary>Longest a build is waited on. One still running after this is reported as unverifiable
    /// rather than as clean, because "no errors" is a claim this must never make without knowing it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The Error List's window kind. The ErrorList/ErrorItem automation types are not in the
    /// interop assembly this project compiles against, so the window is found by GUID and read late-bound.</summary>
    internal const string ErrorListKind = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";

    /// <summary>The build event owns success; the Error List supplies diagnostic details only.</summary>
    internal static async Task<BuildVerification> VerifyAsync(CancellationToken token)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
        if (dte?.Solution == null || !dte.Solution.IsOpen || string.IsNullOrEmpty(dte.Solution.FullName)) return Unavailable("no saved solution is open");

        var manager = await LiteLlmPackage.Instance.GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
        if (manager == null) return Unavailable("the solution build manager is unavailable");

        // Every open document is written first. The edit tools already save the file they changed, so this is
        // for the rest: an unsaved document makes Visual Studio raise its own save prompt, and a prompt raised
        // inside this wait is a dialog nobody is there to answer.
        try { dte.Documents.SaveAll(); } catch (Exception) { }

        // The build reports finishing through the solution events rather than through a poll. Polling for
        // "not busy" can read the state before the build has started and then hand back the previous build's
        // Error List — a false "no errors" is the one answer this feature must never give.
        var finished = new TaskCompletionSource<BuildVerificationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new BuildEvents(status => finished.TrySetResult(status));
        BuildVerificationStatus status;
        uint cookie = 0;
        try
        {
            if (manager.AdviseUpdateSolutionEvents(events, out cookie) != 0) return Unavailable("build events could not be observed");
            dte.ExecuteCommand("Build.BuildSolution");
            using (token.Register(() => finished.TrySetCanceled()))
            {
                var winner = await Task.WhenAny(finished.Task, Task.Delay(Timeout, token)).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (winner != finished.Task) return new BuildVerification { Status = BuildVerificationStatus.TimedOut, Problem = "the build did not finish within ten minutes" };
                status = await finished.Task.ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unavailable("the build could not be started: " + ex.Message); }
        finally
        {
            if (cookie != 0) { try { manager.UnadviseUpdateSolutionEvents(cookie); } catch (Exception) { } }
        }

        var diagnostics = await ReadErrorListCoreAsync().ConfigureAwait(true);
        return new BuildVerification
        {
            Status = status,
            Diagnostics = diagnostics ?? "",
            Problem = status == BuildVerificationStatus.Cancelled ? "the build was cancelled" : ""
        };
    }

    private static BuildVerification Unavailable(string problem) => new BuildVerification { Status = BuildVerificationStatus.Unavailable, Problem = problem };

    /// <summary>The Error List as text, best-effort: it is empty in a solution that has not been built, which
    /// is not a failure worth reporting as one. This is deliberately language-agnostic — severity, message,
    /// file and line are what every project type puts here, so nothing below knows one language's error
    /// format from another's.</summary>
    internal static async Task<string> ReadErrorListAsync() => await ReadErrorListCoreAsync().ConfigureAwait(true) ?? "";

    private static async Task<string?> ReadErrorListCoreAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var window = dte?.Windows.Item(ErrorListKind)?.Object;
            if (window == null) return null;
            if (window.GetType().InvokeMember("ErrorItems", BindingFlags.GetProperty, null, window, null) is not System.Collections.IEnumerable items) return null;

            var text = new StringBuilder();
            foreach (var item in items)
            {
                if (text.Length >= BuildDiagnostics.MaxDetailChars) break;
                var type = item.GetType();
                string Read(string name) { try { return type.InvokeMember(name, BindingFlags.GetProperty, null, item, null)?.ToString() ?? ""; } catch (Exception) { return ""; } }

                text.Append(Read("ErrorLevel").Replace("vsErrorType", "").ToLowerInvariant()).Append(": ").Append(Read("Description"));
                var file = Read("FileName");
                if (file.Length > 0) text.Append(" — ").Append(file).Append(':').Append(Read("Line"));
                text.Append('\n');
            }
            return text.ToString().TrimEnd('\n');
        }
        catch (Exception) { return null; }
    }

    /// <summary>The solution-events sink, whose only job is to say when the build has finished. Visual Studio
    /// calls the members on the UI thread; the ones that report progress are not needed, so they are empty.</summary>
    private sealed class BuildEvents : IVsUpdateSolutionEvents2
    {
        private readonly Action<BuildVerificationStatus> _done;
        internal BuildEvents(Action<BuildVerificationStatus> done) { _done = done; }

        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => 0;
        public int UpdateSolution_Begin(ref int pfCancelUpdate) => 0;
        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) { _done(fCancelCommand != 0 ? BuildVerificationStatus.Cancelled : fSucceeded != 0 ? BuildVerificationStatus.Succeeded : BuildVerificationStatus.Failed); return 0; }
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => 0;
        // Cancellation completes the wait, but can never authorize tests against leftover assemblies.
        public int UpdateSolution_Cancel() { _done(BuildVerificationStatus.Cancelled); return 0; }
        public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel) => 0;
        public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => 0;
    }
}
