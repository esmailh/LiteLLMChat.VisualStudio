using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>Builds the message list that is actually sent.
///
/// Two things are added here rather than stored in the transcript: the agent instructions, and the block naming what
/// the user attached to the message. Keeping both out of the stored history is what stops them reappearing in the chat
/// when a session is reloaded, so what the user sees stays exactly what they typed.
///
/// The instructions matter as much as the tools do. A model that has an edit tool but has not been told to use it will
/// still print the corrected file into the chat and leave the file on disk untouched — which is the report this was
/// written to answer.</summary>
public static class AgentPrompt
{
    public static List<ChatMessage> Build(IEnumerable<ChatMessage> history, IReadOnlyList<ChatContextItem>? attachments, bool toolCalling, bool fileEditing) =>
        Build(history, attachments, "agent", toolCalling, fileEditing, false);

    /// <summary>Ceiling on everything the instruction block plus the attachments may add to one request. The
    /// attachments are the user's own doing, so they are allowed to be large; they are not allowed to be unbounded.</summary>
    private const int MaxAttachmentChars = 24000;

    /// <summary>Ceiling on the text all the attached files together may contribute when the conversation has no tool
    /// that can read them. Shared across the attachments of one message, so ten files cannot add up to a novel.</summary>
    private const int MaxInlineFileChars = 8000;

    public static List<ChatMessage> Build(IEnumerable<ChatMessage> history, IReadOnlyList<ChatContextItem>? attachments, string mode, bool toolCalling, bool fileEditing, bool terminalTools = false)
    {
        var messages = new List<ChatMessage> { new ChatMessage { Role = "system", Content = Instructions(mode, toolCalling, fileEditing, terminalTools) } };
        messages.AddRange(history.Where(m => m != null).Select(ChatStateLimits.CopyMessage));

        var attached = (attachments ?? new List<ChatContextItem>()).Where(a => a != null && a.Label.Length > 0).ToList();
        if (attached.Count == 0) return messages;

        var block = AttachmentBlock(attached, toolCalling);
        if (block.Length == 0) return messages;

        // Attached to the last user message, which is the one this request is about. Later in the list would put the
        // context after the question it belongs to; earlier would attach it to a turn already answered.
        var last = messages.FindLast(m => m.Role == "user");
        if (last == null) return messages;
        last.Content = last.Content.Length > 0 ? last.Content + "\n\n" + block : block;
        return messages;
    }

    /// <summary>Turns attached files into text, for a conversation whose tools cannot read them. The path is no use to
    /// a model with no way to open it, so a bounded excerpt travels with the request instead.
    ///
    /// The reader is injected to keep this testable, and it returns null for anything it cannot read — a file that has
    /// been deleted, or one the tools would refuse. That entry then keeps its path, which is at least something the
    /// model can ask about. Nothing here touches the stored transcript: the turn keeps the path it was sent with, and
    /// the text is added at request time the same way the instructions are.</summary>
    public static List<ChatContextItem> InlineFiles(IReadOnlyList<ChatContextItem>? attachments, Func<string, string?> read)
    {
        var result = new List<ChatContextItem>();
        if (attachments == null) return result;

        var budget = MaxInlineFileChars;
        foreach (var item in attachments)
        {
            if (item == null) continue;
            if (item.Kind != "file" || budget <= 0) { result.Add(item); continue; }

            var text = read(item.Preview);
            if (string.IsNullOrEmpty(text)) { result.Add(item); continue; }

            var excerpt = text!.Length <= budget
                ? text!
                : text!.Substring(0, budget) + "\n… [truncated at " + budget + " characters]";
            budget -= excerpt.Length;
            result.Add(new ChatContextItem { Label = item.Label, Kind = "file-text", Preview = excerpt });
        }
        return result;
    }

