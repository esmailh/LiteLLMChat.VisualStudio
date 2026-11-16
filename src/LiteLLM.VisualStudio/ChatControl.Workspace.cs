using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;

public partial class ChatControl
{
    private readonly Dictionary<Guid, List<AppliedEdit>> _checkpoints = new Dictionary<Guid, List<AppliedEdit>>();
    private readonly Dictionary<Guid, Expander> _changeSections = new Dictionary<Guid, Expander>();
    private int _approvalsWaiting;
    private bool _wasBusy;
    internal Action<string>? ActivityCaptionChanged;
    internal void RefreshActivityCaption() => UpdateActivity();

    private void UpdateActivity()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateActivity)); return; }
        var running = _wasBusy && !_disposed;
        ActivityDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ActivityDot.Fill = _approvalsWaiting > 0 ? Brushes.Goldenrod : Brushes.LimeGreen;
        ActivityDot.ToolTip = _approvalsWaiting > 0 ? "Waiting for your approval" : _phase;
        // The shell owns tab colours. A coloured glyph in the caption also survives auto-hide.
        ActivityCaptionChanged?.Invoke(running && !IsKeyboardFocusWithin
            ? (_approvalsWaiting > 0 ? "🟡 LiteLLM Chat" : "🟢 LiteLLM Chat") : "LiteLLM Chat");
    }
    private void ApprovalWaiting(bool waiting)
    {
        _approvalsWaiting = Math.Max(0, _approvalsWaiting + (waiting ? 1 : -1));
        UpdateActivity();
    }
    private void CaptureTurnChanges(Guid turnId, int revision)
    {
        var changes = EditTools.EditsSince(revision).ToList();
        if (!_checkpoints.TryGetValue(turnId, out var captured)) _checkpoints[turnId] = captured = new List<AppliedEdit>();
        foreach (var change in changes) if (!captured.Contains(change)) captured.Add(change);
        if (captured.Count < EditTools.Revisions - revision)
            AddToolNote("changes", "Checkpoint · some older edits exceeded the in-memory history cap and are unavailable", false);
        RefreshChanges(turnId);
        while (_checkpoints.Count > 20) _checkpoints.Remove(_checkpoints.Keys.First());
    }
    private void RefreshChanges(Guid turnId)
    {
        if (!_checkpoints.TryGetValue(turnId, out var changes) || changes.Count == 0) return;
        if (!_changeSections.TryGetValue(turnId, out var section) || !Messages.Children.Contains(section))
        {
            section = new Expander { Margin = new Thickness(0, 4, 0, 12), IsExpanded = false };
            _changeSections[turnId] = section;
            Messages.Children.Add(section);
        }
        section.Header = "Changes · " + changes.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() + " files";
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Captured LiteLLM file edits only; not Claude, terminal or MCP changes. In-memory checkpoints; restore refuses conflicts.", TextWrapping = TextWrapping.Wrap });
        foreach (var edit in changes)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBox { Text = edit.Diff, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            var actions = new WrapPanel();
            var compare = new Button { Content = "Open diff", Margin = new Thickness(3) };
            compare.Click += async (s, e) => { try { await OpenCheckpointDiffAsync(edit); } catch (Exception ex) { StateText.Text = ex.Message; } };
            var restore = new Button { Content = "Restore edit", Margin = new Thickness(3), IsEnabled = !edit.Undone };
            restore.Click += async (s, e) =>
            {
                if (_generation != null) { StateText.Text = "Stop the active turn before restoring files."; return; }
                if (MessageBox.Show("Restore " + edit.Label + "?", "Checkpoint", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await RestoreCheckpointAsync(edit, null); RefreshChanges(turnId);
            };
            actions.Children.Add(compare); actions.Children.Add(restore); body.Children.Add(actions);
            panel.Children.Add(new Expander { Header = edit.ToString(), Content = body, Margin = new Thickness(0, 6, 0, 0) });
        }
        var all = new Button { Content = "Restore this turn", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        all.Click += async (s, e) =>
        {
            if (_generation != null) { StateText.Text = "Stop the active turn before restoring files."; return; }
            if (MessageBox.Show("Restore all captured edits of this turn?", "Checkpoint", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await RestoreCheckpointAsync(null, changes); RefreshChanges(turnId);
        };
        panel.Children.Add(all); section.Content = panel;
    }
    private async Task RestoreCheckpointAsync(AppliedEdit? edit, IReadOnlyList<AppliedEdit>? changes)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE)) as DTE;
            StateText.Text = edit != null ? EditTools.Undo(dte, edit) : EditTools.UndoAll(dte, changes!);
        }
        catch (Exception ex) { StateText.Text = "Restore failed: " + ex.Message; }
    }
    private async Task OpenCheckpointDiffAsync(AppliedEdit edit)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var directory = Path.Combine(Path.GetTempPath(), "LiteLLM.VisualStudio", "diffs"); Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var before = Path.Combine(directory, id + "-before" + Path.GetExtension(edit.Path));
        var after = Path.Combine(directory, id + "-after" + Path.GetExtension(edit.Path));
        File.WriteAllText(before, edit.Before); File.WriteAllText(after, edit.After);
        var service = await LiteLlmPackage.Instance.GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
        if (service == null) throw new InvalidOperationException("Visual Studio diff service is unavailable.");
        service.OpenComparisonWindow2(before, after, "Checkpoint · " + edit.Label, "Captured before/after; not the current working copy", "Before", "After", "", "", 0);
    }
}
