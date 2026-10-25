using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Discovery answers one question — which definitions exist and which one a name means — without knowing
/// a harness or a model. The cases that matter are the ones a user will actually hit: a folder without SKILL.md, a
/// header that does not parse, a name that disagrees with its folder, and two copies of one name.</summary>
public sealed class SkillRegistryTests
{
    [Fact]
    public void Refresh_FindsSkillsInBothScopes()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Project, "code-review");
        temp.WriteSkill(temp.Personal, "release-notes");

        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        Assert.Equal(2, snapshot.Effective.Count);
        Assert.Equal(SkillScope.Project, snapshot.Find("code-review")!.Scope);
        Assert.Equal(SkillScope.Personal, snapshot.Find("release-notes")!.Scope);
    }

    [Fact]
    public void Refresh_IgnoresAFolderWithoutSkillMd()
    {
        using var temp = new SkillTestDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Personal, "not-a-skill"));
        temp.WriteSkill(temp.Personal, "real");

        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        Assert.Equal("real", Assert.Single(snapshot.All).Name);
    }

    [Fact]
    public void Refresh_KeepsAnInvalidDefinitionSoTheDashboardCanExplainIt()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteRaw(temp.Personal, "broken", "---\nname: broken\n---\nNo description here.");

        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var skill = Assert.Single(snapshot.All);
        Assert.False(skill.IsValid);
        Assert.Contains("description", skill.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(snapshot.Effective);
    }

    [Fact]
    public void Refresh_RefusesANameThatDoesNotMatchItsFolder()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteRaw(temp.Personal, "alpha",
            "---\nname: beta\ndescription: A skill.\n---\nBody.");

        var skill = Assert.Single(SkillTestDirectory.Snapshot(temp.Options()).All);

        Assert.False(skill.IsValid);
        Assert.Contains("does not match", skill.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_ProjectDefinitionWinsAndTheCopyThatLostIsMarked()
    {
        using var temp = new SkillTestDirectory();
        var project = temp.WriteSkill(temp.Project, "code-review", "Project copy.");
        var personal = temp.WriteSkill(temp.Personal, "code-review", "Personal copy.");

        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        Assert.Equal(SkillRegistry.PathKey(project), snapshot.Find("code-review")!.Directory);
        var loser = snapshot.All.Single(skill => SkillRegistry.PathKey(skill.Directory) == SkillRegistry.PathKey(personal));
        Assert.True(loser.Shadowed);
        Assert.Equal(SkillRegistry.PathKey(project), loser.ShadowedBy);
        var conflict = Assert.Single(snapshot.Conflicts);
        Assert.Equal(2, conflict.Others.Count + 1);
        Assert.Contains("code-review", conflict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_ADisabledWinningDefinitionLetsTheOtherCopyWinByPathNotByName()
    {
        using var temp = new SkillTestDirectory();
        var project = temp.WriteSkill(temp.Project, "code-review");
        var personal = temp.WriteSkill(temp.Personal, "code-review");
        var options = temp.Options();
        options.DisabledPaths = new[] { project };

        var snapshot = SkillTestDirectory.Snapshot(options);

        Assert.Equal(SkillRegistry.PathKey(personal), snapshot.Find("code-review")!.Directory);
        var disabled = snapshot.All.Single(skill => SkillRegistry.PathKey(skill.Directory) == SkillRegistry.PathKey(project));
        Assert.False(disabled.Enabled);
        Assert.False(disabled.Shadowed);
    }

    [Fact]
    public void Refresh_ScansTheCompatibleLocationsOnlyWhenAsked()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Claude, "native");
        var without = temp.Options(includeCompatible: false);
        var with = temp.Options(includeCompatible: true);

        Assert.Empty(SkillTestDirectory.Snapshot(without).All);
        var snapshot = SkillTestDirectory.Snapshot(with);
        Assert.True(Assert.Single(snapshot.All).IsClaudeNative);
    }

    [Fact]
    public void Refresh_ReusesTheSnapshotUntilAFileChanges()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var registry = new SkillRegistry();
        var options = temp.Options();

        var first = registry.Refresh(options);
        var second = registry.Refresh(options);
        // The length changes, so even a filesystem with a coarse write clock notices the edit.
        File.WriteAllText(Path.Combine(folder, "SKILL.md"),
            SkillFrontmatter.Build("alpha", "A longer description than before.", "Body."));
        var third = registry.Refresh(options);

        Assert.Same(first, second);
        Assert.NotSame(second, third);
        Assert.Equal("A longer description than before.", third.Find("alpha")!.Description);
    }

    [Fact]
    public void Refresh_PropagatesCancellation()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha");

        Assert.Throws<OperationCanceledException>(() =>
            new SkillRegistry().Refresh(temp.Options(), new CancellationToken(true)));
    }

    [Fact]
    public void Refresh_ReportsARootThatCannotBeReadWithoutFailingTheScan()
    {
        using var temp = new SkillTestDirectory();
        var options = temp.Options();
        // A file where the personal directory should be: it exists, but it cannot be listed as a directory.
        Directory.CreateDirectory(temp.Root);
        File.WriteAllText(temp.Personal, "not a directory");
        temp.WriteSkill(temp.Project, "still-found");

        var snapshot = SkillTestDirectory.Snapshot(options);

        Assert.Equal("still-found", Assert.Single(snapshot.Effective).Name);
        Assert.NotEmpty(snapshot.Problems);
    }

    [Fact]
    public void Settings_CarryTheLocationsAndDisabledPathsIntoTheOptions()
    {
        var settings = new UserSettings
        {
            SkillsPersonalDirectory = "C:\\skills",
            SkillsProjectDirectory = "custom/skills",
            SkillsIncludeCompatibleLocations = false,
            DisabledSkillPaths = new System.Collections.Generic.List<string> { "C:\\skills\\off" }
        };

        var options = SkillDiscoveryOptions.FromSettings(settings, "C:\\solution");

        Assert.Equal("C:\\skills", options.PersonalDirectory);
        Assert.Equal("custom/skills", options.ProjectDirectory);
        Assert.False(options.IncludeCompatibleLocations);
        Assert.Equal("C:\\skills\\off", Assert.Single(options.DisabledPaths));
    }

    [Fact]
    public void Settings_DefaultToTheDocumentedLocations()
    {
        // A settings file written before Skills existed has none of these fields; loading it must still produce a
        // usable feature rather than a disabled or rootless one.
        var settings = new UserSettings();

        Assert.True(settings.EnableSkills);
        Assert.True(settings.SkillAutomatic);
        Assert.True(settings.SkillsIncludeCompatibleLocations);
        Assert.Equal(SkillLocations.DefaultProjectDirectory, settings.SkillsProjectDirectory);
        Assert.Equal("", settings.SkillsPersonalDirectory);
        Assert.Empty(settings.DisabledSkillPaths);
    }

    [Fact]
    public void IsDirectChildOfAnyRoot_AcceptsOnlyAnImmediateChild()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var options = temp.Options();

        Assert.True(SkillRegistry.IsDirectChildOfAnyRoot(options, folder));
        Assert.False(SkillRegistry.IsDirectChildOfAnyRoot(options, Path.Combine(folder, "references")));
        Assert.False(SkillRegistry.IsDirectChildOfAnyRoot(options, Path.Combine(temp.Root, "..", "elsewhere")));
    }
}
