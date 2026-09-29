using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

/// <summary>An edit that was applied, kept so the chat can show what changed and put it back. Deliberately holds no
/// reference to the model's conversation: it is built from the file, shown on demand, and never rendered into the
/// transcript.</summary>
internal sealed class AppliedEdit
{
    internal string Path = "";
    /// <summary>Solution-relative, which is the only form the chat shows.</summary>
    internal string Label = "";
    /// <summary>What happened, in a few words: "edited", "created", "removed".</summary>
    internal string Note = "";
    internal string Diff = "";
    internal string Summary = "";
    /// <summary>The file's text before the edit, kept for undo.</summary>
    internal string Before = "";
    internal bool Existed;
    internal bool Removed;
    /// <summary>How the file was encoded and ended its lines, so undo writes it back the same way.</summary>
    internal TextFile.Content Content = null!;
}

/// <summary>An edit waiting on the user's answer, shown when "Confirm before each edit" is on. Everything a file
/// touched by one tool call produced is folded into a single question rather than one per file.</summary>
internal sealed class EditProposal
{
    internal string Label = "";
    internal string Summary = "";
    internal string Diff = "";
}

/// <summary>The writing half of the tool set: Copilot's <c>apply_patch</c>, <c>create_file</c>,
/// <c>replace_string_in_file</c>, <c>multi_replace_string_in_file</c> and <c>create_directory</c>, with the same names
/// and parameters, applied to the open solution.
///
/// Two rules shape everything here. A failed edit must fail loudly and name what to look at, because the alternative —
/// a near-match applied somewhere the model did not look — is discovered when the code stops compiling. And an edit
/// must be visible and reversible: an open document is changed through its own buffer so the change joins Visual
/// Studio's undo stack and Ctrl+Z takes it back, and a file that was closed is written and then opened.
///
/// What the model gets back is a couple of lines describing the change, never the file.</summary>
internal static class EditTools
{
    internal static readonly IReadOnlyList<ToolDefinition> Definitions = new[]
    {
        Define("apply_patch",
            "Edit one or more text files with a patch. The patch starts with \"*** Begin Patch\" and ends with \"*** End Patch\". "
            + "Sections are \"*** Add File: <path>\" followed by lines prefixed \"+\", \"*** Delete File: <path>\", and "
            + "\"*** Update File: <path>\" followed by one or more hunks and optionally \"*** Move to: <path>\". Each hunk starts "
            + "with a line beginning \"@@\", and its lines are prefixed \" \" to keep, \"-\" to remove, or \"+\" to add. "
            + "\"*** End of File\" anchors a hunk to the end of the file. Every context line must match the file exactly.",
            new JObject { ["input"] = Str("The complete patch"), ["explanation"] = Str("One short sentence on what the patch does, shown to the user") }, "input"),
        Define("create_file",
            "Create a new file in the solution and write its content. Use it for a file that does not exist yet; "
            + "it fails if the file is already there, and it creates any missing parent folders.",
            new JObject { ["filePath"] = Str("Solution-relative or absolute path"), ["content"] = Str("The complete contents of the new file") }, "filePath", "content"),
        Define("replace_string_in_file",
            "Replace one string in an existing file. oldString must occur exactly once in the file, so include enough "
            + "surrounding lines to identify the one place you mean. To make several changes at once, use "
            + "multi_replace_string_in_file or apply_patch.",
            new JObject
            {
                ["explanation"] = Str("One short sentence on what the change does, shown to the user"),
                ["filePath"] = Str("Path of the file to change"),
                ["oldString"] = Str("The exact text to replace, including its indentation"),
                ["newString"] = Str("The text to put in its place")
            }, "filePath", "oldString", "newString"),
        Define("multi_replace_string_in_file",
            "Apply several string replacements across one or more files in a single call. Each replacement must match "
            + "exactly once, and replacements in the same file are applied in the order given, so a later one sees the "
            + "result of an earlier one. Prefer this over calling replace_string_in_file repeatedly.",
            new JObject
            {
                ["explanation"] = Str("One short sentence on what the changes do, shown to the user"),
                ["replacements"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "The replacements to apply",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["filePath"] = Str("Path of the file to change"),
                            ["oldString"] = Str("The exact text to replace, including its indentation"),
                            ["newString"] = Str("The text to put in its place")
                        },
                        ["required"] = new JArray("filePath", "oldString", "newString")
                    }
                }
            }, "replacements"),
        Define("create_directory",
            "Create a directory in the solution, including any missing parent directories.",
            new JObject { ["dirPath"] = Str("Solution-relative or absolute path of the directory") }, "dirPath")
    };

    private static readonly string[] Names = { "apply_patch", "create_file", "replace_string_in_file", "multi_replace_string_in_file", "create_directory" };

    internal static bool Handles(string name) => Array.IndexOf(Names, name) >= 0;

    /// <summary>Set by the chat window when "Confirm before each edit" is on. Null means apply straight away, which is
    /// what Visual Studio Code does for an ordinary file in the workspace.</summary>
    internal static Func<EditProposal, Task<bool>>? Confirm;

    private static readonly List<AppliedEdit> History = new List<AppliedEdit>();
    private const int MaxHistory = 20;

    /// <summary>The most recent edit, which is the only one the chat may offer to undo.</summary>
    internal static AppliedEdit? Latest => History.Count > 0 ? History[History.Count - 1] : null;

    /// <summary>How many edits have been applied. A caller that reads this before a tool call can tell whether the
    /// call changed anything, and so whether the row it is about to write deserves a diff and an undo.</summary>
    internal static int Revisions => History.Count;

    internal static async Task<ToolResult> ExecuteAsync(DTE? dte, string? root, string name, JObject args)
    {
        try
        {
            switch (name)
            {
                case "apply_patch": return await PatchAsync(dte, root, args).ConfigureAwait(true);
                case "create_file": return await CreateAsync(dte, root, args).ConfigureAwait(true);
                case "create_directory": return CreateDirectory(root, args);
                case "replace_string_in_file": return await ReplaceAsync(dte, root, args).ConfigureAwait(true);
                case "multi_replace_string_in_file": return await MultiReplaceAsync(dte, root, args).ConfigureAwait(true);
                default: return Fail("Unknown tool: " + name, "unknown tool");
            }
        }
        catch (EditFailure failure)
        {
            return Fail(failure.Message, "edit refused · " + failure.Message.Truncate(90));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                   || ex is NotSupportedException || ex is COMException || ex is InvalidOperationException)
        {
            return Fail(ex.Message, "edit failed · " + ex.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- the tools

    private static async Task<ToolResult> PatchAsync(DTE? dte, string? root, JObject args)
    {
        var patch = Text(args, "input");
        if (patch.Trim().Length == 0) return Fail("apply_patch was called without a patch in `input`.", "patch · no input");

        var plans = new List<Plan>();
        foreach (var file in PatchApply.Parse(patch))
        {
            var plan = Prepare(dte, root, file.Path);
            if (file.IsAdd)
            {
                if (plan.Existed) throw new EditFailure("Cannot add " + plan.Label + ": the file already exists. Use an update hunk to change it.");
                plan.Note = "created";
                plan.After = file.NewContent ?? "";
            }
            else if (file.Delete)
            {
                if (!plan.Existed) throw new EditFailure("Cannot delete " + plan.Label + ": there is no such file.");
                plan.Note = "removed";
                plan.Removed = true;
                plan.After = "";
            }
            else
            {
                if (!plan.Existed) throw new EditFailure("Cannot update " + plan.Label + ": there is no such file. Use \"*** Add File\" or create_file for a new one.");
                plan.Note = "edited";
                plan.After = PatchApply.Apply(file, plan.Before);
            }

            if (file.MoveTo is { Length: > 0 } target && !file.Delete)
            {
                var moved = Prepare(dte, root, target);
                if (moved.Existed) throw new EditFailure("Cannot move " + plan.Label + " to " + moved.Label + ": the destination already exists.");
                plan.Note = "moved to " + moved.Label;
                moved.Note = "created";
                moved.After = plan.After;
                // The original is emptied by the move, so the move is a removal of one file and a creation of another.
                plan.Removed = true;
                plan.After = "";
                plans.Add(plan);
                plans.Add(moved);
                continue;
            }
            plans.Add(plan);
        }

        if (!await ApproveAsync(plans).ConfigureAwait(true)) return Declined();
        return Commit(dte, plans);
    }

    private static async Task<ToolResult> CreateAsync(DTE? dte, string? root, JObject args)
    {
        var path = Text(args, "filePath");
        if (path.Length == 0) path = Text(args, "path");
        var plan = Prepare(dte, root, path);
        if (plan.Existed) return Fail("Cannot create " + plan.Label + ": the file already exists. Edit it with apply_patch or replace_string_in_file instead.", "create refused · exists");
        plan.Note = "created";
        plan.After = Text(args, "content");

        if (!await ApproveAsync(new List<Plan> { plan }).ConfigureAwait(true)) return Declined();
        return Commit(dte, new List<Plan> { plan });
    }

    private static ToolResult CreateDirectory(string? root, JObject args)
    {
        var path = Text(args, "dirPath");
        if (path.Length == 0) path = Text(args, "path");
        if (path.Length == 0) return Fail("create_directory needs a dirPath.", "directory · no path");
        var full = PathGuard.Resolve(root, path);
        var label = PathGuard.Relative(root, full);
        if (Directory.Exists(full)) return Result("The directory " + label + " already exists.", "directory " + label + " · already there");
        Directory.CreateDirectory(full);
        return Result("Created the directory " + label + ".", "created directory " + label);
    }

    private static async Task<ToolResult> ReplaceAsync(DTE? dte, string? root, JObject args)
    {
        var path = Text(args, "filePath");
        if (path.Length == 0) path = Text(args, "path");
        var plan = Prepare(dte, root, path);
        if (!plan.Existed) return Fail("No such file: " + plan.Label + ". Use create_file for a new one.", "edit refused · not found");

        plan.Note = "edited";
        plan.After = StringReplacer.ReplaceOnce(plan.Before, Text(args, "oldString"), Text(args, "newString"), plan.Label);

        if (!await ApproveAsync(new List<Plan> { plan }).ConfigureAwait(true)) return Declined();
        return Commit(dte, new List<Plan> { plan });
    }

    private static async Task<ToolResult> MultiReplaceAsync(DTE? dte, string? root, JObject args)
    {
        var replacements = args["replacements"] as JArray;
        if (replacements == null || replacements.Count == 0) return Fail("multi_replace_string_in_file needs at least one entry in `replacements`.", "edit · no replacements");

        // Grouped by file in the order the files are first named, because a later entry against the same file must see
        // the earlier one already applied. An entry that fails is reported and skipped rather than aborting the batch:
        // one stale oldString in five should not cost the model the other four edits, which it would then have to
        // discover by reading the file back.
        var order = new List<string>();
        var files = new Dictionary<string, Plan>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        for (var i = 0; i < replacements.Count; i++)
        {
            var entry = replacements[i] as JObject;
            var label = (string?)entry?["filePath"] ?? "entry " + (i + 1);
            try
            {
                if (entry == null) throw new EditFailure("entry " + (i + 1) + " is missing filePath, oldString or newString");

                var full = PathGuard.Resolve(root, (string?)entry["filePath"] ?? "");
                label = PathGuard.Relative(root, full);
                if (!files.TryGetValue(full, out var plan))
                {
                    plan = Prepare(dte, root, (string?)entry["filePath"] ?? "");
                    if (!plan.Existed) throw new EditFailure("no such file. Use create_file for a new one");
                    plan.Note = "edited";
                    plan.After = plan.Before;   // each entry replaces into the running text, not into raw disk state
                    files[full] = plan;
                    order.Add(full);
                }

                var oldString = (string?)entry["oldString"] ?? "";
                var replacement = StringReplacer.ReplaceOnce(plan.After, oldString, (string?)entry["newString"] ?? "", label);
                if (string.Equals(replacement, plan.After, StringComparison.Ordinal))
                    throw new EditFailure("oldString and newString are identical");
                plan.After = replacement;
            }
            catch (EditFailure failure)
            {
                failures.Add(label + ": " + failure.Message);
            }
        }

        var plans = order.Select(full => files[full]).ToList();
        if (plans.Count == 0 || plans.All(p => string.Equals(p.After, p.Before, StringComparison.Ordinal)))
            return Fail(failures.Count > 0 ? "No replacement could be applied.\n" + string.Join("\n", failures) : "None of the replacements changed anything.", "edit · nothing applied");

        if (!await ApproveAsync(plans).ConfigureAwait(true)) return Declined();
        var result = Commit(dte, plans);
        if (failures.Count == 0) return result;

        result.Ok = false;
        result.Content += "\nNot applied:\n" + string.Join("\n", failures);
        return result;
    }

    // ---------------------------------------------------------------- applying

    /// <summary>One file's worth of a pending edit: what it holds now, and what it will hold.</summary>
    private sealed class Plan
    {
        internal string Full = "";
        internal string Label = "";
        internal string Note = "";
        internal bool Existed;
        internal bool Removed;
        internal TextFile.Content Content = null!;
        /// <summary>The open document, when there is one. Held as <see cref="Document"/> rather than
        /// <see cref="TextDocument"/> because Save and Close live on the former and the buffer on the latter.</summary>
        internal Document? Open;
        internal string Before = "";
        internal string After = "";
        internal string Summary = "";
    }

    /// <summary>Reads the file's current text. An open document is the source of truth, not the disk: it may hold
    /// unsaved work, and that work is what the model read and what the edit has to be measured against.</summary>
    private static Plan Prepare(DTE? dte, string? root, string path)
    {
        var full = PathGuard.Resolve(root, path);
        var open = FindOpen(dte, full);
        var view = open == null ? null : View(open);
        var existed = File.Exists(full);
        var content = existed ? TextFile.Read(full) : TextFile.New();
        var before = view != null ? Buffer(view) : content.Text;
        if (view != null && before.Length > 0) content.NewLine = TextFile.DominantNewLine(before);
        return new Plan
        {
            Full = full,
            Label = PathGuard.Relative(root, full),
            Existed = existed,
            Content = content,
            Open = open,
            Before = before
        };
    }

    /// <summary>Folds every file into the one question the user answers, then applies everything. Nothing is written
    /// before the whole tool call has been validated, so a patch that is wrong in its third file does not leave its
    /// first two half-applied.</summary>
    private static async Task<bool> ApproveAsync(List<Plan> plans)
    {
        foreach (var plan in plans)
        {
            var change = TextChange.Between(plan.Before, plan.After);
            plan.Summary = Diff.Summary(change.Removed.Count, change.Added.Count);
        }

        if (Confirm == null) return true;
        var changed = plans.Where(p => !string.Equals(p.Before, p.After, StringComparison.Ordinal)).ToList();
        if (changed.Count == 0) return true;

        var proposal = new EditProposal
        {
            Label = changed.Count == 1 ? changed[0].Label : changed.Count + " files",
            Summary = Diff.Summary(changed.Sum(p => TextChange.Between(p.Before, p.After).Removed.Count),
                                   changed.Sum(p => TextChange.Between(p.Before, p.After).Added.Count)),
            Diff = string.Join("\n", changed.Select(p => Diff.ForRegion(p.Label, TextChange.Between(p.Before, p.After))))
        };
        return await Confirm(proposal).ConfigureAwait(true);
    }

    private static ToolResult Declined() =>
        Fail("The user declined this edit. Do not apply it again unless they ask; say what you would have changed instead.",
            "edit declined by the user");

    private static ToolResult Commit(DTE? dte, List<Plan> plans)
    {
        var changed = new List<Plan>();
        var lines = new List<string>();
        foreach (var plan in plans)
        {
            if (string.Equals(plan.Before, plan.After, StringComparison.Ordinal))
            {
                lines.Add(plan.Label + ": already as you asked, nothing to change.");
                continue;
            }

            // The file may have been typed into while the edit was being prepared. Line positions are no longer the
            // ones the change was computed against, so writing now would corrupt the file rather than edit it.
            var view = plan.Open == null ? null : View(plan.Open);
            if (view != null && !string.Equals(Buffer(view), plan.Before, StringComparison.Ordinal))
                throw new EditFailure(plan.Label + " changed while the edit was being prepared. Read it again and reapply.");

            changed.Add(plan);
        }

        foreach (var plan in changed)
        {
            Apply(dte, plan);
            Remember(new AppliedEdit
            {
                Path = plan.Full,
                Label = plan.Label,
                Note = plan.Note,
                Summary = plan.Summary,
                Diff = Diff.ForRegion(plan.Label, TextChange.Between(plan.Before, plan.After)),
                Before = plan.Before,
                Existed = plan.Existed,
                Removed = plan.Removed,
                Content = plan.Content
            });
            lines.Add(plan.Label + ": " + plan.Note + " (" + plan.Summary + ", now "
                + Count(plan.After) + " lines).");
        }

        if (changed.Count == 0) return Fail(string.Join("\n", lines), "edit · nothing changed");

        // One file gets its own row in the chat; a patch gets one row for the whole patch, because six rows saying
        // "edited" and nothing else is noise rather than a report.
        if (changed.Count == 1) return Result(string.Join("\n", lines), changed[0].Note + " " + changed[0].Label + " · " + changed[0].Summary);

        var added = 0;
        var removed = 0;
        foreach (var plan in changed)
        {
            var change = TextChange.Between(plan.Before, plan.After);
            added += change.Added.Count;
            removed += change.Removed.Count;
        }
        return Result(string.Join("\n", lines), changed.Count + " files changed · " + Diff.Summary(removed, added));
    }

    /// <summary>Writes one plan. An open document is changed through its own buffer, so the change joins Visual
    /// Studio's undo stack and Ctrl+Z takes it back; anything else is written to disk and opened so the user sees it.</summary>
    private static void Apply(DTE? dte, Plan plan)
    {
        if (plan.Removed)
        {
            if (plan.Open != null) Try(() => plan.Open.Close(vsSaveChanges.vsSaveChangesNo));
            if (File.Exists(plan.Full)) File.Delete(plan.Full);
            return;
        }

        var text = plan.Content.Conform(plan.After);
        var view = plan.Open == null ? null : View(plan.Open);
        if (view != null)
        {
            try
            {
                // Only the lines that differ are replaced, so the editor marks the change rather than the whole file.
                var change = TextChange.Between(plan.Before, plan.After);
                Substitute(view, change, plan.Content.NewLine);
                // The reduced edit is an optimisation, not a guarantee: a file with no trailing newline, or one whose
                // last line is being appended to, has a line model the arithmetic above can miss. Checking the result
                // costs a string compare and means a wrong guess degrades to a whole-buffer write rather than to a
                // silently mangled file.
                if (!string.Equals(LineText.Normalize(Buffer(view)), LineText.Normalize(plan.After), StringComparison.Ordinal))
                    Substitute(view, new TextChange { StartLine = 1, Removed = LineText.Split(LineText.Normalize(plan.Before)), Added = LineText.Split(LineText.Normalize(plan.After)) }, plan.Content.NewLine);
                plan.Open!.Save();
                return;
            }
            catch (COMException) { }   // the document was closed between planning and writing
        }

        TextFile.Write(plan.Full, plan.Content, text);
        Open(dte, plan.Full);
    }

    /// <summary>Replaces just the changed region of the buffer, leaving the caret, the scroll position and the
    /// editor's change tracking alone.</summary>
    private static void Substitute(TextDocument document, TextChange change, string newLine)
    {
        var start = document.StartPoint.CreateEditPoint();
        var afterRemoved = change.StartLine + change.Removed.Count;
        if (change.StartLine > document.EndPoint.Line)
        {
            start.EndOfDocument();   // the change begins past the last line, which is where an append belongs
        }
        else
        {
            if (change.StartLine > 1) start.LineDown(change.StartLine - 1);
            start.StartOfLine();
        }

        EditPoint end;
        if (change.Removed.Count == 0)
        {
            end = start.CreateEditPoint();
        }
        else if (afterRemoved - 1 < document.EndPoint.Line)
        {
            end = document.StartPoint.CreateEditPoint();
            end.LineDown(afterRemoved - 1);
            end.StartOfLine();
        }
        else
        {
            end = document.StartPoint.CreateEditPoint();
            end.EndOfDocument();
        }

        var text = change.Added.Count == 0 ? "" : string.Join(newLine, change.Added) + newLine;
        start.ReplaceText(end, text, 0);
    }

    // ---------------------------------------------------------------- undo

    /// <summary>Puts a file back the way it was. Only the most recent edit to a file can be undone, because undoing an
    /// older one would write back text that a later edit has since changed — the message says so rather than silently
    /// discarding the newer work.</summary>
    internal static string Undo(DTE? dte, AppliedEdit edit)
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(History[i], edit)) break;
            if (string.Equals(History[i].Path, edit.Path, StringComparison.OrdinalIgnoreCase))
                return "A later edit changed " + edit.Label + ". Undo that one first.";
        }

        var document = FindOpen(dte, edit.Path);
        if (edit.Removed)
        {
            TextFile.Write(edit.Path, edit.Content, edit.Content.Conform(edit.Before));
            Open(dte, edit.Path);
            return "Restored " + edit.Label + ".";
        }

        if (!edit.Existed)
        {
            if (document != null) Try(() => document.Close(vsSaveChanges.vsSaveChangesNo));
            if (File.Exists(edit.Path)) File.Delete(edit.Path);
            return "Removed " + edit.Label + ", which this edit had created.";
        }

        var text = edit.Content.Conform(edit.Before);
        var view = document == null ? null : View(document);
        if (view != null)
        {
            try
            {
                var whole = view.StartPoint.CreateEditPoint();
                whole.ReplaceText(view.EndPoint, text, 0);
                document!.Save();
                return "Restored " + edit.Label + ".";
            }
            catch (COMException) { }
        }
        TextFile.Write(edit.Path, edit.Content, text);
        Open(dte, edit.Path);
        return "Restored " + edit.Label + ".";
    }

    // ---------------------------------------------------------------- editor plumbing

    private static string Buffer(TextDocument view)
    {
        var start = view.StartPoint.CreateEditPoint();
        return start.GetText(view.EndPoint);
    }

    /// <summary>The text buffer behind an open document. A document that is not text — the designer, a diff view — has
    /// none, and is treated as not open rather than as an error.</summary>
    private static TextDocument? View(Document document)
    {
        try { return document.Object("TextDocument") as TextDocument; }
        catch (Exception) { return null; }
    }

    private static Document? FindOpen(DTE? dte, string full)
    {
        if (dte == null) return null;
        try
        {
            foreach (Document document in dte.Documents)
            {
                if (document == null || !string.Equals(document.FullName, full, StringComparison.OrdinalIgnoreCase)) continue;
                return document;
            }
        }
        catch (Exception) { }   // no solution, or the document list is not ready yet
        return null;
    }

    private static void Open(DTE? dte, string full)
    {
        if (dte == null) return;
        Try(() => dte.ItemOperations.OpenFile(full));
    }

    private static int Count(string text) => LineText.Split(LineText.Normalize(text)).Count;
    private static void Remember(AppliedEdit edit) { History.Add(edit); while (History.Count > MaxHistory) History.RemoveAt(0); }

    // ---------------------------------------------------------------- small shared bits

    private static JObject Parse(string arguments) { try { return string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments); } catch (Exception) { return new JObject(); } }
    private static string Text(JObject args, string name) => (string?)args[name] ?? "";
    private static ToolResult Result(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = true };
    private static ToolResult Fail(string content, string summary) => new ToolResult { Content = content, Summary = summary, Ok = false };
    private static void Try(Action action) { try { action(); } catch (Exception) { } }

    private static ToolDefinition Define(string name, string description, JObject properties, params string[] required)
    {
        var schema = new JObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JArray(required.Cast<object>().ToArray());
        return new ToolDefinition { Function = new JObject { ["name"] = name, ["description"] = description, ["parameters"] = schema } };
    }

    private static JObject Str(string description) => new JObject { ["type"] = "string", ["description"] = description };
}
