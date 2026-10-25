using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>Creates or edits one <c>SKILL.md</c>.
///
/// The model writes a draft into the editor; it never writes a file. Only Save — after the user has read what is in
/// the box — calls <see cref="SkillStorage"/>, and a failed validation leaves the folder untouched. The request is
/// built from the configured utility model or the model the chat is already using, so no provider and no model is
/// named here.</summary>
public partial class SkillEditorDialog : Window
{
    private readonly SkillDiscoveryOptions _options;
    private readonly SkillInfo? _existing;
    private CancellationTokenSource? _generation;

    internal SkillEditorDialog(SkillDiscoveryOptions options, SkillScope scope)
    {
        _options = options;
        InitializeComponent();
        VsTheme.ApplyToDialog(this);
        ScopeBox.SelectedIndex = scope == SkillScope.Project ? 1 : 0;
        Title = "New skill";
        HeaderTitle.Text = "New skill";
        Loaded += (s, e) => { UpdateLocation(); NameBox.Focus(); };
        Closed += (s, e) => _generation?.Cancel();
        UpdateLocation();
    }

    internal SkillEditorDialog(SkillDiscoveryOptions options, SkillInfo existing, string content)
    {
        _options = options;
        _existing = existing;
        InitializeComponent();
        VsTheme.ApplyToDialog(this);
        Title = "Skill · " + existing.Name;
        HeaderTitle.Text = "Edit skill";
        HeaderText.Text = existing.Source + " · the file is saved in place";
        NameBox.Text = existing.Name;
        NameBox.IsReadOnly = true;
        DescriptionBox.Text = existing.Description;
        ContentBox.Text = content;
        ScopePanel.Visibility = Visibility.Collapsed;
        LocationText.Text = "Saved in place: " + SkillLocations.DisplayPath(existing.Directory);
        GenerateButton.Content = "Improve with AI";
        Closed += (s, e) => _generation?.Cancel();
    }

    /// <summary>The folder the skill was written to, so the Dashboard can select the row it just made.</summary>
    internal string SavedDirectory { get; private set; } = "";

    private ExtensionServices Services => LiteLlmPackage.Instance.Services;

