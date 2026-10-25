using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteLLM.Core;

/// <summary>Where a skill came from. Scope is about the user's intent — "just this solution" versus "on this
/// computer" — not about the harness; both backends use the same list.</summary>
public enum SkillScope
{
    Project,
    Personal
}

/// <summary>One file that ships beside <c>SKILL.md</c>. The kind is a display hint taken from the first folder
/// (<c>references</c>, <c>scripts</c>, <c>assets</c>), and nothing here is executable: the Skills feature never
/// runs a script merely because it exists.</summary>
public sealed class SkillResourceInfo
{
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "file";
    public long SizeBytes { get; set; }
}

/// <summary>A discovered skill, valid or not. An invalid entry is kept rather than hidden: a malformed
/// <c>SKILL.md</c> is exactly what the user needs to see in the Dashboard, and suppressing it would leave them
/// wondering why a folder they created never appears.
///
/// <see cref="Shadowed"/> is not an error. It means another definition of the same name won by precedence, and
/// the Dashboard shows both paths so the choice is never made silently.</summary>
public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public string Compatibility { get; set; } = "";
    public string AllowedTools { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public SkillScope Scope { get; set; }
    /// <summary>A short, user-facing label for the configured root, such as <c>Project · .agents/skills</c>.</summary>
    public string Source { get; set; } = "";
    /// <summary>Full path of the skill directory. This is the identity used for enabled state.</summary>
    public string Directory { get; set; } = "";
    public string SkillFilePath { get; set; } = "";
    /// <summary>Why this definition cannot be used, or null when it is valid.</summary>
    public string? Error { get; set; }
    /// <summary>Non-fatal notes, such as a resource list that was cut short.</summary>
    public List<string> Warnings { get; set; } = new List<string>();
    /// <summary>True when a higher-precedence definition of the same name is effective.</summary>
    public bool Shadowed { get; set; }
    public string ShadowedBy { get; set; } = "";
    /// <summary>Set by the registry from the user's disabled-path list.</summary>
    public bool Enabled { get; set; } = true;
    public List<SkillResourceInfo> Resources { get; set; } = new List<SkillResourceInfo>();
    public DateTime LastWriteUtc { get; set; }
    public long ContentLength { get; set; }

    public bool IsValid => string.IsNullOrEmpty(Error);

    /// <summary>A definition that can actually be offered to a model.</summary>
    public bool IsEffective => IsValid && Enabled && !Shadowed;

    /// <summary>The path shown in the Dashboard and in prompt rows. Relative to the solution where possible, so
    /// a project skill reads as <c>.agents/skills/code-review</c> instead of a long absolute path.</summary>
    public string LocationLabel => string.IsNullOrEmpty(Directory) ? Source : Path.GetFileName(Directory);

    /// <summary>True when the CLI already discovers this directory natively. Used on the Claude backend to
    /// reference a skill instead of injecting its body twice.</summary>
    public bool IsClaudeNative => IsClaudeNativeDirectory(Directory);

    internal static bool IsClaudeNativeDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        var normalized = directory.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.IndexOf(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar + "skills" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public string ResourceSummary
    {
        get
        {
            if (Resources.Count == 0) return "";
            return string.Join(" · ", Resources.GroupBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count() + " " + g.Key + (g.Count() == 1 ? "" : "s")));
        }
    }
}
