using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>The file picker behind Add Context ▸ Files &amp; Folders and the composer's '#'. It lists the same files the
/// read tools will open — every root of the workspace, the scannable extensions, and the same bin/obj/.git/.vs
/// filter — so it can never offer something the agent would then refuse to read, and a file in an added project
/// is labelled the way the tools spell it.
///
/// The listing is built once and filtered in memory, because the filter is typed into and re-enumerating a solution
/// on every keystroke is what makes a picker feel slow.</summary>
public partial class FilePickerDialog : Window
{
    /// <summary>A large solution is cut off rather than left to hang the dialog, and the dialog says so.</summary>
    private const int MaxFiles = 6000;
    private const int MaxShown = 400;

    private readonly Workspace _workspace;
    private readonly List<FileEntry> _all = new List<FileEntry>();
    private readonly List<FileEntry> _shown = new List<FileEntry>();

    internal IReadOnlyList<string> Selected { get; private set; } = Array.Empty<string>();

    internal FilePickerDialog(Workspace workspace)
    {
        InitializeComponent();
        VsTheme.ApplyToDialog(this);
        _workspace = workspace;
        ScopeText.Text = workspace.Roots.Count == 0
            ? workspace.Primary ?? ""
            : string.Join("  ·  ", workspace.Roots);
        Read();
        Filter("");
        CountText.Text = _all.Count >= MaxFiles ? "Showing the first " + MaxFiles + " files" : _all.Count + " files";
        Loaded += (s, e) => SearchBox.Focus();
    }

    private sealed class FileEntry
    {
        internal string Full = "";
        internal string Label = "";
        public override string ToString() => Label;
    }

    private void Read()
    {
        var found = new List<string>();
        try
        {
            foreach (var root in _workspace.Roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (ContextTools.IsNoise(file)) continue;
                    if (Array.IndexOf(ContextTools.ScannableExtensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                    found.Add(file);
                    if (found.Count >= MaxFiles) break;
                }
                if (found.Count >= MaxFiles) break;
            }
        }
        catch (Exception) { }   // one unreadable subdirectory skips its own files rather than losing the whole list
        found.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var file in found) _all.Add(new FileEntry { Full = file, Label = _workspace.Relative(file).Replace('\\', '/') });
    }

    private void Filter(string query)
    {
        _shown.Clear();
        foreach (var file in _all)
        {
            if (query.Length > 0 && file.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _shown.Add(file);
            if (_shown.Count >= MaxShown) break;   // a filter that matches everything should not build a list nobody reads
        }
        List.ItemsSource = null;
        List.ItemsSource = _shown;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter(SearchBox.Text.Trim());

    /// <summary>Enter adds what is highlighted, or the only match when the filter has narrowed to one thing, so the
    /// keyboard can finish the job without reaching for the mouse.</summary>
    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (List.SelectedItems.Count == 0 && _shown.Count > 0) List.SelectedIndex = 0;
        Accept();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var chosen = List.SelectedItems.Count > 0
            ? List.SelectedItems.OfType<FileEntry>().Select(f => f.Full).ToList()
            : _shown.Take(1).Select(f => f.Full).ToList();
        if (chosen.Count == 0) return;
        Selected = chosen;
        DialogResult = true;
    }
}
