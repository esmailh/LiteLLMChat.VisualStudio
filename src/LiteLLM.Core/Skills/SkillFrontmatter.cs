using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

/// <summary>The metadata at the top of one <c>SKILL.md</c>, plus the Markdown body below it.
///
/// The Agent Skills specification uses a small subset of YAML. This is a hand-written reader for that subset
/// rather than a YAML package: a net48 VSIX that already hand-rolls its MCP stack cannot afford a new
/// dependency's binding conflicts for six scalar fields and one string map. Unknown top-level keys are ignored
/// so a file written for a newer revision of the specification still loads.
///
/// Validation is part of parsing, not a separate pass. A caller that gets an <see cref="Error"/> must not use
/// the body: the file did not say what it claims to be, and offering it to a model would be trusting a document
/// whose own header is broken.</summary>
public sealed class SkillFrontmatter
{
    public const int MaxNameChars = 64;
    public const int MaxDescriptionChars = 1024;
    public const int MaxCompatibilityChars = 500;
    public const int MaxFileBytes = 1024 * 1024;

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public string Compatibility { get; set; } = "";
    public string AllowedTools { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Everything after the closing <c>---</c>. This is the part that becomes instructions.</summary>
    public string Body { get; set; } = "";
    /// <summary>Why the file is invalid, or null when it is usable.</summary>
    public string? Error { get; set; }

    public bool IsValid => string.IsNullOrEmpty(Error);

    public static SkillFrontmatter Parse(string? text)
    {
        var result = new SkillFrontmatter();
        if (string.IsNullOrEmpty(text))
        {
            result.Error = "SKILL.md is empty.";
            return result;
        }

        var normalized = text!.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '﻿') normalized = normalized.Substring(1);
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            result.Error = "SKILL.md must start with a YAML frontmatter block delimited by --- lines.";
            return result;
        }

        var close = -1;
        for (var index = 1; index < lines.Length; index++)
            if (lines[index].Trim() == "---") { close = index; break; }

        if (close < 0)
        {
            result.Error = "The frontmatter block is never closed. Add a --- line after the metadata.";
            return result;
        }

        result.Body = string.Join("\n", lines, close + 1, lines.Length - close - 1).TrimStart('\n');
        var error = ParseFields(lines, 1, close, result);
        if (error != null)
        {
            result.Error = error;
            return result;
        }

