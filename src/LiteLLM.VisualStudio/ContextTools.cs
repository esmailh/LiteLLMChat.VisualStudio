using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

/// <summary>What a tool hands back: <see cref="Content"/> goes to the model, <see cref="Summary"/> is the one line the chat shows.
/// Keeping them apart is the point — a file read must never be echoed into the conversation.</summary>
internal sealed class ToolResult
{
    internal string Content = "";
    internal string Summary = "";
    internal string? Detail;
    internal bool Ok = true;
}

/// <summary>File and editor tools. Every read is bounded and confined to the workspace — the solution and any
/// project the user added to this conversation — so "look at this file" costs a few hundred lines of context
/// instead of the whole file, and the model can fetch what it needs itself.</summary>
internal static class ContextTools
{
    private const int MaxLines = 400;               // ceiling for one read_file call
    private const int DefaultLines = 200;
    private const int MaxContentChars = 60000;      // guard against minified or single-line files
    private const int MaxSelectionChars = 20000;
    private const int MaxEntries = 300;             // list_files results
    private const int MaxMatches = 80;              // search_files results
    private const int MaxScannedFileBytes = 1024 * 1024;

    /// <summary>The file kinds search_files will open. Also what the Add Context picker lists, so the picker cannot offer a file the agent then refuses to read.</summary>
    internal static readonly string[] ScannableExtensions = { ".cs", ".xaml", ".xml", ".json", ".config", ".csproj", ".props", ".targets", ".md", ".txt", ".yml", ".yaml", ".sql", ".js", ".ts", ".css", ".html", ".razor", ".ps1", ".sh", ".resx", ".vsct", ".pkgdef" };

    internal static readonly IReadOnlyList<ToolDefinition> Definitions = new[]
    {
        Define("get_current_file_path", "Path of the file currently open in the editor.", null),
        Define("get_selected_text", "The text the user has selected in the active editor.", null),
        Define("read_file", "Read part of a file straight from disk. Use this instead of asking the user to paste a file. Returns at most " + MaxLines + " lines per call; call it again with a larger start_line to continue.",
            new JObject { ["path"] = Str("Path in the workspace: solution-relative, absolute, or ProjectName/ path for an added project"), ["start_line"] = Int("1-based first line, default 1"), ["line_count"] = Int("Lines to return, default " + DefaultLines + ", max " + MaxLines) }, "path"),
        Define("list_files", "List files under a directory.",
            new JObject { ["path"] = Str("Directory, defaults to every project in the workspace"), ["pattern"] = Str("Glob such as *.cs, default *") }),
        Define("search_files", "Find a literal string across the workspace's text files and report file and line.",
            new JObject { ["query"] = Str("Text to find"), ["path"] = Str("Directory, defaults to every project in the workspace") }, "query"),
        Define("open_file", "Open a file in the editor and move the caret to a line, so the user sees the file you are talking about.",
            new JObject { ["path"] = Str("Path to open"), ["line"] = Int("1-based line to jump to") }, "path"),
        Define("get_solution_name", "Full path of the current solution.", null),
        Define("get_project_name", "Project containing the active document.", null)
    };

    /// <summary>The tools that only read the disk: no editor state beyond the solution root, no confirmation card.
    /// Consecutive calls of these may be run at the same time, because the answer to one cannot depend on the
    /// answer to another — which is the whole of what the loop's batching relies on.</summary>
    internal static bool IsDiskOnly(string name) =>
        name == "read_file" || name == "list_files" || name == "search_files";

