using System;
using System.IO;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Every write path proves three things before it touches the disk: the name is a safe slug, the target is
/// a direct child of a configured root, and a destructive operation was confirmed. The tests below take each of
/// those away in turn and check that the folder survives.</summary>
public sealed class SkillStorageTests
{
    [Fact]
    public void Create_WritesAValidSkillIntoTheChosenScope()
    {
        using var temp = new SkillTestDirectory();

        var result = SkillStorage.Create(temp.Options(), SkillScope.Personal, "code-review",
            "Review a diff before it is committed.", "Read the diff.");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.Combine(temp.Personal, "code-review"), result.Path);
        var parsed = SkillFrontmatter.Parse(File.ReadAllText(Path.Combine(result.Path, "SKILL.md")));
        Assert.True(parsed.IsValid);
        Assert.Equal("code-review", parsed.Name);
        Assert.Equal("Read the diff.", parsed.Body.Trim());
    }

    [Fact]
    public void Create_RefusesToReplaceAnExistingFolder()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha", "The original.");
        var before = File.ReadAllText(Path.Combine(folder, "SKILL.md"));

        var result = SkillStorage.Create(temp.Options(), SkillScope.Personal, "alpha", "A replacement.", "New body.");

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "SKILL.md")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("UPPER")]
    public void Create_RejectsANameThatCouldEscapeOrIsNotASlug(string name)
    {
        using var temp = new SkillTestDirectory();

        var result = SkillStorage.Create(temp.Options(), SkillScope.Personal, name, "A description.", "Body.");

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(temp.Personal, name)));
    }

    [Fact]
    public void Create_ProjectScopeNeedsASolution()
    {
        using var temp = new SkillTestDirectory();
        var options = temp.Options();
        options.SolutionRoot = null;

        var result = SkillStorage.Create(options, SkillScope.Project, "alpha", "A description.", "Body.");

        Assert.False(result.Ok);
        Assert.Contains("open solution", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_NeedsConfirmationEvenForAValidChange()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;
        var before = File.ReadAllText(Path.Combine(folder, "SKILL.md"));

        var result = SkillStorage.Save(options, skill, SkillFrontmatter.Build("alpha", "Changed.", "Changed body."), confirmed: false);

        Assert.False(result.Ok);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "SKILL.md")));
    }

    [Fact]
    public void Save_WritesOnlySkillMdAndKeepsTheFilesBesideIt()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha", "Old description.");
        Directory.CreateDirectory(Path.Combine(folder, "references"));
        File.WriteAllText(Path.Combine(folder, "references", "notes.md"), "kept");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;

        var result = SkillStorage.Save(options, skill,
            SkillFrontmatter.Build("alpha", "New description.", "New body."), confirmed: true);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("New description.", File.ReadAllText(Path.Combine(folder, "SKILL.md")), StringComparison.Ordinal);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(folder, "references", "notes.md")));
    }

    [Fact]
    public void Save_RefusesASkillThatIsNotUnderAConfiguredRoot()
    {
        using var temp = new SkillTestDirectory();
        var options = temp.Options();
        var outside = Path.Combine(temp.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var skill = new SkillInfo { Name = "alpha", Directory = outside };

        var result = SkillStorage.Save(options, skill, SkillFrontmatter.Build("alpha", "A description.", "Body."), confirmed: true);

        Assert.False(result.Ok);
        Assert.Contains("not inside", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(outside, "SKILL.md")));
    }

    [Fact]
    public void Save_RefusesAContentWhoseNameDisagreesWithTheFolder()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;

        var result = SkillStorage.Save(options, skill,
            SkillFrontmatter.Build("beta", "A description.", "Body."), confirmed: true);

        Assert.False(result.Ok);
        Assert.Contains("must match the folder", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("alpha", SkillFrontmatter.Parse(File.ReadAllText(Path.Combine(folder, "SKILL.md"))).Name);
    }

    [Fact]
    public void Read_ReturnsTheFileAndRefusesOneOutsideTheRoots()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha", body: "READ ME");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;

        var inside = SkillStorage.Read(options, skill, out var content);

        Assert.True(inside.Ok);
        Assert.Contains("READ ME", content, StringComparison.Ordinal);

        var outside = new SkillInfo { Name = "alpha", Directory = Path.Combine(temp.Root, "elsewhere") };
        var refused = SkillStorage.Read(options, outside, out _);
        Assert.False(refused.Ok);
    }

    [Fact]
    public void Delete_NeedsConfirmation()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;

        var result = SkillStorage.Delete(options, skill, confirmed: false);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Delete_RemovesTheWholeFolderAfterConfirmation()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "scripts", "run.ps1"), "Write-Host 'never executed by the extension'");
        var options = temp.Options();
        var skill = SkillTestDirectory.Snapshot(options).Find("alpha")!;

        var result = SkillStorage.Delete(options, skill, confirmed: true);

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Delete_RefusesAFolderOutsideTheRoots()
    {
        using var temp = new SkillTestDirectory();
        var outside = Path.Combine(temp.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), SkillFrontmatter.Build("elsewhere", "A description.", "Body."));
        var options = temp.Options();
        var skill = new SkillInfo { Name = "elsewhere", Directory = outside };

        var result = SkillStorage.Delete(options, skill, confirmed: true);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Delete_RefusesAFolderWithoutSkillMd()
    {
        using var temp = new SkillTestDirectory();
        var folder = Path.Combine(temp.Personal, "alpha");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a skill");
        var options = temp.Options();
        var skill = new SkillInfo { Name = "alpha", Directory = folder };

        var result = SkillStorage.Delete(options, skill, confirmed: true);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Import_CopiesTheFolderAndItsResources()
    {
        using var temp = new SkillTestDirectory();
        var source = Path.Combine(temp.Root, "incoming", "alpha");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        File.WriteAllText(Path.Combine(source, "SKILL.md"), SkillFrontmatter.Build("alpha", "A description.", "Body."));
        File.WriteAllText(Path.Combine(source, "references", "notes.md"), "A note.");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        File.WriteAllText(Path.Combine(source, ".git", "config"), "private");

        var result = SkillStorage.Import(temp.Options(), SkillScope.Personal, source);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.Combine(temp.Personal, "alpha"), result.Path);
        Assert.True(File.Exists(Path.Combine(result.Path, "references", "notes.md")));
        Assert.False(Directory.Exists(Path.Combine(result.Path, ".git")));
    }

    [Fact]
    public void Import_RefusesToReplaceWithoutAnExplicitOverwrite()
    {
        using var temp = new SkillTestDirectory();
        var source = Path.Combine(temp.Root, "incoming", "alpha");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"),
            SkillFrontmatter.Build("alpha", "The imported description.", "Imported body."));
        var existing = temp.WriteSkill(temp.Personal, "alpha", "The existing description.");

        var refused = SkillStorage.Import(temp.Options(), SkillScope.Personal, source);
        var before = File.ReadAllText(Path.Combine(existing, "SKILL.md"));

        Assert.False(refused.Ok);
        Assert.Contains("already exists", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The existing description.", before, StringComparison.Ordinal);

        var replaced = SkillStorage.Import(temp.Options(), SkillScope.Personal, source, overwrite: true);

        Assert.True(replaced.Ok, replaced.Error);
        Assert.Contains("The imported description.",
            File.ReadAllText(Path.Combine(existing, "SKILL.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesAFolderThatIsNotASkill()
    {
        using var temp = new SkillTestDirectory();
        var source = Path.Combine(temp.Root, "incoming", "plain");
        Directory.CreateDirectory(source);

        var result = SkillStorage.Import(temp.Options(), SkillScope.Personal, source);

        Assert.False(result.Ok);
        Assert.Contains("SKILL.md", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(temp.Personal, "plain")));
    }

    [Fact]
    public void Import_RefusesAnInvalidSourceAndLeavesNoPartialCopy()
    {
        using var temp = new SkillTestDirectory();
        var source = Path.Combine(temp.Root, "incoming", "broken");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: broken\n---\nNo description.");

        var result = SkillStorage.Import(temp.Options(), SkillScope.Personal, source);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(temp.Personal, "broken")));
        Assert.Empty(Directory.Exists(temp.Personal) ? Directory.GetDirectories(temp.Personal) : Array.Empty<string>());
    }

    [Fact]
    public void Session_KeepsSelectedSkillsAcrossASaveAndALoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ChatSessionStore(path);
            var document = new ChatSessionDocument
            {
                Sessions =
                {
                    new ChatSessionState
                    {
                        Title = "Skills",
                        SelectedSkills = { " alpha ", "ALPHA", "code-review", "", "beta" }
                    }
                }
            };

            store.Save(document);
            var loaded = new ChatSessionStore(path).Load();

            Assert.Equal(new[] { "alpha", "code-review", "beta" }, Assert.Single(loaded.Sessions).SelectedSkills);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }

    [Fact]
    public void Session_WithoutSelectedSkillsStillLoads()
    {
        // A conversation written before Skills existed has no such field; opening it must not lose the chat.
        var path = Path.Combine(Path.GetTempPath(), "litellm-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path,
                "{\"Version\":1,\"ActiveSessionId\":\"" + Guid.NewGuid() + "\"," +
                "\"Sessions\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Title\":\"Old chat\",\"Turns\":[]}]}");

            var loaded = new ChatSessionStore(path).Load();

            var session = Assert.Single(loaded.Sessions);
            Assert.Equal("Old chat", session.Title);
            Assert.NotNull(session.SelectedSkills);
            Assert.Empty(session.SelectedSkills);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