        result.Validate();
        return result;
    }

    /// <summary>The exact inverse of <see cref="Parse"/> for a skill the extension creates. Every scalar is
    /// double-quoted, so a description containing a colon, a hash or a line break cannot change the shape of the
    /// document; the parser unescapes it back to the text the user typed.</summary>
    public static string Build(string name, string description, string body, string license = "",
        string compatibility = "", IReadOnlyDictionary<string, string>? metadata = null, string allowedTools = "")
    {
        var text = new StringBuilder();
        text.Append("---\n");
        text.Append("name: ").Append(Quote(name.Trim())).Append('\n');
        text.Append("description: ").Append(Quote(description.Trim())).Append('\n');
        if (!string.IsNullOrWhiteSpace(license)) text.Append("license: ").Append(Quote(license.Trim())).Append('\n');
        if (!string.IsNullOrWhiteSpace(compatibility)) text.Append("compatibility: ").Append(Quote(compatibility.Trim())).Append('\n');
        if (!string.IsNullOrWhiteSpace(allowedTools)) text.Append("allowed-tools: ").Append(Quote(allowedTools.Trim())).Append('\n');
        if (metadata != null && metadata.Count > 0)
        {
            text.Append("metadata:\n");
            foreach (var pair in metadata)
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    text.Append("  ").Append(Quote(pair.Key.Trim())).Append(": ").Append(Quote(pair.Value ?? "")).Append('\n');
        }
        text.Append("---\n\n");
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length > 0) text.Append(trimmed).Append('\n');
        return text.ToString();
    }

    /// <summary>Null when the name follows the specification, otherwise a sentence for the editor to show.</summary>
    public static string? ValidateName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.Length == 0) return "The name is required.";
        if (name.Length > MaxNameChars) return "The name must be at most " + MaxNameChars + " characters.";
        if (name[0] == '-' || name[name.Length - 1] == '-') return "The name cannot start or end with a hyphen.";
        for (var index = 0; index < name.Length; index++)
        {
            var c = name[index];
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') continue;
            return "The name may contain only lowercase letters, digits and hyphens; '" + SafeChar(c) + "' is not allowed.";
        }
        if (name.Contains("--")) return "The name may contain only lowercase letters, digits and hyphens; two hyphens in a row are not allowed.";
        return null;
    }

    public static string? ValidateDescription(string? value)
    {
        var description = (value ?? "").Trim();
        if (description.Length == 0) return "The description is required.";
        if (description.Length > MaxDescriptionChars)
            return "The description must be at most " + MaxDescriptionChars + " characters (" + description.Length + " now).";
        return null;
    }

    private void Validate()
    {
        var name = ValidateName(Name);
        if (name != null) { Error = name; return; }
        Name = Name.Trim();

        var description = ValidateDescription(Description);
        if (description != null) { Error = description; return; }
        Description = Description.Trim();

        if (Compatibility.Length > MaxCompatibilityChars)
        {
            Error = "compatibility must be at most " + MaxCompatibilityChars + " characters.";
            return;
        }
    }

    private static string? ParseFields(string[] lines, int start, int end, SkillFrontmatter result)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < end; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (raw[0] == ' ' || raw[0] == '\t')
                return "Unexpected indentation on line " + (index + 1) + " of the frontmatter.";

            var colon = raw.IndexOf(':');
            if (colon <= 0) return "Line " + (index + 1) + " of the frontmatter is not a key: value pair.";
            var key = Unquote(raw.Substring(0, colon).Trim());
            if (key.Length == 0) return "Line " + (index + 1) + " of the frontmatter has no field name.";
            if (!seen.Add(key)) return "The field '" + key + "' appears more than once in the frontmatter.";

            var value = StripComment(raw.Substring(colon + 1).Trim());

            if (key.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                var error = ParseMetadata(lines, ref index, end, result, value);
                if (error != null) return error;
                continue;
            }

            if (value == "|" || value == "|-" || value == "|+" || value == ">" || value == ">-" || value == ">+")
            {
                value = ReadBlock(lines, ref index, end, value[0] == '>');
            }
            else
            {
                value = Unquote(value);
            }

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) result.Name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) result.Description = value;
            else if (key.Equals("license", StringComparison.OrdinalIgnoreCase)) result.License = value;
            else if (key.Equals("compatibility", StringComparison.OrdinalIgnoreCase)) result.Compatibility = value;
            else if (key.Equals("allowed-tools", StringComparison.OrdinalIgnoreCase)) result.AllowedTools = value;
            // Everything else is ignored on purpose: the specification is allowed to grow fields, and rejecting
            // a file for a field this build does not know would be stricter than the format demands.
        }
        return null;
    }

    private static string? ParseMetadata(string[] lines, ref int index, int end, SkillFrontmatter result,
        string inline)
    {
        // `metadata:` alone opens the block. `metadata: {}` is the same empty map. Anything else on that line
        // would be a mapping the reader does not support, and dropping it silently would lose data.
        if (inline.Length > 0 && !inline.Equals("{}", StringComparison.Ordinal) && !inline.Equals("null", StringComparison.OrdinalIgnoreCase))
            return "metadata must be a block mapping, one 'key: value' per indented line.";

        while (index + 1 < end)
        {
            var next = lines[index + 1];
            var trimmed = next.Trim();
            if (trimmed.Length == 0) { index++; continue; }
            if (next[0] != ' ' && next[0] != '\t') break;
            index++;
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) return "Line " + (index + 1) + " of the metadata block is not a key: value pair.";
            var key = Unquote(trimmed.Substring(0, colon).Trim());
            var value = Unquote(StripComment(trimmed.Substring(colon + 1).Trim()));
            if (key.Length == 0) return "Line " + (index + 1) + " of the metadata block has no key.";
            if (result.Metadata.ContainsKey(key)) return "The metadata key '" + key + "' appears more than once.";
            result.Metadata[key] = value;
        }
        return null;
    }

    /// <summary>Reads a literal (<c>|</c>) or folded (<c>&gt;</c>) block scalar and leaves <paramref name="index"/>
    /// on the last line it consumed.</summary>
    private static string ReadBlock(string[] lines, ref int index, int end, bool folded)
    {
        var collected = new List<string>();
        var indent = -1;
        while (index + 1 < end)
        {
            var next = lines[index + 1];
            if (next.Trim().Length == 0) { collected.Add(""); index++; continue; }
            var leading = 0;
            while (leading < next.Length && (next[leading] == ' ' || next[leading] == '\t')) leading++;
            if (indent < 0) indent = leading;
            if (leading < indent) break;
            collected.Add(next.Substring(indent));
            index++;
        }
        while (collected.Count > 0 && collected[collected.Count - 1].Length == 0) collected.RemoveAt(collected.Count - 1);
        return folded ? string.Join(" ", collected).Trim() : string.Join("\n", collected);
    }

    private static string StripComment(string value)
    {
        var single = false;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c == '\'' && !quoted) single = !single;
            else if (c == '"' && !single) quoted = !quoted;
            else if (c == '#' && !single && !quoted && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                return value.Substring(0, index).TrimEnd();
        }
        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            return UnescapeDouble(value.Substring(1, value.Length - 2));
        if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            return value.Substring(1, value.Length - 2).Replace("''", "'");
        return value;
    }

    private static string UnescapeDouble(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c != '\\' || index + 1 >= value.Length) { text.Append(c); continue; }
            var next = value[++index];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                default: text.Append('\\').Append(next); break;
            }
        }
        return text.ToString();
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);
        text.Append('"');
        foreach (var c in value ?? "")
        {
            switch (c)
            {
                case '\\': text.Append("\\\\"); break;
                case '"': text.Append("\\\""); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default: text.Append(c); break;
            }
        }
        text.Append('"');
        return text.ToString();
    }

    private static string SafeChar(char c) => char.IsControl(c) ? "control character" : c.ToString();
}
