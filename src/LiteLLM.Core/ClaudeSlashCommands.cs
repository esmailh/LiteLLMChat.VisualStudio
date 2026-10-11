using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteLLM.Core;

public sealed class ClaudeSlashCommand
{
    public ClaudeSlashCommand(string name, string description)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public string Name { get; }
    public string Description { get; }
}

/// <summary>Commands that Claude Code accepts as prompt-invoked bundled skills in print mode.
/// Terminal-only commands such as /help, /clear, /config, and /resume are deliberately omitted.</summary>
public static class ClaudeSlashCommands
{
    private static readonly IReadOnlyList<ClaudeSlashCommand> Commands = new[]
    {
        new ClaudeSlashCommand("/init", "Create or improve the project's CLAUDE.md guidance"),
        new ClaudeSlashCommand("/review", "Review the current code changes"),
        new ClaudeSlashCommand("/security-review", "Review the current changes for security issues")
    };

    public static IReadOnlyList<ClaudeSlashCommand> All => Commands;

    public static bool TryGetFilter(string text, int caretIndex, out int tokenStart, out int tokenLength, out string filter)
    {
        tokenStart = tokenLength = 0;
        filter = "";
        if (string.IsNullOrEmpty(text) || caretIndex < 1 || caretIndex > text.Length) return false;

        var lineStart = text.LastIndexOf('\n', caretIndex - 1) + 1;
        var i = lineStart;
        while (i < caretIndex && char.IsWhiteSpace(text[i]) && text[i] != '\n') i++;
        if (i >= caretIndex || text[i] != '/') return false;

        for (var j = i + 1; j < caretIndex; j++)
            if (char.IsWhiteSpace(text[j])) return false;

        tokenStart = i;
        tokenLength = caretIndex - i;
        filter = text.Substring(i + 1, caretIndex - i - 1);
        return true;
    }

    public static IReadOnlyList<ClaudeSlashCommand> Filter(string filter)
    {
        filter = (filter ?? "").TrimStart('/');
        return Commands.Where(c => c.Name.Substring(1).StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static string ReplaceToken(string text, int tokenStart, int tokenLength, string command)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (tokenStart < 0 || tokenLength < 0 || tokenStart + tokenLength > text.Length) throw new ArgumentOutOfRangeException();
        var suffix = text.Substring(tokenStart + tokenLength);
        var separator = suffix.Length == 0 || !char.IsWhiteSpace(suffix[0]) ? " " : "";
        return text.Substring(0, tokenStart) + command + separator + suffix;
    }
}
