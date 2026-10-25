using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>How a skill reaches a request: automatic discovery offers metadata only, a manual selection injects the
/// body the user asked for, and <c>/skill</c> is parsed locally so a handled command never becomes ordinary text.
/// The tools are covered here too, because they are the other half of progressive disclosure.</summary>
public sealed class SkillInvocationTests
{
    [Fact]
    public void Automatic_OffersTheCatalogAndNotTheInstructions()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review", "Review a diff before committing.", "SECRET BODY TEXT");

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), null, automatic: true, toolCalling: true);

        Assert.Contains("Available skills (catalog only)", plan.Instructions, StringComparison.Ordinal);
        Assert.Contains("code-review", plan.Instructions, StringComparison.Ordinal);
        Assert.Contains("Review a diff before committing.", plan.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET BODY TEXT", plan.Instructions, StringComparison.Ordinal);
        Assert.True(plan.OffersTools);
        Assert.Empty(plan.Selected);
    }

    [Fact]
    public void Automatic_WithoutToolCallingSaysSoInsteadOfPretendingItOfferedSomething()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review");

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), null, automatic: true, toolCalling: false);

        Assert.Equal("", plan.Instructions);
        Assert.False(plan.OffersTools);
        var row = Assert.Single(plan.Rows);
        Assert.False(row.Ok);
        Assert.Contains("tool calling", row.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selected_LoadsTheBodyAndDoesNotRepeatItInTheCatalog()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review", "Review a diff.", "THE REVIEW STEPS");

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), new[] { "code-review" },
            automatic: true, toolCalling: true);

        Assert.Contains("THE REVIEW STEPS", plan.Instructions, StringComparison.Ordinal);
        Assert.Contains("Selected skills (loaded for this turn)", plan.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Available skills (catalog only)", plan.Instructions, StringComparison.Ordinal);
        Assert.Equal("code-review", Assert.Single(plan.Selected).Name);
    }

    [Fact]
    public void Selected_IsListedOnceEvenIfTheConversationStoredItTwice()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha", body: "ONLY ONCE");

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), new[] { "alpha", "ALPHA" },
            automatic: false, toolCalling: false);

        var marker = plan.Instructions.Split(new[] { "ONLY ONCE" }, StringSplitOptions.None);
        Assert.Equal(2, marker.Length);
        Assert.Single(plan.Selected);
    }

    [Fact]
    public void Selected_MissingNameDrawsARowAndAddsNothingToThePrompt()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review");

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), new[] { "no-such-skill" },
            automatic: false, toolCalling: true);

        Assert.Equal("", plan.Instructions);
        var row = Assert.Single(plan.Rows);
        Assert.False(row.Ok);
        Assert.Contains("was not found", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_DisabledNameSaysWhereToTurnItOn()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        var options = temp.Options();
        options.DisabledPaths = new[] { folder };

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(options), new[] { "alpha" }, automatic: false, toolCalling: true);

        var row = Assert.Single(plan.Rows);
        Assert.Contains("disabled", row.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.Selected);
    }

    [Fact]
    public void OffersTools_IsFalseWhenThereIsNothingTheToolsCouldResolve()
    {
        using var temp = new SkillTestDirectory();

        var plan = SkillPrompt.Build(SkillTestDirectory.Snapshot(temp.Options()), null, automatic: true, toolCalling: true);

        Assert.False(plan.OffersTools);
        Assert.Equal("", plan.Instructions);
    }

    [Fact]
    public void ClaudeBlock_InjectsANonNativeBody()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha", body: "PERSONAL INSTRUCTIONS");
        var rows = new List<SkillTurnRow>();

        var block = SkillPrompt.BuildClaudeBlock(SkillTestDirectory.Snapshot(temp.Options()), new[] { "alpha" }, rows);

        Assert.NotNull(block);
        Assert.Contains("PERSONAL INSTRUCTIONS", block!, StringComparison.Ordinal);
        Assert.DoesNotContain("discovered by Claude Code", block!, StringComparison.Ordinal);
        Assert.True(Assert.Single(rows).Ok);
    }

    [Fact]
    public void ClaudeBlock_NamesANativeSkillInsteadOfSendingItsBodyTwice()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Claude, "alpha", body: "NATIVE BODY MARKER");
        var rows = new List<SkillTurnRow>();
        var options = temp.Options(includeCompatible: true);

        var block = SkillPrompt.BuildClaudeBlock(SkillTestDirectory.Snapshot(options), new[] { "alpha" }, rows);

        Assert.NotNull(block);
        Assert.Contains("discovered by Claude Code", block!, StringComparison.Ordinal);
        Assert.DoesNotContain("NATIVE BODY MARKER", block!, StringComparison.Ordinal);
        Assert.Contains(".claude/skills", Assert.Single(rows).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeBlock_IsNullWhenNothingWasSelected()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha");

        Assert.Null(SkillPrompt.BuildClaudeBlock(SkillTestDirectory.Snapshot(temp.Options()), null, new List<SkillTurnRow>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("review this diff")]
    [InlineData("/skills code-review")]
    [InlineData("please /skill code-review")]
    public void Slash_LeavesOrdinaryTextAlone(string text)
    {
        Assert.Equal(SkillCommandKind.None, SkillSlashCommand.Parse(text).Kind);
    }

    [Fact]
    public void Slash_ReadsTheNameAndKeepsTheRestAsTheMessage()
    {
        var command = SkillSlashCommand.Parse("  /SKILL code-review  review the last commit  ");

        Assert.Equal(SkillCommandKind.Invoked, command.Kind);
        Assert.Equal("code-review", command.Name);
        Assert.Equal("review the last commit", command.Remaining);
    }

    [Fact]
    public void Slash_WithoutANameAsksForOne()
    {
        Assert.Equal(SkillCommandKind.MissingName, SkillSlashCommand.Parse("/skill").Kind);
    }

    [Fact]
    public void Slash_ResolvesAnExactNameBeforeAPrefix()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code");
        temp.WriteSkill(temp.Personal, "code-review");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var skills = SkillSlashCommand.Resolve(snapshot.Effective, "code", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal("code", Assert.Single(skills).Name);
    }

    [Fact]
    public void Slash_AcceptsAUniquePrefix()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var skills = SkillSlashCommand.Resolve(snapshot.Effective, "code", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal("code-review", Assert.Single(skills).Name);
    }

    [Fact]
    public void Slash_ReportsAnAmbiguousPrefixRatherThanChoosingOne()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "code-review");
        temp.WriteSkill(temp.Personal, "code-explain");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var skills = SkillSlashCommand.Resolve(snapshot.Effective, "code", out var ambiguous);

        Assert.True(ambiguous);
        Assert.Equal(2, skills.Count);
    }

    [Fact]
    public void Completion_FollowsOnlyTheNameWord()
    {
        Assert.True(SkillSlashCommand.TryGetNameFilter("/skill cod", 10, out var start, out var length, out var filter));
        Assert.Equal(7, start);
        Assert.Equal(3, length);
        Assert.Equal("cod", filter);

        Assert.False(SkillSlashCommand.TryGetNameFilter("/skill code-review review this", 30, out _, out _, out _));
        Assert.False(SkillSlashCommand.TryGetNameFilter("code-review", 5, out _, out _, out _));
        Assert.False(SkillSlashCommand.TryGetNameFilter("/skill", 3, out _, out _, out _));
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public void LoadSkill_ReturnsTheBodyAndItsResources()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha", body: "LOADED BODY");
        Directory.CreateDirectory(Path.Combine(folder, "references"));
        File.WriteAllText(Path.Combine(folder, "references", "notes.md"), "A note.");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.LoadSkill, "{\"name\":\"alpha\"}", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("LOADED BODY", result.Content, StringComparison.Ordinal);
        Assert.Contains("references/notes.md", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSkillFile_ReadsAFileInsideTheSkill()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        Directory.CreateDirectory(Path.Combine(folder, "references"));
        File.WriteAllText(Path.Combine(folder, "references", "notes.md"), "RESOURCE CONTENT");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.ReadSkillFile,
            "{\"name\":\"alpha\",\"path\":\"references/notes.md\"}", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("RESOURCE CONTENT", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSkillFile_RefusesAPathOutsideTheSkill()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha");
        File.WriteAllText(Path.Combine(temp.Personal, "outside.txt"), "SECRET");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.ReadSkillFile,
            "{\"name\":\"alpha\",\"path\":\"../outside.txt\"}", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("outside", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSkillFile_RefusesABinaryFile()
    {
        using var temp = new SkillTestDirectory();
        var folder = temp.WriteSkill(temp.Personal, "alpha");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllBytes(Path.Combine(folder, "assets", "image.bin"), new byte[] { 0x41, 0x00, 0x42 });
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.ReadSkillFile,
            "{\"name\":\"alpha\",\"path\":\"assets/image.bin\"}", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("binary", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSkill_IsAnsweredWithWhatIsAvailable()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.LoadSkill, "{\"name\":\"beta\"}", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("No skill named", result.Content, StringComparison.Ordinal);
        Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedArguments_FailAsAResultSoTheCallCanStillBeAnswered()
    {
        using var temp = new SkillTestDirectory();
        temp.WriteSkill(temp.Personal, "alpha");
        var snapshot = SkillTestDirectory.Snapshot(temp.Options());

        var result = SkillTools.Execute(snapshot, SkillTools.LoadSkill, "{ this is not json", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public void Handles_ClaimsOnlyTheTwoSkillTools()
    {
        Assert.True(SkillTools.Handles(SkillTools.LoadSkill));
        Assert.True(SkillTools.Handles(SkillTools.ReadSkillFile));
        Assert.False(SkillTools.Handles("read_file"));
        Assert.Equal(2, SkillTools.Definitions.Count);
    }
}