    internal static async Task<ToolResult> ExecuteAsync(AsyncPackage package, string name, string arguments, CancellationToken token, Workspace? workspace = null)
    {
        token.ThrowIfCancellationRequested();
        var settings = LiteLlmPackage.Instance.Services.Settings;
        var refusal = ToolExecutionPolicy.Refusal(name, settings.EnableToolCalling, settings.EnableFileEditing, settings.EnableTerminalTools);
        if (refusal != null) return Fail(refusal, name + " · disabled");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await package.GetServiceAsync(typeof(DTE));
        // Answered here rather than by each tool, and answered with the reason: every tool below reads its inputs
        // out of this object, and an empty one turns a call the model got wrong into "No such file" against a
        // path it never sent. Reported before the dispatch, so nothing downstream needs to check for it.
        if (!ToolArguments.TryParse(arguments, out var args, out var argumentsError)) return Fail(argumentsError, "invalid arguments");
        var active = Try(() => dte?.ActiveDocument?.FullName) ?? "";
        // The turn's own workspace when the loop passes one — a tool call belongs to the turn that announced it,
        // not to whatever conversation happens to be selected when it runs.
        var turnWorkspace = workspace ?? LiteLlmPackage.Instance.Services.WorkspaceFor(SolutionRoot(dte));
        // Commands are the one thing that stays anchored to the solution: a working directory is one folder,
        // and a command typed against a second root would run somewhere the user did not see. The model is
        // told so, and an added project is built with its own command instead.
        var root = turnWorkspace.Primary;

        if (TerminalTools.Handles(name))
        {
            if (root == null) return Fail("No solution or active document is available for the command working directory.", "command · no working directory");
            return await TerminalTools.ExecuteAsync(root, name, args, LiteLlmPackage.Instance.Services.Settings.CommandTimeoutSeconds, token).ConfigureAwait(true);
        }
        if (name == "run_git")
        {
            if (root == null) return Fail("No solution or active document is available for the Git working directory.", "git · no working directory");
            return await GitTools.ExecuteAsync(root, args, LiteLlmPackage.Instance.Services.Settings.CommandTimeoutSeconds, token).ConfigureAwait(true);
        }
        if (name == "run_glab")
        {
            if (root == null) return Fail("No solution is open.", "glab · no working directory");
            return await GlabTools.ExecuteAsync(root, args, settings.CommandTimeoutSeconds, token).ConfigureAwait(true);
        }

        // The edit tools are named differently but resolved, confined and applied the same way, and the caller is one
        // switch statement that should not have to know which half a tool lives in.
        if (EditTools.Handles(name)) return await EditTools.ExecuteAsync(dte, turnWorkspace, name, args).ConfigureAwait(true);

        switch (name)
        {
            case "get_current_file_path":
                return active.Length == 0
                    ? Fail("No file is open in the editor.", "current file · none")
                    : Result(active, "current file · " + turnWorkspace.Relative(active));
            case "get_selected_text":
            {
                var selection = Try(() => (dte?.ActiveDocument?.Selection as TextSelection)?.Text) ?? "";
                if (selection.Trim().Length == 0) return Fail("The user has nothing selected in the editor.", "selection · empty");
                var lines = selection.Split('\n').Length;
                return Result(Clip(selection, MaxSelectionChars), "selection · " + lines + " lines");
            }
            case "get_solution_name":
            {
                var solution = Try(() => dte?.Solution?.FullName) ?? "";
                return solution.Length == 0 ? Fail("No solution is open.", "solution · none") : Result(solution, "solution · " + Path.GetFileName(solution));
            }
            case "get_project_name":
            {
                var project = Try(() => dte?.ActiveDocument?.ProjectItem?.ContainingProject?.Name) ?? "";
                return project.Length == 0 ? Fail("The active document has no project.", "project · none") : Result(project, "project · " + project);
            }
            case "open_file":
                return Open(dte, turnWorkspace, active, args);
        }
        return await Task.Run(() => FromDisk(name, args, turnWorkspace, active)).ConfigureAwait(true);
    }

