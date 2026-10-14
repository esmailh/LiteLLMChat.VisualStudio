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
    internal bool Ok = true;
}

/// <summary>File and editor tools. Every read is bounded and confined to the solution, so "look at this file" costs
/// a few hundred lines of context instead of the whole file, and the model can fetch what it needs itself.</summary>
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
            new JObject { ["path"] = Str("Solution-relative or absolute path"), ["start_line"] = Int("1-based first line, default 1"), ["line_count"] = Int("Lines to return, default " + DefaultLines + ", max " + MaxLines) }, "path"),
        Define("list_files", "List files under a directory.",
            new JObject { ["path"] = Str("Directory, defaults to the solution root"), ["pattern"] = Str("Glob such as *.cs, default *") }),
        Define("search_files", "Find a literal string across the solution's text files and report file and line.",
            new JObject { ["query"] = Str("Text to find"), ["path"] = Str("Directory, defaults to the solution root") }, "query"),
        Define("open_file", "Open a file in the editor and move the caret to a line, so the user sees the file you are talking about.",
            new JObject { ["path"] = Str("Path to open"), ["line"] = Int("1-based line to jump to") }, "path"),
        Define("get_solution_name", "Full path of the current solution.", null),
        Define("get_project_name", "Project containing the active document.", null)
    };

    internal static async Task<ToolResult> ExecuteAsync(AsyncPackage package, string name, string arguments, CancellationToken token)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await package.GetServiceAsync(typeof(DTE));
        // Answered here rather than by each tool, and answered with the reason: every tool below reads its inputs
        // out of this object, and an empty one turns a call the model got wrong into "No such file" against a
        // path it never sent. Reported before the dispatch, so nothing downstream needs to check for it.
        if (!ToolArguments.TryParse(arguments, out var args, out var argumentsError)) return Fail(argumentsError, "invalid arguments");
        var active = Try(() => dte?.ActiveDocument?.FullName) ?? "";
        var root = SolutionRoot(dte);

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

        // The edit tools are named differently but resolved, confined and applied the same way, and the caller is one
        // switch statement that should not have to know which half a tool lives in.
        if (EditTools.Handles(name)) return await EditTools.ExecuteAsync(dte, root, name, args).ConfigureAwait(true);

        switch (name)
        {
            case "get_current_file_path":
                return active.Length == 0
                    ? Fail("No file is open in the editor.", "current file · none")
                    : Result(active, "current file · " + Relative(root, active));
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
                return Open(dte, root, active, args);
        }
        return await Task.Run(() => FromDisk(name, args, root, active)).ConfigureAwait(true);
    }

    private static ToolResult FromDisk(string name, JObject args, string? root, string active)
    {
        try
        {
            switch (name)
            {
                case "read_file": return ReadFile(args, root);
                case "list_files": return ListFiles(args, root, active);
                case "search_files": return SearchFiles(args, root, active);
                default: return Fail("Unknown tool: " + name, "unknown tool");
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return Fail(ex.Message, "failed · " + ex.GetType().Name);
        }
    }

    private static ToolResult ReadFile(JObject args, string? root)
    {
        if (!ToolArguments.TryText(args, "path", out var path, out var error)) return Fail(error, "read · invalid arguments");
        // Named, because the schema calls it required and the model that omits it would otherwise be told the
        // solution directory does not exist — a sentence about a path it never asked for.
        if (path.Length == 0) return Fail("read_file needs a path. Call it again with {\"path\": \"src/Foo.cs\"}.", "read · no path");
        var full = Resolve(root, path);
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
        var relative = Relative(root, full);
        var header = more
            ? relative + " lines " + start + "-" + last + " (more follows; call read_file again with start_line=" + (last + 1) + "):"
            : relative + " lines " + start + "-" + last + " (end of file):";
        return Result(header + "\n" + text, "read " + relative + " · lines " + start + "-" + last + (more ? " of more" : " · end"));
    }

    private static ToolResult ListFiles(JObject args, string? root, string active)
    {
        if (!ToolArguments.TryText(args, "path", out var path, out var error)) return Fail(error, "list · invalid arguments");
        if (!ToolArguments.TryText(args, "pattern", out var pattern, out error)) return Fail(error, "list · invalid arguments");
        var directory = SearchDirectory(root, active, path);
        if (!Directory.Exists(directory)) return Fail("No such directory: " + directory, "list · not found");
        if (pattern.Length == 0) pattern = "*";
        var files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(p => !IsNoise(p))
            .Take(MaxEntries + 1).ToList();
        if (files.Count == 0) return Fail("No files matched " + pattern + " under " + directory, "list · 0 files");
        var more = files.Count > MaxEntries;
        if (more) files.RemoveAt(files.Count - 1);
        var relative = Relative(root, directory);
        var listing = string.Join("\n", files.Select(f => Relative(root, f)));
        return Result((more ? "First " + MaxEntries + " of more than " + MaxEntries : files.Count.ToString()) + " files under " + relative + ":\n" + listing,
            "list " + relative + " · " + files.Count + (more ? "+ files" : " files"));
    }

    private static ToolResult SearchFiles(JObject args, string? root, string active)
    {
        if (!ToolArguments.TryText(args, "query", out var query, out var error)) return Fail(error, "search · invalid arguments");
        if (query.Length == 0) return Fail("search_files needs a query.", "search · no query");
        if (!ToolArguments.TryText(args, "path", out var path, out error)) return Fail(error, "search · invalid arguments");
        var directory = SearchDirectory(root, active, path);
        if (!Directory.Exists(directory)) return Fail("No such directory: " + directory, "search · not found");
        var hits = new List<string>();
        var files = 0;
        var stopped = false;
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
                hits.Add(Relative(root, file) + ":" + (i + 1) + ": " + lines[i].Trim().Truncate(200));
                if (hits.Count >= MaxMatches) { stopped = true; break; }
            }
            if (stopped) break;
        }
        if (hits.Count == 0) return Fail("No matches for \"" + query + "\".", "search · 0 matches");
        return Result((stopped ? "First " + MaxMatches + " matches" : hits.Count + " matches") + " for \"" + query + "\" in " + files + " files:\n" + string.Join("\n", hits),
            "search \"" + query.Truncate(40) + "\" · " + hits.Count + " matches in " + files + " files");
    }

    private static ToolResult Open(DTE? dte, string? root, string active, JObject args)
    {
        try
        {
            if (!ToolArguments.TryText(args, "path", out var requested, out var error)) return Fail(error, "open · invalid arguments");
            var full = requested.Length == 0 ? active : Resolve(root, requested);
            if (full.Length == 0) return Fail("No file to open.", "open · no path");
            if (!File.Exists(full)) return Fail("No such file: " + full, "open · not found");
            if (!ToolArguments.TryNumber(args, "line", 0, out var line, out error)) return Fail(error, "open · invalid arguments");
            line = Math.Max(0, line);
            var relative = Relative(root, full);
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

    /// <summary>Where a listing or a search looks when the call named no directory: the solution, then the open
    /// document's folder, then the process's own. Any path the model did send has already been read, so that a
    /// wrong-typed one is reported rather than treated as an absent one.</summary>
    private static string SearchDirectory(string? root, string active, string path)
    {
        if (path.Length > 0) return Resolve(root, path);
        if (root != null) return root;
        return active.Length > 0 ? Path.GetDirectoryName(active)! : Environment.CurrentDirectory;
    }

    /// <summary>Resolves a model-supplied path against the solution and refuses anything outside it — the model must not be able to read the user's whole disk. The rule itself lives in <see cref="PathGuard"/> so that the edit tools cannot enforce a different one.</summary>
    private static string Resolve(string? root, string path) => PathGuard.Resolve(root, path);

    internal static string? SolutionRoot(DTE? dte)
    {
        var solution = Try(() => dte?.Solution?.FullName);
        if (!string.IsNullOrEmpty(solution)) { var directory = Path.GetDirectoryName(solution); if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory; }
        var active = Try(() => dte?.ActiveDocument?.FullName);
        if (!string.IsNullOrEmpty(active)) { var directory = Path.GetDirectoryName(active); if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory; }
        return null;
    }

    /// <summary>Drops the solution prefix so the model reads "src/Foo.cs" rather than the whole absolute path.</summary>
    internal static string Relative(string? root, string path) => PathGuard.Relative(root, path);

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