    public static string AttachmentBlock(IReadOnlyList<ChatContextItem> attached, bool toolCalling)
    {
        var text = new StringBuilder();
        var budget = MaxAttachmentChars;
        var files = attached.Where(a => a.Kind == "file").ToList();
        var inline = attached.Where(a => a.Kind != "file").ToList();

        if (files.Count > 0)
        {
            text.Append(toolCalling
                ? "<attached_files>\nThe user attached these files to this message. Read each one with read_file before you answer, and do not ask the user to paste them:\n"
                : "<attached_files>\nThe user attached these files to this message. You have no tool that can read them, so ask the user to paste what you need:\n");
            foreach (var file in files) text.Append("- ").Append(file.Label).Append("  (").Append(file.Preview).Append(")\n");
            text.Append("</attached_files>");
        }

        // A selection or an error list has no path to read from, so its text travels with the request.
        foreach (var item in inline)
        {
            var header = "<context kind=\"" + Escape(item.Kind) + "\" label=\"" + Escape(item.Label) + "\">";
            if (header.Length + item.Preview.Length + 14 > budget) break;
            budget -= header.Length + item.Preview.Length + 14;
            if (text.Length > 0) text.Append('\n');
            text.Append(header).Append('\n').Append(item.Preview).Append("\n</context>");
        }
        return text.ToString();
    }

    public static string Instructions(bool toolCalling, bool fileEditing) => Instructions("agent", toolCalling, fileEditing, false);

    public static string Instructions(string mode, bool toolCalling, bool fileEditing, bool terminalTools = false)
    {
        var text = new StringBuilder();
        text.Append(@"You are an AI assistant working inside Visual Studio at the user's side, in the solution they have open.
");
        // Promising a tool that was never offered is worse than promising nothing: the model asks for read_file, no
        // such tool exists, and the turn is wasted. Which paragraph goes in has to follow the tool list actually sent.
        text.Append(toolCalling
            ? @"
Work from the workspace instead of from assumptions. Use list_files, search_files and read_file to find and read what
you need. Never ask the user to paste a file you can read yourself with a tool, and never claim to have read
something you have not opened."
            : @"
You have no tools in this conversation. Work from what is in the thread and from what the user pastes; when you need a
file you cannot see, ask for it by name rather than guessing at what is in it.");
        text.Append(@"

Reply in the same language the user writes in. Keep the reply short and concrete: say what you did and what you found,
not what you are about to do.");
        if (mode == "ask") text.Append("\n\nYou are in Ask mode. Answer directly; do not inspect or modify workspace files.");
        else if (mode == "plan") text.Append("\n\nYou are in Plan mode. Investigate as needed, then provide a numbered plan. Do not modify files or claim changes were made; wait for confirmation.");
        else text.Append("\n\nYou are in Agent mode. Work autonomously using available tools, implement the request, verify it, and report briefly.");
        if (!toolCalling) return text.ToString();

        if (terminalTools) text.Append(@"

    Command execution
    - Use run_git for every Git operation, including commit, merge, pull, and push. It supports all Git arguments and can open GitHub/GitLab browser login when authentication is required, then retry once.
    - Use run_powershell or run_cmd when another real command is needed to build, test, package, or inspect the workspace.
    - Commands run from the solution directory. Inspect their exit code and output; never claim a command succeeded unless its result says so.
    - Prefer non-interactive, bounded commands. Browser-based Git authentication is the exception; do not start other servers, watchers, shells, or programs that wait for user input.");

        if (!fileEditing)
        {
            text.Append(@"

You have no tool for changing files. If the user asks for an edit, say so plainly and show the smallest snippet that
makes the change, rather than implying the file was updated.");
            return text.ToString();
        }

        text.Append(@"

Editing files
- To edit files in the workspace, use the apply_patch tool.
- If you have issues with it, you should first try to fix your patch and continue using apply_patch.
- Before you edit an existing file, make sure you either already have it in the provided context, or read it with
  read_file, so that you can make proper changes. Don't try to edit a file without reading it first.
- Use create_file for a file that does not exist yet.
- Use replace_string_in_file to replace a string in a file only if you are sure that the string is unique enough to
  not cause any issues. Use multi_replace_string_in_file when you need several edits, and group your changes by file.
- NEVER show the changes to the user, just call the tool, and the edits will be applied and shown to the user.
- NEVER print a codeblock that represents a change to a file. NEVER print out a codeblock with file changes unless
  the user asked for it. Use the appropriate edit tool instead.
- When a tool reports an error, read it, correct what it names, and call the tool again. Do not give up and print the
  code at the user instead.

Verifying the build
- The solution is built automatically after you change a file, and the result arrives in this conversation as a message.
  Read it, fix the cause of anything it reports, and stop; you do not need to build the solution yourself.
- Never claim that a change compiles on your own authority. The build result in the conversation is what says so, and
  when it reports no errors you may say the change compiles.");
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
}
