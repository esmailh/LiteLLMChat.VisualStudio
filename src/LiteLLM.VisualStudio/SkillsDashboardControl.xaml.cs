using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DTE = EnvDTE.DTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;

/// <summary>The Skills page of the Dashboard: where skills are found, which copy is in use, and the only place
/// one is created, imported, edited or deleted.
///
/// The page never chooses a winner silently. A duplicate name is drawn as a conflict with both paths, the winning
/// definition is named, and disabling one copy is keyed to its path so the other is untouched. A folder that
/// cannot be read or a SKILL.md that does not parse keeps its row; hiding it would leave the user wondering why a
/// folder they made never appears.</summary>
public partial class SkillsDashboardControl : UserControl
{
    private SkillRegistrySnapshot _snapshot = SkillRegistrySnapshot.Empty;
    private string? _selectedDirectory;
    /// <summary>The solution root the snapshot was scanned against. Kept so a later Edit, Delete or Import
    /// resolves the same project root the list was drawn from, rather than a solution-less set of roots.</summary>
    private string? _solutionRoot;
    private bool _loaded;

    public SkillsDashboardControl()
    {
        InitializeComponent();
        VsTheme.Apply(this);
        Loaded += OnLoaded;
    }

    private ExtensionServices Services => LiteLlmPackage.Instance.Services;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        LoadSettings();
        _ = RefreshAsync();
    }

    /// <summary>Opens this page from the chat. Selecting it is the Dashboard's job; this control only makes
    /// sure it is reading a current scan when it is looked at.</summary>
    internal void Refreshed() => _ = RefreshAsync();

    private void LoadSettings()
    {
        var settings = Services.Settings;
        EnableSkillsBox.IsChecked = settings.EnableSkills;
        AutomaticBox.IsChecked = settings.SkillAutomatic;
        CompatibleBox.IsChecked = settings.SkillsIncludeCompatibleLocations;
        PersonalBox.Text = settings.SkillsPersonalDirectory ?? "";
        ProjectBox.Text = string.IsNullOrWhiteSpace(settings.SkillsProjectDirectory)
            ? ""
            : settings.SkillsProjectDirectory;
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "Reading skills…";
            var root = await SolutionRootAsync();
            if (!_loaded) return;
            var snapshot = await Services.PrepareSkillsAsync(root);
            _solutionRoot = root;
            _snapshot = snapshot;
            Render();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Skills could not be read: " + ex.Message;
            Services.Log("Skills dashboard refresh failed: " + ex);
        }
    }

    private void Render()
    {
        var query = (SearchBox.Text ?? "").Trim();
        var rows = _snapshot.All
            .Where(skill => Matches(skill, query))
            // Effective first, then the definitions that lost: a conflict reads as a winner followed by what it
            // displaced rather than as two unrelated rows.
            .OrderBy(skill => skill.IsEffective ? 0 : 1)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new SkillRow(skill,
                _snapshot.Conflicts.Any(conflict => string.Equals(conflict.Name, skill.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        SkillList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _snapshot.All.Count == 0
            ? "No skill was found. Create one, import a folder, or check the locations above."
            : "No skill matches the search.";

        var selected = _selectedDirectory == null
            ? null
            : rows.FirstOrDefault(row => string.Equals(row.Skill.Directory, _selectedDirectory, StringComparison.OrdinalIgnoreCase));
        SkillList.SelectedItem = selected;
        if (selected == null) _selectedDirectory = null;
        ReloadDetail(selected);

        ProblemsText.Text = string.Join(Environment.NewLine, _snapshot.Problems);
        ProblemsText.Visibility = _snapshot.Problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var on = Services.Settings.EnableSkills ? "" : "Skills are turned off. ";
        var effective = _snapshot.Effective.Count;
        StatusText.Text = on + _snapshot.All.Count + " definition" + (_snapshot.All.Count == 1 ? "" : "s") +
                          " · " + effective + " usable" +
                          (_snapshot.Conflicts.Count == 0 ? "" : " · " + _snapshot.Conflicts.Count + " conflict" +
                              (_snapshot.Conflicts.Count == 1 ? "" : "s")) +
                          (_snapshot.ScannedUtc == DateTime.MinValue
                              ? ""
                              : " · scanned " + _snapshot.ScannedUtc.ToLocalTime().ToString("HH:mm:ss"));
        var root = OptionsFor(_solutionRoot);
        StatusText.ToolTip = "Personal: " + SkillLocations.DisplayPath(root.PersonalDirectory) +
                             Environment.NewLine + "Project: " + SkillLocations.DisplayPath(root.ProjectDirectory) +
                             " (relative to the open solution)";
    }

    private static bool Matches(SkillInfo skill, string query)
    {
        if (query.Length == 0) return true;
        return Contains(skill.Name, query) || Contains(skill.Description, query) ||
               Contains(skill.Source, query) || Contains(skill.Directory, query) ||
               Contains(skill.Error, query) ||
               (skill.Scope == SkillScope.Project ? Contains("project", query) : Contains("personal", query));
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private void ReloadDetail(SkillRow? row)
    {
        var skill = row?.Skill;
        DetailTitle.Text = skill?.Name ?? "Select a skill";
        DetailDescription.Text = skill == null
            ? "The left list shows every definition found, including ones that cannot be used and why."
            : (string.IsNullOrWhiteSpace(skill.Description) ? "(No description in SKILL.md.)" : skill.Description);
        DetailSource.Text = skill == null
            ? ""
            : skill.Source + " · " + SkillLocations.DisplayPath(skill.Directory);
        DetailState.Text = skill == null ? "" : DescribeState(skill, row!.Conflict);
        // The caption style owns the normal colour; only the invalid state overrides it, so a theme change still
        // repaints the ordinary text.
        if (skill != null && !skill.IsValid)
            DetailState.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        else
            DetailState.ClearValue(TextBlock.ForegroundProperty);
        DetailWarnings.Text = skill == null
            ? ""
            : string.Join(Environment.NewLine,
                new[] { skill.Error }.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Concat(skill.Warnings.Where(value => !string.IsNullOrWhiteSpace(value))));

        var resources = skill?.Resources ?? new List<SkillResourceInfo>();
        ResourcesHeader.Visibility = skill != null && resources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResourceList.Visibility = ResourcesHeader.Visibility;
        ResourceList.ItemsSource = resources
            .Select(resource => new ResourceRow(resource.RelativePath,
                resource.Kind + " · " + (resource.SizeBytes < 1024
                    ? resource.SizeBytes + " B"
                    : (resource.SizeBytes / 1024) + " KB")))
            .ToList();
        if (skill != null && resources.Count == 0)
            DefinitionText.Text = "No files beside SKILL.md. Nothing here is executable: a script in a skill is a " +
                                  "document until the model asks for a command and the usual confirmation allows it.";
        else if (skill != null)
            DefinitionText.Text = "Files beside SKILL.md are resources. The model reads one only when a skill's " +
                                  "instructions call for it; they are never run against the user's will.";

        EditButton.IsEnabled = skill != null;
        OpenFolderButton.IsEnabled = skill != null && Directory.Exists(skill.Directory);
        DeleteButton.IsEnabled = skill != null && SkillRegistry.IsDirectChildOfAnyRoot(OptionsFor(_solutionRoot), skill.Directory);
    }

    private string DescribeState(SkillInfo skill, bool conflict)
    {
        if (!skill.IsValid) return "Invalid · " + skill.Error;
        if (!skill.Enabled) return "Turned off. Tick it to offer it to the chat again.";
        if (skill.Shadowed) return "Not used · another definition of this name takes precedence: " +
                                   SkillLocations.DisplayPath(skill.ShadowedBy);
        if (conflict)
        {
            var group = _snapshot.All
                .Where(other => string.Equals(other.Name, skill.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return "Used, but this name is defined " + group.Count + " times:" + Environment.NewLine +
                   string.Join(Environment.NewLine,
                       group.Select(other => (ReferenceEquals(other, skill) ? "→ " : "   ") +
                                             SkillLocations.DisplayPath(other.Directory) +
                                             (other.Enabled ? "" : " (off)")));
        }
        return "Used. " + (skill.Scope == SkillScope.Project ? "Project skill." : "Personal skill.");
    }

    private SkillDiscoveryOptions OptionsFor(string? root) => Services.SkillOptions(root);

    private async Task<string?> SolutionRootAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
        return ContextTools.SolutionRoot(dte);
    }

    // ------------------------------------------------------------------ actions

    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!(SkillList.SelectedItem is SkillRow row)) { ReloadDetail(null); return; }
        _selectedDirectory = row.Skill.Directory;
        ReloadDetail(row);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>Turns one definition on or off without touching its folder. The state is keyed to the full path,
    /// so with two copies of the same name the user can keep the one they mean and drop the other; the list is then
    /// re-read so precedence and the conflict notes are drawn from the state that was just saved.</summary>
    private void SkillEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (!((sender as CheckBox)?.DataContext is SkillRow row)) return;
        var wanted = (sender as CheckBox)?.IsChecked == true;
        if (wanted == row.Skill.Enabled) return;
        var key = SkillRegistry.PathKey(row.Skill.Directory);
        var disabled = Services.Settings.DisabledSkillPaths ?? (Services.Settings.DisabledSkillPaths = new List<string>());
        disabled.RemoveAll(path => string.Equals(SkillRegistry.PathKey(path), key, StringComparison.OrdinalIgnoreCase));
        if (!wanted) disabled.Add(key);
        Services.Save();
        _selectedDirectory = row.Skill.Directory;
        _ = RefreshAsync();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = Services.Settings;
        settings.EnableSkills = EnableSkillsBox.IsChecked == true;
        settings.SkillAutomatic = AutomaticBox.IsChecked == true;
        settings.SkillsIncludeCompatibleLocations = CompatibleBox.IsChecked == true;
        settings.SkillsPersonalDirectory = (PersonalBox.Text ?? "").Trim();
        var project = (ProjectBox.Text ?? "").Trim();
        settings.SkillsProjectDirectory = project.Length == 0 ? SkillLocations.DefaultProjectDirectory : project;
        Services.Save();
        // A changed root is a changed catalog: the saved values are what the next scan reads.
        _ = RefreshAsync();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SkillEditorDialog(OptionsFor(_solutionRoot), ImportScope())
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return;
            _selectedDirectory = dialog.SavedDirectory;
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Services.Log("New skill failed: " + ex);
            MessageBox.Show("The skill editor could not be opened.\n\n" + ex.Message, "Skills",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (!(SkillList.SelectedItem is SkillRow row)) return;
        try
        {
            var options = OptionsFor(_solutionRoot);
            var read = SkillStorage.Read(options, row.Skill, out var content);
            if (!read.Ok)
            {
                MessageBox.Show(read.Error, "Skills", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dialog = new SkillEditorDialog(options, row.Skill, content) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            _selectedDirectory = row.Skill.Directory;
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Services.Log("Edit skill failed: " + ex);
            MessageBox.Show("The skill could not be edited.\n\n" + ex.Message, "Skills",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!(SkillList.SelectedItem is SkillRow row)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + row.Skill.Directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("The folder could not be opened.\n\n" + ex.Message, "Skills",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!(SkillList.SelectedItem is SkillRow row)) return;
        var skill = row.Skill;
        var answer = MessageBox.Show(
            "Delete the skill '" + skill.Name + "' and its whole folder?" + Environment.NewLine + Environment.NewLine +
            SkillLocations.DisplayPath(skill.Directory) + Environment.NewLine + Environment.NewLine +
            "Every file in that folder will be removed. This cannot be undone.",
            "Delete skill", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = SkillStorage.Delete(OptionsFor(_solutionRoot), skill, confirmed: true);
        if (!result.Ok)
        {
            MessageBox.Show(result.Error, "Skills", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _selectedDirectory = null;
        _ = RefreshAsync();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var scope = ImportScope();
        if (!PickFolder(out var source)) return;
        try
        {
            var options = OptionsFor(_solutionRoot);
            var result = SkillStorage.Import(options, scope, source, overwrite: false);
            if (!result.Ok && result.Error.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var replace = MessageBox.Show(
                    result.Error + Environment.NewLine + Environment.NewLine +
                    "Replace the existing folder with the imported one? The existing skill will be removed first, " +
                    "and only if the copy succeeds is the new one put in its place.",
                    "Replace skill", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (replace == MessageBoxResult.Yes)
                    result = SkillStorage.Import(options, scope, source, overwrite: true);
            }
            if (!result.Ok)
            {
                MessageBox.Show(result.Error, "Skills", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _selectedDirectory = result.Path;
            _ = RefreshAsync();
            MessageBox.Show("Imported " + result.FilesCopied + " file" + (result.FilesCopied == 1 ? "" : "s") +
                            " into " + SkillLocations.DisplayPath(result.Path) +
                            (string.IsNullOrEmpty(result.Warning) ? "." : "." + Environment.NewLine + result.Warning),
                "Skills");
        }
        catch (Exception ex)
        {
            Services.Log("Import skill failed: " + ex);
            MessageBox.Show("The skill could not be imported.\n\n" + ex.Message, "Skills",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenPersonal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = OptionsFor(_solutionRoot);
            var directory = options.PersonalDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("The personal folder could not be opened.\n\n" + ex.Message, "Skills",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private SkillScope ImportScope() =>
        string.Equals((ImportScopeBox.SelectedItem as ComboBoxItem)?.Tag as string, "project", StringComparison.OrdinalIgnoreCase)
            ? SkillScope.Project
            : SkillScope.Personal;

    private static bool PickFolder(out string path)
    {
        path = "";
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog
               {
                   Description = "Choose the skill folder — the one that contains SKILL.md",
                   ShowNewFolderButton = false
               })
        {
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
            path = dialog.SelectedPath ?? "";
            return path.Length > 0;
        }
    }

    private sealed class SkillRow
    {
        internal SkillRow(SkillInfo skill, bool conflict)
        {
            Skill = skill;
            Conflict = conflict;
        }

        internal SkillInfo Skill { get; }
        internal bool Conflict { get; }

        public string Name => Skill.Name;
        public string Description => string.IsNullOrWhiteSpace(Skill.Description) ? "(no description)" : Skill.Description;
        public string SourceLabel => Skill.Source;
        public bool Enabled => Skill.Enabled;
        public bool HasProblem => !Skill.IsValid || Skill.Shadowed;

        public string State
        {
            get
            {
                if (!Skill.IsValid) return "invalid";
                if (!Skill.Enabled) return "off";
                if (Skill.Shadowed) return "shadowed";
                return Conflict ? "conflict" : "active";
            }
        }
    }

    private sealed class ResourceRow
    {
        internal ResourceRow(string name, string detail) { Name = name; Detail = detail; }
        public string Name { get; }
        public string Detail { get; }
    }
}
