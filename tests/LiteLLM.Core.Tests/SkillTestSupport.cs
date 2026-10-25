using System;
using System.IO;
using LiteLLM.Core;

namespace LiteLLM.Core.Tests;

/// <summary>A throwaway directory shaped like a solution: a personal folder outside it and a project folder inside
/// it. Nothing here touches the real profile, so a test can create, disable and delete definitions freely.</summary>
internal sealed class SkillTestDirectory : IDisposable
{
    internal SkillTestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "litellm-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string Personal => Path.Combine(Root, "personal");
    internal string Project => Path.Combine(Root, ".agents", "skills");
    internal string Claude => Path.Combine(Root, ".claude", "skills");

    internal SkillDiscoveryOptions Options(bool includeCompatible = false) => new SkillDiscoveryOptions
    {
        SolutionRoot = Root,
        PersonalDirectory = Personal,
        ProjectDirectory = SkillLocations.DefaultProjectDirectory,
        IncludeCompatibleLocations = includeCompatible
    };

    internal string WriteSkill(string root, string name, string description = "Use this when the task matches.",
        string body = "Do the thing carefully.")
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), SkillFrontmatter.Build(name, description, body));
        return folder;
    }

    internal string WriteRaw(string root, string folderName, string content)
    {
        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), content);
        return folder;
    }

    internal static SkillRegistrySnapshot Snapshot(SkillDiscoveryOptions options)
    {
        var registry = new SkillRegistry();
        return registry.Refresh(options);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch (Exception) { }
    }
}
