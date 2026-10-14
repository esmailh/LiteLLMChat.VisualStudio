using System;
using System.IO;
using System.Text;

namespace LiteLLM.Core;

/// <summary>The project's own instructions, read from the files agents are expected to leave them in.
///
/// A model that has not been told how a project is built reaches for the obvious command and is wrong about it, and
/// the round it spends finding that out is a round the user paid for. Each of these files is somewhere the project
/// has already written down what it expects, and the same files are read by Claude Code and by Copilot, so one block
/// here serves all three.
///
/// Read once per turn and never stored: like the instructions and the attachments, this belongs to the request
/// rather than to the transcript.</summary>
public static class RepoInstructions
{
    /// <summary>Where a project's instructions are looked for, most authoritative first. The order is also the
    /// answer when two of them disagree, since the first is the one the model reads first.</summary>
    public static readonly string[] FileNames = { "AGENTS.md", "CLAUDE.md", ".github/copilot-instructions.md" };

    /// <summary>Ceiling on the whole block. A project's conventions are worth a page or two of the request; they do
    /// not get to crowd out the conversation they are there to serve.</summary>
    public const int MaxChars = 8000;

    /// <summary>The block for the system prompt, or "" for a project that says nothing about itself.</summary>
    public static string Load(string? root) => Load(root, Read);

    /// <summary>As above, with the reader supplied so the caller — or a test — decides what a file is.</summary>
    public static string Load(string? root, Func<string, string?> read)
    {
        if (root == null || root.Length == 0 || read == null) return "";
        var text = new StringBuilder();
        foreach (var name in FileNames)
        {
            var body = read(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (body == null || body.Trim().Length == 0) continue;
            // Named, so the model can tell a house rule from something the user typed, and can ask for the file
            // itself when the block has been cut short.
            if (text.Length > 0) text.Append('\n');
            text.Append("From ").Append(name).Append(":\n").Append(body.Trim()).Append('\n');
        }
        return text.Length == 0 ? "" : ChatStateLimits.Clip(text.ToString().TrimEnd(), MaxChars);
    }

    /// <summary>A file's text, or null for anything that cannot be read. An instructions file that is missing or
    /// locked must cost the request nothing; it is not worth failing a turn over.</summary>
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException) { return null; }
    }
}
