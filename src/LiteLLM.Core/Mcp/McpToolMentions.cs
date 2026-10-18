using System;
using System.Collections.Generic;

namespace LiteLLM.Core;

/// <summary>How a name written in a message is meant. A <see cref="Selection"/> narrows what the turn may call; a
/// <see cref="Hint"/> only tells the model which tool the prose is about, because a name in backticks is as likely
/// to be a symbol or a quotation as a request.</summary>
public enum McpMentionKind
{
    Selection,
    Hint
}

/// <summary>One tool name found in a user message, with where it sits in the text.</summary>
public sealed class McpToolMention
{
    /// <summary>The name without its leading <c>#</c>. For a bare exposed name this is the whole token.</summary>
    public string Text { get; set; } = "";
    public McpMentionKind Kind { get; set; }
    /// <summary>Offset of the token in the message, including the <c>#</c> when it has one.</summary>
    public int Start { get; set; }
    public int Length { get; set; }

    public override string ToString() => Text;
}

/// <summary>The mention grammar, kept UI-independent so the composer and the agent loop agree on what a token means.
///
/// The rules are deliberately narrow. Only the current user message is read, and inside it only prose: fenced code,
/// inline code, <c>C#</c>, markdown headings and C# preprocessor lines are not selections. A token must still resolve
/// against the connected servers' tools to do anything, so a stray <c>#word</c> can draw a row saying it is
/// unavailable but can never invoke a tool.</summary>
public static class McpToolMentions
{
    /// <summary>Names that are a preprocessor line at the start of a line rather than a tool selection. A tool that
    /// really is called <c>region</c> remains reachable as <c>#Server.region</c>, which is what the picker inserts.</summary>
    private static readonly HashSet<string> Directives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "elif", "endif", "define", "undef", "warning", "error", "line", "region",
        "endregion", "pragma", "nullable", "load", "r", "reference"
    };

    private const int MaxHintLength = 128;

    /// <summary>Every mention in <paramref name="prompt"/>, in the order it appears. Duplicates are returned; the
    /// selector deduplicates by the tool each one resolves to.</summary>
    public static IReadOnlyList<McpToolMention> Parse(string? prompt)
    {
        var mentions = new List<McpToolMention>();
        if (string.IsNullOrEmpty(prompt)) return mentions;

        var lines = prompt!.Split('\n');
        var inFence = false;
        var offset = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var lineStart = offset;
            offset += raw.Length + 1;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            ScanLine(line, lineStart, mentions);
        }
        return mentions;
    }

    /// <summary>The <c>#</c> token the caret sits at the end of, for the inline picker. Mirrors
    /// <see cref="FileMentions.TryGetFilter"/>: the token starts at a word boundary, has no whitespace, and the
    /// caret is inside it. A <c>#</c> followed by a space is a heading or a comment, not a picker.</summary>
    public static bool TryGetFilter(string text, int caret, out int start, out int length, out string filter)
    {
        start = length = 0;
        filter = "";
        if (string.IsNullOrEmpty(text) || caret < 1 || caret > text.Length) return false;

        var at = caret - 1;
        while (at >= 0 && !char.IsWhiteSpace(text[at])) at--;
        at++;
        if (at >= caret || text[at] != '#') return false;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1])) return false;

        filter = text.Substring(at + 1, caret - at - 1);
        if (filter.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
        if (filter.Length > 0 && !IsTokenChar(filter[0])) return false;

        start = at;
        length = caret - at;
        return true;
    }

    /// <summary>What the picker inserts: the server-qualified form, so it survives another server adding a tool with
    /// the same name, and so the token reads as a choice rather than a decoration. Falls back to the exposed name
    /// when the tool's own name could not be written as one token.</summary>
    public static string TokenFor(McpToolInfo tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        var name = tool.Name ?? "";
        if (IsSimpleName(name))
            return "#" + McpToolNaming.Slug(tool.Server?.Name) + "." + name;
        return "#" + (tool.ExposedName ?? "");
    }

    private static void ScanLine(string line, int lineStart, List<McpToolMention> mentions)
    {
        for (var index = 0; index < line.Length; )
        {
            // Inline code is a quotation, not a selection. Its content is still worth a hint when it names one tool.
            if (line[index] == '`')
            {
                var close = line.IndexOf('`', index + 1);
                if (close < 0) { index++; continue; }
                var content = line.Substring(index + 1, close - index - 1).Trim();
                if (content.Length > 0 && content.Length <= MaxHintLength && IsSingleWord(content))
                    mentions.Add(new McpToolMention
                    {
                        Text = content,
                        Kind = McpMentionKind.Hint,
                        Start = lineStart + index + 1,
                        Length = close - index - 1
                    });
                index = close + 1;
                continue;
            }

            if (line[index] == '#')
            {
                var end = index + 1;
                if (end < line.Length && IsTokenChar(line[end]))
                {
                    while (end < line.Length && IsTokenChar(line[end])) end++;
                    var token = line.Substring(index + 1, end - index - 1).TrimEnd('.');
                    var atLineStart = index == FirstNonSpace(line);
                    if (token.Length > 0 && !(atLineStart && Directives.Contains(token)) &&
                        (index == 0 || char.IsWhiteSpace(line[index - 1])))
                    {
                        mentions.Add(new McpToolMention
                        {
                            Text = token,
                            Kind = McpMentionKind.Selection,
                            Start = lineStart + index,
                            Length = token.Length + 1
                        });
                    }
                }
                index = end;
                continue;
            }

            if (string.CompareOrdinal(line, index, "mcp__", 0, 5) == 0 &&
                (index == 0 || !IsTokenChar(line[index - 1])))
            {
                var end = index + 5;
                while (end < line.Length && IsTokenChar(line[end])) end++;
                var token = line.Substring(index, end - index).TrimEnd('.');
                // Only a name shaped like an exposed name counts; the bare word must not turn prose into a row.
                if (token.IndexOf("__", 5, StringComparison.Ordinal) > 0)
                    mentions.Add(new McpToolMention
                    {
                        Text = token,
                        Kind = McpMentionKind.Selection,
                        Start = lineStart + index,
                        Length = token.Length
                    });
                index = end;
                continue;
            }

            index++;
        }
    }

    private static int FirstNonSpace(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
        return index;
    }

    private static bool IsSimpleName(string name)
    {
        if (name.Length == 0 || !IsTokenStart(name[0])) return false;
        foreach (var c in name)
            if (!IsTokenChar(c)) return false;
        return true;
    }

    private static bool IsSingleWord(string text)
    {
        foreach (var c in text)
            if (char.IsWhiteSpace(c)) return false;
        return true;
    }

    private static bool IsTokenStart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsTokenChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/';
}
