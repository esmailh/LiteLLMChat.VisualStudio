using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

public sealed class SkillDraft
{
    /// <summary>The reviewed draft. It is always text; whether it validates is <see cref="Error"/>'s answer.</summary>
    public string Content { get; set; } = "";
    public string? Error { get; set; }
    public bool IsValid => string.IsNullOrEmpty(Error);
}

/// <summary>Builds the request that asks the configured model for a skill, and turns what comes back into a
/// <c>SKILL.md</c> draft.
///
/// The model never writes the file. It returns text, the editor shows it, and only an explicit Save in the editor
/// calls <see cref="SkillStorage"/>. A malformed answer is kept as the draft with its validation error so the user
/// can fix it rather than losing what the model wrote.</summary>
public static class SkillGeneration
{
    public const int MaxNotesChars = 4000;
    public const int MaxExistingChars = 50000;

    public static List<ChatMessage> Request(string name, string description, string? examples = null,
        string? conventions = null, string? existingContent = null)
    {
        var improving = !string.IsNullOrWhiteSpace(existingContent);
        var system = new StringBuilder();
        system.Append("You write Agent Skills: reusable instructions that tell a coding assistant how to perform a specific task consistently.\n\n");
        system.Append("Return only one complete SKILL.md document. Do not wrap it in code fences and do not add commentary before or after it.\n");
        system.Append("It must begin with YAML frontmatter:\n");
        system.Append("---\nname: ").Append(name).Append("\ndescription: a single specific sentence, at most 1024 characters\n---\n\n");
        system.Append("After the frontmatter, write the Markdown instructions. They must be concrete and specific to the described task: what to inspect, the order to do it in, the checks to perform, how to report the result, and what not to do. Do not pad the document with generic assistant advice.\n");
        system.Append("Do not invent scripts or claim that anything was executed. If a workflow needs a command, say what the command is; the extension's own approval rules decide whether it may run.\n");
        system.Append("Keep the body focused: roughly 300 to 1200 words. Use headings and short lists where they help.\n");
        if (improving)
            system.Append("Improve the existing skill below. Preserve its intent and any project-specific detail. Keep the same name. Return the complete improved document, not a diff.\n");

        var user = new StringBuilder();
        user.Append("Name: ").Append(Clip(name, 120)).Append('\n');
        user.Append("What the skill should accomplish: ").Append(Clip(description, MaxNotesChars)).Append('\n');
        if (!string.IsNullOrWhiteSpace(examples))
            user.Append("\nExamples or situations where it applies:\n").Append(Clip(examples!, MaxNotesChars)).Append('\n');
        if (!string.IsNullOrWhiteSpace(conventions))
            user.Append("\nConventions and constraints to follow:\n").Append(Clip(conventions!, MaxNotesChars)).Append('\n');
        if (improving)
        {
            user.Append("\nExisting skill to improve:\n");
            user.Append(Clip(existingContent!, MaxExistingChars));
            if (existingContent!.Length > MaxExistingChars)
                user.Append("\n… [the existing skill was truncated in this request]");
            user.Append('\n');
        }

        return new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = system.ToString() },
            new ChatMessage { Role = "user", Content = user.ToString().TrimEnd() }
        };
    }

    /// <summary>Turns a model's answer into a draft whose name is the one the user entered. A well-formed answer
    /// keeps its own description and optional fields; the name is taken from the user because that is the folder
    /// the skill will be written to. A malformed answer is returned unchanged, with the parser's reason, so the
    /// editor still has something to review.</summary>
    public static SkillDraft Extract(string? response, string name, string description)
    {
        var draft = new SkillDraft();
        var text = (response ?? "").Trim();
        if (text.Length == 0)
        {
            draft.Error = "The model returned an empty draft.";
            return draft;
        }

        text = StripFence(text);
        var firstLine = FirstLine(text);
        if (firstLine.Trim() == "---")
        {
            var parsed = SkillFrontmatter.Parse(text);
            if (!parsed.IsValid)
            {
                draft.Content = text;
                draft.Error = parsed.Error;
                return draft;
            }

            var finalName = (name ?? "").Trim();
            var finalDescription = string.IsNullOrWhiteSpace(parsed.Description) ? (description ?? "").Trim() : parsed.Description.Trim();
            draft.Content = SkillFrontmatter.Build(finalName, finalDescription, parsed.Body, parsed.License,
                parsed.Compatibility, parsed.Metadata, parsed.AllowedTools);
            draft.Error = SkillFrontmatter.Parse(draft.Content).Error;
            return draft;
        }

        // A model that answered with instructions only is still useful. The frontmatter is built around it.
        draft.Content = SkillFrontmatter.Build((name ?? "").Trim(), (description ?? "").Trim(), text);
        draft.Error = SkillFrontmatter.Parse(draft.Content).Error;
        return draft;
    }

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text;
        var body = text.Substring(firstBreak + 1);
        var last = body.TrimEnd();
        if (last.EndsWith("```", StringComparison.Ordinal))
            body = last.Substring(0, last.Length - 3);
        return body.Trim();
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text.Substring(0, index);
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "…";
}