    private static ToolResult FromDisk(string name, JObject args, Workspace workspace, string active)
    {
        try
        {
            switch (name)
            {
                case "read_file": return ReadFile(args, workspace);
                case "list_files": return ListFiles(args, workspace, active);
                case "search_files": return SearchFiles(args, workspace, active);
                default: return Fail("Unknown tool: " + name, "unknown tool");
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return Fail(ex.Message, "failed · " + ex.GetType().Name);
        }
    }

    private static ToolResult ReadFile(JObject args, Workspace workspace)
    {
        if (!ToolArguments.TryText(args, "path", out var path, out var error)) return Fail(error, "read · invalid arguments");
        // Named, because the schema calls it required and the model that omits it would otherwise be told the
        // solution directory does not exist — a sentence about a path it never asked for.
        if (path.Length == 0) return Fail("read_file needs a path. Call it again with {\"path\": \"src/Foo.cs\"}.", "read · no path");
        var full = workspace.Resolve(path);
        if (!File.Exists(full)) return Fail("No such file: " + full, "read · not found");
        if (!ToolArguments.TryNumber(args, "start_line", 1, out var start, out error)) return Fail(error, "read · invalid arguments");
        if (!ToolArguments.TryNumber(args, "line_count", DefaultLines, out var count, out error)) return Fail(error, "read · invalid arguments");
        start = Math.Max(1, start);
        count = Math.Min(MaxLines, Math.Max(1, count));
        var buffer = File.ReadLines(full).Skip(start - 1).Take(count + 1).ToList();
        if (buffer.Count == 0) return Fail("The file has fewer than " + start + " lines.", "read · past end of file");
        var more = buffer.Count > count;
        if (more) buffer.RemoveAt(buffer.Count - 1);
        var last = start + buffer.Count - 1;
        var text = Clip(string.Join("\n", buffer), MaxContentChars);
        var relative = workspace.Relative(full);
        var header = more
            ? relative + " lines " + start + "-" + last + " (more follows; call read_file again with start_line=" + (last + 1) + "):"
            : relative + " lines " + start + "-" + last + " (end of file):";
        return Result(header + "\n" + text, "read " + relative + " · lines " + start + "-" + last + (more ? " of more" : " · end"));
    }

    private static ToolResult ListFiles(JObject args, Workspace workspace, string active)
    {
        if (!ToolArguments.TryText(args, "path", out var path, out var error)) return Fail(error, "list · invalid arguments");
        if (!ToolArguments.TryText(args, "pattern", out var pattern, out error)) return Fail(error, "list · invalid arguments");
        var directories = SearchDirectories(workspace, active, path);
        // One root missing is a fact about that root, not a failure of the call: a project the user added and
        // then moved is skipped, and the listing that still worked is returned.
        var present = directories.Where(Directory.Exists).ToList();
        if (present.Count == 0) return Fail("No such directory: " + string.Join(", ", directories), "list · not found");
        if (pattern.Length == 0) pattern = "*";
        var files = new List<string>();
        var more = false;
        foreach (var directory in present)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Where(p => !IsNoise(p)))
            {
                if (files.Count >= MaxEntries) { more = true; break; }
                files.Add(file);
            }
            if (more) break;
        }
        var scope = Scope(workspace, present);
        if (files.Count == 0) return Fail("No files matched " + pattern + " under " + scope, "list · 0 files");
        var listing = string.Join("\n", files.Select(f => workspace.Relative(f)));
        return Result((more ? "First " + MaxEntries + " of more than " + MaxEntries : files.Count.ToString()) + " files under " + scope + ":\n" + listing,
            "list " + scope + " · " + files.Count + (more ? "+ files" : " files"));
    }

    private static ToolResult SearchFiles(JObject args, Workspace workspace, string active)
    {
        if (!ToolArguments.TryText(args, "query", out var query, out var error)) return Fail(error, "search · invalid arguments");
        if (query.Length == 0) return Fail("search_files needs a query.", "search · no query");
        if (!ToolArguments.TryText(args, "path", out var path, out error)) return Fail(error, "search · invalid arguments");
        var directories = SearchDirectories(workspace, active, path);
        var present = directories.Where(Directory.Exists).ToList();
        if (present.Count == 0) return Fail("No such directory: " + string.Join(", ", directories), "search · not found");
        var hits = new List<string>();
        var files = 0;
        var stopped = false;
        foreach (var directory in present)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (IsNoise(file) || !ScannableExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                var info = new FileInfo(file);
                if (info.Length > MaxScannedFileBytes) continue;
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
                var found = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(query, StringComparison.Ordinal) < 0) continue;
                    if (!found) { found = true; files++; }
                    hits.Add(workspace.Relative(file) + ":" + (i + 1) + ": " + lines[i].Trim().Truncate(200));
                    if (hits.Count >= MaxMatches) { stopped = true; break; }
                }
                if (stopped) break;
            }
            if (stopped) break;
        }
        var scope = Scope(workspace, present);
        if (hits.Count == 0) return Fail("No matches for \"" + query + "\".", "search · 0 matches");
        return Result((stopped ? "First " + MaxMatches + " matches" : hits.Count + " matches") + " for \"" + query + "\" in " + files + " files under " + scope + ":\n" + string.Join("\n", hits),
            "search \"" + query.Truncate(40) + "\" · " + hits.Count + " matches in " + files + " files");
    }

    private static ToolResult Open(DTE? dte, Workspace workspace, string active, JObject args)
    {
        try
        {
            if (!ToolArguments.TryText(args, "path", out var requested, out var error)) return Fail(error, "open · invalid arguments");
            var full = requested.Length == 0 ? active : workspace.Resolve(requested);
            if (full.Length == 0) return Fail("No file to open.", "open · no path");
            if (!File.Exists(full)) return Fail("No such file: " + full, "open · not found");
            if (!ToolArguments.TryNumber(args, "line", 0, out var line, out error)) return Fail(error, "open · invalid arguments");
            line = Math.Max(0, line);
            var relative = workspace.Relative(full);
            if (dte != null)
            {
                dte.ItemOperations.OpenFile(full);
                if (line > 0 && dte.ActiveDocument?.Selection is TextSelection selection) selection.GotoLine(line, false);
            }
            return Result("Opened " + relative + (line > 0 ? " at line " + line : "") + " in the editor.", "open " + relative + (line > 0 ? ":" + line : ""));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is System.Runtime.InteropServices.COMException)
        {
            return Fail("Could not open the file: " + ex.Message, "open · failed");
        }
    }

    /// <summary>Where a listing or a search looks: the directory the call named, or every root of the workspace
    /// when it named none — a tool told to search "the project" must not silently leave out half of what the
    /// user added. The open document's folder, then the process's own, are the fallbacks when the workspace has
    /// no root at all, which is the pair the tool had before a solution was required.</summary>
    private static List<string> SearchDirectories(Workspace workspace, string active, string path)
    {
        if (path.Length > 0) return new List<string> { workspace.Resolve(path) };
        if (workspace.Roots.Count > 0) return new List<string>(workspace.Roots);
        return new List<string> { active.Length > 0 ? Path.GetDirectoryName(active)! : Environment.CurrentDirectory };
    }

    /// <summary>How a listing names where it looked: one folder, or the whole workspace by the names the row
    /// already uses for each root.</summary>
    private static string Scope(Workspace workspace, List<string> directories) =>
        directories.Count == 1
            ? workspace.Relative(directories[0])
            : "every project (" + string.Join(", ", directories.Select(d => workspace.LabelOf(d))) + ")";

    internal static string? SolutionRoot(DTE? dte)
    {
        var solution = Try(() => dte?.Solution?.FullName);
        if (!string.IsNullOrEmpty(solution)) { var directory = Path.GetDirectoryName(solution); if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory; }
        var active = Try(() => dte?.ActiveDocument?.FullName);
        if (!string.IsNullOrEmpty(active)) { var directory = Path.GetDirectoryName(active); if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory; }
        return null;
    }

    internal static bool IsNoise(string path) =>
        path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
        path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
        path.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
        path.IndexOf(Path.DirectorySeparatorChar + ".vs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Clip(string value, int max) => value.Length <= max ? value : value.Substring(0, max) + "\n… [truncated, " + value.Length + " characters total]";
    private static ToolResult Result(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = true };
    private static ToolResult Fail(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = false };
    private static string? Try(Func<string?> read) { try { return read(); } catch (Exception) { return null; } }

    private static ToolDefinition Define(string name, string description, JObject? properties, params string[] required)
    {
        var schema = new JObject { ["type"] = "object", ["properties"] = properties ?? new JObject() };
        if (required.Length > 0) schema["required"] = new JArray(required.Cast<object>().ToArray());
        return new ToolDefinition { Function = new JObject { ["name"] = name, ["description"] = description, ["parameters"] = schema } };
    }
    private static JObject Str(string description) => new JObject { ["type"] = "string", ["description"] = description };
    private static JObject Int(string description) => new JObject { ["type"] = "integer", ["description"] = description };
}

internal static class ToolText
{
    /// <summary>Keeps a tool summary short enough to sit on one line.</summary>
    internal static string Truncate(this string value, int max) => value.Length <= max ? value : value.Substring(0, max - 1) + "…";
}
