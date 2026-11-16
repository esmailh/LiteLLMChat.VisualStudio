using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LiteLLM.Core;
using Microsoft.VisualStudio.PlatformUI;

namespace LiteLLM.VisualStudio;

/// <summary>The projects chip: the folders this conversation's tools may reach besides the solution.
///
/// The list belongs to the conversation rather than to the settings, because it is a statement about what this
/// chat is working on — a new conversation should not silently inherit access to a folder the user added for the
/// previous one. Adding is saved with the chat and drawn as a row in it, so a reader scrolling back can see when
/// the conversation reached past the solution, and removing is saved the same way.</summary>
public partial class ChatControl
{
    private void ProjectChip_Click(object sender, MouseButtonEventArgs e)
    {
        var extras = Services.ActiveSession.ExtraRoots ?? new List<string>();
        var entries = new List<object?>();
        foreach (var root in extras)
        {
            var path = root;
            var missing = !Directory.Exists(path);
            entries.Add(Item("Remove " + ProjectName(path) + (missing ? " (missing)" : ""), () => RemoveProject(path)));
        }
        if (entries.Count > 0) entries.Add(null);
        entries.Add(Item("Add project folder…", () => _ = AddProjectAsync()));
        if (extras.Count > 0) entries.Add(Item("Copy project paths", () => Copy(ProjectList(), "Projects")));

        var menu = Menu(entries.ToArray());
        menu.PlacementTarget = ProjectChip;
        menu.Placement = PlacementMode.Top;
        ProjectChip.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Redraws the chip from the active conversation. The glyph in front of the label is the XAML's, so
    /// this only writes the name: quiet with no projects — the chip is still there, because it is also the only
    /// way to add the first one — and named once there are some, since a count alone would leave the user unable
    /// to tell which folder a path belongs to.</summary>
    private void RefreshProjectChip()
    {
        var extras = Services.ActiveSession.ExtraRoots ?? new List<string>();
        ProjectChipText.Text = extras.Count switch
        {
            0 => "Project",
            1 => ProjectName(extras[0]),
            _ => extras.Count + " projects"
        };
        // Accent once the conversation reaches past the solution, the same pair the MCP chip uses, so the two read
        // as one row of state rather than as unrelated controls.
        ProjectChip.SetResourceReference(Border.BorderBrushProperty,
            extras.Count > 0 ? EnvironmentColors.AccentMediumBrushKey : (object)CommonDocumentColors.ListItemBorderBrushKey);

        var lines = new List<string>();
        if (extras.Count == 0)
        {
            lines.Add("The agent works in the open solution. Add a project folder to let it read and change that too.");
        }
        else
        {
            lines.Add("Projects added to this conversation:");
            foreach (var root in extras)
                lines.Add("· " + ProjectName(root) + (Directory.Exists(root) ? "  (" + root + ")" : "  — missing: " + root));
            lines.Add("Paths are addressed as " + ProjectName(extras[0]) + "/src/Foo.cs. Commands still run from the solution; the automatic build and test check covers the solution only.");
        }
        ProjectChip.ToolTip = string.Join("\n", lines);
    }

    /// <summary>Adds one folder, after asking the resolver whether it would add anything: a folder already inside
    /// a root is reachable without it, and one that contains a root would list that root's files twice. Both are
    /// refused with the reason rather than saved and puzzled over later.</summary>
    private async Task AddProjectAsync()
    {
        if (_generation != null) { StateText.Text = "Wait for the running turn to finish before adding a project."; return; }
        if (!PickProjectFolder(out var picked)) return;
        var root = Workspace.Normalize(picked);
        var workspace = Services.WorkspaceFor(await SolutionRootAsync());
        var refusal = workspace.WhyNotAdd(root);
        if (refusal != null) { StateText.Text = refusal; return; }

        var session = Services.ActiveSession;
        var extras = session.ExtraRoots ?? (session.ExtraRoots = new List<string>());
        extras.Add(root);
        SaveSession();
        // The mention list is cached by root, and the cached one no longer describes this workspace.
        _mentionRoot = null;
        RefreshProjectChip();
        AddToolNote("project", "Project added · " + ProjectName(root) + " (" + root + ")", true, null,
            "Files in this folder are addressed as " + ProjectName(root) + "/path, and the agent may read and change them. Commands still run from the solution directory; the automatic build and test check covers the solution only.", null);
        StateText.Text = "Added " + ProjectName(root) + " to this conversation";
    }

    private void RemoveProject(string root)
    {
        var session = Services.ActiveSession;
        var extras = session.ExtraRoots ?? new List<string>();
        var existing = extras.FirstOrDefault(value => string.Equals(value, root, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        // A removal is a narrowing of what the agent may touch, so it takes effect at once — but nothing it wrote
        // is put back, and the row says which folder is no longer in reach rather than implying the files went too.
        extras.Remove(existing);
        SaveSession();
        _mentionRoot = null;
        RefreshProjectChip();
        AddToolNote("project", "Project removed · " + ProjectName(existing) + " is no longer reachable in this conversation", true);
        StateText.Text = "Removed " + ProjectName(existing);
    }

    private static bool PickProjectFolder(out string path)
    {
        path = "";
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog
               {
                   Description = "Choose the project folder the agent may also work in",
                   ShowNewFolderButton = false
               })
        {
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
            path = dialog.SelectedPath ?? "";
            return path.Length > 0;
        }
    }

    /// <summary>The folder's own name, which is what the row and the chip call it. The resolver may number a
    /// duplicate, so the full path is shown beside it wherever the name alone could be ambiguous.</summary>
    private static string ProjectName(string root) =>
        Path.GetFileName(Workspace.Normalize(root)) is { Length: > 0 } name ? name : Workspace.Normalize(root);

    private string ProjectList() => string.Join("\n", Services.ActiveSession.ExtraRoots ?? new List<string>());
}
