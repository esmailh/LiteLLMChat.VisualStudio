using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteLLM.Core;

public enum SkillCommandKind
{
    /// <summary>The message is ordinary text; send it exactly as typed.</summary>
    None,
    /// <summary>The message starts with <c>/skill</c> but names nothing.</summary>
    MissingName,
    /// <summary>The message named a skill; <see cref="SkillCommand.Remaining"/> is what the user said around it.</summary>
    Invoked
}

public sealed class SkillCommand
{
    public SkillCommandKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The text after the name, trimmed. It becomes the user message when the command is handled.</summary>
    public string Remaining { get; set; } = "";
}

/// <summary>The chat's <c>/skill</c> grammar. It is parsed locally, before a message is sent, so a command that
/// the extension can act on never reaches the model as ordinary text. Anything that does not begin with exactly
/// <c>/skill</c> is left alone, which is what keeps MCP prompts and Claude's own commands working.</summary>
public static class SkillSlashCommand
{
    public const string Command = "/skill";

    public static SkillCommand Parse(string? input)
    {
        var text = input ?? "";
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length) return new SkillCommand { Kind = SkillCommandKind.None };

        var tokenEnd = index;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;
        var token = text.Substring(index, tokenEnd - index);
        if (!string.Equals(token, Command, StringComparison.OrdinalIgnoreCase))
            return new SkillCommand { Kind = SkillCommandKind.None };

        var nameStart = tokenEnd;
        while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart])) nameStart++;
        if (nameStart >= text.Length) return new SkillCommand { Kind = SkillCommandKind.MissingName };

        var nameEnd = nameStart;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd])) nameEnd++;
        var name = text.Substring(nameStart, nameEnd - nameStart);
        var remaining = text.Substring(nameEnd).Trim();
        return new SkillCommand { Kind = SkillCommandKind.Invoked, Name = name, Remaining = remaining };
    }

    /// <summary>Exact name first. Otherwise a prefix is accepted only when it names exactly one skill; two or more
    /// is reported as ambiguous rather than choosing one the user did not mean.</summary>
    public static IReadOnlyList<SkillInfo> Resolve(IReadOnlyList<SkillInfo> effective, string? token, out bool ambiguous)
    {
        ambiguous = false;
        var wanted = (token ?? "").Trim();
        if (wanted.Length == 0) return Array.Empty<SkillInfo>();
        var exact = effective
            .Where(skill => string.Equals(skill.Name, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0) return exact;

        var prefix = effective
            .Where(skill => skill.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ambiguous = prefix.Count > 1;
        return prefix;
    }

    /// <summary>The composer's <c>/skill</c> completion. True only while the message's first token is exactly
    /// <c>/skill</c> and the caret is still in the skill-name word — once the user has moved past it they are
    /// writing the instruction, and a name list would cover what they are writing. The span covers the whole
    /// word so choosing a row replaces it rather than appending a second name.</summary>
    public static bool TryGetNameFilter(string? text, int caret, out int start, out int length, out string filter)
    {
        start = 0; length = 0; filter = "";
        var value = text ?? "";
        if (caret < 0) caret = 0;
        if (caret > value.Length) caret = value.Length;

        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
        if (index >= value.Length) return false;
        var commandEnd = index;
        while (commandEnd < value.Length && !char.IsWhiteSpace(value[commandEnd])) commandEnd++;
        if (!string.Equals(value.Substring(index, commandEnd - index), Command, StringComparison.OrdinalIgnoreCase))
            return false;
        if (caret < commandEnd) return false;

        var nameStart = commandEnd;
        while (nameStart < value.Length && char.IsWhiteSpace(value[nameStart])) nameStart++;
        if (caret < nameStart) return false;

        var nameEnd = nameStart;
        while (nameEnd < value.Length && !char.IsWhiteSpace(value[nameEnd])) nameEnd++;
        if (caret > nameEnd) return false;

        // A line break ends the command. The popup follows the first line or it would follow the user into the
        // body of the message.
        for (var position = commandEnd; position < nameEnd; position++)
            if (value[position] == '\n' || value[position] == '\r') return false;

        start = nameStart;
        length = nameEnd - nameStart;
        filter = value.Substring(nameStart, Math.Max(0, caret - nameStart));
        return true;
    }

    public static IReadOnlyList<string> Suggest(IReadOnlyList<SkillInfo> effective, string? token, int max = 5)
    {
        var wanted = (token ?? "").Trim();
        var names = effective
            .Where(skill => wanted.Length == 0 ||
                            skill.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
                            skill.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(skill => skill.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, max))
            .ToList();
        return names;
    }
}