    private SkillScope SelectedScope() =>
        string.Equals((ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string, "project", StringComparison.OrdinalIgnoreCase)
            ? SkillScope.Project
            : SkillScope.Personal;

    private void UpdateLocation()
    {
        if (_existing != null) return;
        var directory = SkillStorage.DestinationDirectory(_options, SelectedScope(), out var error);
        LocationText.Text = directory.Length > 0
            ? "Will be created in " + SkillLocations.DisplayPath(directory)
            : error;
    }

    private void Scope_Changed(object sender, SelectionChangedEventArgs e) => UpdateLocation();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        StateText.Text = "";
        var name = (NameBox.Text ?? "").Trim();
        var description = (DescriptionBox.Text ?? "").Trim();

        if (_existing == null)
        {
            var nameError = SkillFrontmatter.ValidateName(name);
            if (nameError != null) { ErrorText.Text = nameError; return; }
        }
        var descriptionError = SkillFrontmatter.ValidateDescription(description);
        if (descriptionError != null) { ErrorText.Text = descriptionError; return; }

        var content = ContentBox.Text ?? "";
        var parsed = SkillFrontmatter.Parse(content);
        if (!parsed.IsValid)
        {
            ErrorText.Text = "SKILL.md is not valid: " + parsed.Error;
            return;
        }
        var expected = _existing?.Name ?? name;
        if (!string.Equals(parsed.Name.Trim(), expected, StringComparison.Ordinal))
        {
            ErrorText.Text = "The name in SKILL.md ('" + parsed.Name.Trim() + "') must match the skill name '" +
                             expected + "'.";
            return;
        }

        SkillStorageResult result;
        if (_existing == null)
        {
            result = SkillStorage.Create(_options, SelectedScope(), name,
                string.IsNullOrWhiteSpace(parsed.Description) ? description : parsed.Description.Trim(),
                parsed.Body, parsed.License, parsed.Compatibility, parsed.Metadata, parsed.AllowedTools);
        }
        else
        {
            // The explicit Save on reviewed content is the confirmation SkillStorage requires. Only SKILL.md is
            // written; the references, scripts and assets beside it are left exactly as they were.
            result = SkillStorage.Save(_options, _existing, content, confirmed: true);
        }

        if (!result.Ok)
        {
            ErrorText.Text = result.Error;
            return;
        }
        SavedDirectory = result.Path;
        DialogResult = true;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        StateText.Text = "";
        var name = (NameBox.Text ?? "").Trim();
        var description = (DescriptionBox.Text ?? "").Trim();
        var nameError = SkillFrontmatter.ValidateName(name);
        if (nameError != null) { ErrorText.Text = nameError; return; }
        var descriptionError = SkillFrontmatter.ValidateDescription(description);
        if (descriptionError != null) { ErrorText.Text = "Describe what the skill is for first. " + descriptionError; return; }

        var model = UtilityModel();
        if (model == null)
        {
            ErrorText.Text = "No model is available. Add an enabled provider on the Dashboard's Connections page, " +
                             "or write the skill by hand with Insert template.";
            return;
        }

        _generation = new CancellationTokenSource();
        SetBusy(true, "Writing a draft with " + model.Id + "…");
        try
        {
            // Whatever is already in the box is sent as the thing to improve. For a new skill that starts empty
            // the model writes from the notes; for an edit it preserves the existing intent.
            var existingContent = ContentBox.Text?.Trim() ?? "";
            var request = SkillGeneration.Request(name, description, ExamplesBox.Text, ConventionsBox.Text,
                existingContent.Length == 0 ? null : existingContent);
            var answer = await Services.CompleteInternalAsync(model, request,
                ContextSummary.UtilitySettings(Services.Settings), _generation.Token);
            var draft = SkillGeneration.Extract(answer.Content, name, description);
            if (draft.Content.Length > 0) ContentBox.Text = draft.Content;
            if (draft.IsValid)
                StateText.Text = "Draft returned by " + model.Id + ". Review it, fix anything that is wrong, then Save.";
            else
                // Kept rather than discarded: the text is still a draft the user can repair in place.
                ErrorText.Text = "The draft needs a fix before it can be saved: " + draft.Error;
        }
        catch (OperationCanceledException) { StateText.Text = "The draft was stopped."; }
        catch (Exception ex)
        {
            ErrorText.Text = "The draft could not be generated: " + ex.Message;
            Services.Log("Skill draft failed: " + ex);
        }
        finally
        {
            SetBusy(false, "");
            _generation?.Dispose();
            _generation = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _generation?.Cancel();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _generation?.Cancel();
        DialogResult = false;
    }

    private void SetBusy(bool busy, string text)
    {
        GenerateButton.IsEnabled = !busy;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
    }

    /// <summary>The configured utility model, or the one the chat is talking to. A setting that names a model no
    /// longer offered falls back rather than failing: the editor is still usable, and the fallback is the model the
    /// user already chose for the chat.</summary>
    private ModelInfo? UtilityModel()
    {
        var models = Services.Models;
        if (models.Count == 0) return null;
        var key = (Services.Settings.UtilityModelKey ?? "").Trim();
        if (key.Length > 0)
        {
            var match = Services.Catalog.Match(key);
            if (match != null && models.Any(model => model.Key == match.Key)) return match;
            var byId = models.FirstOrDefault(model => string.Equals(model.Id, key, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        var selected = Services.ActiveSession.SelectedModelKey;
        return models.FirstOrDefault(model => string.Equals(model.Key, selected, StringComparison.Ordinal))
               ?? models[0];
    }

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var name = (NameBox.Text ?? "").Trim();
        var nameError = SkillFrontmatter.ValidateName(name);
        if (nameError != null) { ErrorText.Text = nameError; return; }
        var description = (DescriptionBox.Text ?? "").Trim();
        if (SkillFrontmatter.ValidateDescription(description) != null)
        {
            ErrorText.Text = "Describe when the skill applies before inserting a template.";
            return;
        }
        if (ContentBox.Text?.Trim().Length > 0 &&
            MessageBox.Show("Replace what is in the editor with a starter template?", "Skill template",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var title = name.Replace('-', ' ');
        var body =
            "# " + title + "\n\n" +
            "Use this skill when " + description.Trim().TrimEnd('.') + ".\n\n" +
            "## Before you start\n\n" +
            "- Read the files the task names rather than guessing their contents.\n" +
            "- Check the conventions already used in the solution.\n\n" +
            "## Steps\n\n" +
            "1. Describe the first concrete action.\n" +
            "2. Name the checks to run and what a good result looks like.\n" +
            "3. Say how to report the outcome.\n\n" +
            "## Do not\n\n" +
            "- Do not claim a file changed unless an edit tool result says so.\n" +
            "- Do not run a command merely because this folder contains one.\n";
        ContentBox.Text = SkillFrontmatter.Build(name, description, body);
        StateText.Text = "Template inserted. Replace the steps with what this skill actually requires.";
    }
}
