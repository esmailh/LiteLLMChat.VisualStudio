using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class SkillToolResult
{
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Ok { get; set; } = true;
}

/// <summary>The two tools that make progressive disclosure work on the LiteLLM backend.
///
/// They read only. Nothing here executes a file, follows a link out of the skill directory, or grants anything:
/// an instruction inside a skill that says "run this script" is still an instruction, and the terminal tool that
/// could run it keeps its own approval card.</summary>
public static class SkillTools
{
    public const string LoadSkill = "load_skill";
    public const string ReadSkillFile = "read_skill_file";

    private static readonly IReadOnlyList<ToolDefinition> ToolDefinitions = new List<ToolDefinition>
    {
        new ToolDefinition
        {
            Function = new JObject
            {
                ["name"] = LoadSkill,
                ["description"] = "Load the full instructions of one available skill by name. Only the catalog is in the request; call this before applying a skill, and do not claim a skill is loaded until this call returns.",
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["name"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "The exact skill name from the catalog."
                        }
                    },
                    ["required"] = new JArray("name"),
                    ["additionalProperties"] = false
                }
            }
        },
        new ToolDefinition
        {
            Function = new JObject
            {
                ["name"] = ReadSkillFile,
                ["description"] = "Read one text file that ships inside a skill, such as a reference or an example. The path is confined to that skill's directory. Scripts are read as text and never executed.",
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["name"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "The exact skill name."
                        },
                        ["path"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "A path relative to the skill directory, such as references/conventions.md."
                        }
                    },
                    ["required"] = new JArray("name", "path"),
                    ["additionalProperties"] = false
                }
            }
        }
    };

    public static IReadOnlyList<ToolDefinition> Definitions => ToolDefinitions;

    public static bool Handles(string? name) =>
        string.Equals(name, LoadSkill, StringComparison.Ordinal) ||
        string.Equals(name, ReadSkillFile, StringComparison.Ordinal);

    /// <summary>Runs one call. Every failure is a result, not an exception: the announced call still needs an
    /// answer, and a model that is told what was wrong can correct it.</summary>
    public static SkillToolResult Execute(SkillRegistrySnapshot snapshot, string name, string? arguments,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        snapshot ??= SkillRegistrySnapshot.Empty;
        try
        {
            if (!ToolArguments.TryParse(arguments, out var args, out var argumentsError))
                return Failed(name, argumentsError);
            if (!ToolArguments.TryText(args, "name", out var skillName, out var nameError))
                return Failed(name, nameError);
            skillName = skillName.Trim();
            if (skillName.Length == 0)
                return Failed(name, "The call needs a skill name. Available skills: " + Available(snapshot) + ".");

            var skill = snapshot.Find(skillName);
            if (skill == null) return Failed(name, Unavailable(snapshot, skillName));

            if (string.Equals(name, LoadSkill, StringComparison.Ordinal))
                return Load(skill, token);
            if (string.Equals(name, ReadSkillFile, StringComparison.Ordinal))
                return Read(skill, args, token);
            return Failed(name, "The tool '" + name + "' is not a skill tool.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Failed(name, "The skill call failed: " + ex.Message);
        }
    }

    private static SkillToolResult Load(SkillInfo skill, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SkillContent.TryReadBody(skill, out var body, out var truncated, out var error))
            return new SkillToolResult
            {
                Summary = "Skill · " + skill.Name + " could not be loaded",
                Ok = false,
                Content = "The skill '" + skill.Name + "' could not be loaded: " + error
            };

        var text = new StringBuilder();
        text.Append("Skill: ").Append(skill.Name).Append('\n');
        text.Append("Scope: ").Append(skill.Scope == SkillScope.Project ? "project" : "personal").Append('\n');
        text.Append("Location: ").Append(SkillLocations.DisplayPath(skill.Directory)).Append('\n');
        if (skill.Description.Length > 0) text.Append("Description: ").Append(skill.Description).Append('\n');
        text.Append('\n');
        if (body.Length == 0) text.Append("(This skill has no instructions beyond its metadata.)\n");
        else text.Append(body).Append('\n');
        if (skill.Resources.Count > 0)
        {
            text.Append("\nResources (read with read_skill_file; do not execute them):\n");
            foreach (var resource in skill.Resources.Take(60))
                text.Append("- ").Append(resource.RelativePath).Append(" (").Append(resource.Kind).Append(", ")
                    .Append(resource.SizeBytes).Append(" bytes)\n");
            if (skill.Resources.Count > 60)
                text.Append("- … ").Append(skill.Resources.Count - 60).Append(" more; the Dashboard lists the rest.\n");
        }
        if (truncated)
            text.Append("\nThe instructions were shortened to ").Append(SkillContent.MaxBodyChars)
                .Append(" characters by the extension.\n");

        return new SkillToolResult
        {
            Content = text.ToString(),
            Summary = "Skill · " + skill.Name + " loaded" + (truncated ? " (shortened)" : "")
        };
    }

    private static SkillToolResult Read(SkillInfo skill, JObject args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ToolArguments.TryText(args, "path", out var path, out var pathError))
            return new SkillToolResult
            {
                Summary = "Skill · " + skill.Name + " · invalid path",
                Ok = false,
                Content = pathError
            };
        if (string.IsNullOrWhiteSpace(path))
            return new SkillToolResult
            {
                Summary = "Skill · " + skill.Name + " · no path",
                Ok = false,
                Content = "The call needs a path inside the skill, such as references/conventions.md."
            };

        if (!SkillContent.TryReadResource(skill, path, out var content, out var shown, out var error))
            return new SkillToolResult
            {
                Summary = "Skill · " + skill.Name + " · " + shown + " not read",
                Ok = false,
                Content = error
            };

        return new SkillToolResult
        {
            Content = "File: " + skill.Name + "/" + SkillLocations.DisplayPath(shown) + "\n\n" + content,
            Summary = "Skill · " + skill.Name + " · " + shown
        };
    }

    private static SkillToolResult Failed(string tool, string content) => new SkillToolResult
    {
        Content = content,
        Summary = "Skill · " + tool + " failed",
        Ok = false
    };

    private static string Available(SkillRegistrySnapshot snapshot) =>
        snapshot.Effective.Count == 0
            ? "none"
            : string.Join(", ", snapshot.Effective.Take(30).Select(skill => skill.Name)) +
              (snapshot.Effective.Count > 30 ? ", …" : "");

    private static string Unavailable(SkillRegistrySnapshot snapshot, string name)
    {
        var candidates = snapshot.All.Where(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Any(skill => skill.IsValid && !skill.Enabled))
            return "The skill '" + name + "' is disabled in the Dashboard, so it cannot be loaded.";
        var invalid = candidates.FirstOrDefault(skill => !skill.IsValid);
        if (invalid != null)
            return "The skill '" + name + "' cannot be used: " + invalid.Error;
        var suggestions = snapshot.Match(name).Take(5).Select(skill => skill.Name).ToList();
        return "No skill named '" + name + "' is available." +
               (suggestions.Count > 0 ? " Did you mean " + string.Join(", ", suggestions) + "?" : "") +
               " Available skills: " + Available(snapshot) + ".";
    }
}
