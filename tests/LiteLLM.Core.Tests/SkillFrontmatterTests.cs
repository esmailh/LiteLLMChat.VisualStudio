using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The header is the whole contract of a skill: a body whose name or description is missing cannot be
/// addressed, and a parser that guessed at a malformed field would offer a model instructions the user did not
/// write. These cases cover both the fields the format does define and the shapes it explicitly leaves open.</summary>
public sealed class SkillFrontmatterTests
{
    [Fact]
    public void Parse_ReadsTheDefinedFieldsAndKeepsTheBodySeparate()
    {
        var parsed = SkillFrontmatter.Parse(
            "---\n" +
            "name: code-review\n" +
            "description: Review a diff before it is committed.\n" +
            "license: MIT\n" +
            "compatibility: Visual Studio 2022\n" +
            "allowed-tools: read_file terminal\n" +
            "---\n\n" +
            "# Steps\n\nRead the diff.");

        Assert.True(parsed.IsValid);
        Assert.Equal("code-review", parsed.Name);
        Assert.Equal("Review a diff before it is committed.", parsed.Description);
        Assert.Equal("MIT", parsed.License);
        Assert.Equal("Visual Studio 2022", parsed.Compatibility);
        Assert.Equal("read_file terminal", parsed.AllowedTools);
        Assert.Contains("# Steps", parsed.Body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("---", parsed.Body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsTextThatDoesNotStartWithFrontmatter()
    {
        var parsed = SkillFrontmatter.Parse("# A heading\n\nSome prose.");

        Assert.False(parsed.IsValid);
        Assert.Contains("frontmatter", parsed.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsAnUnclosedBlock()
    {
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\ndescription: A skill.");

        Assert.False(parsed.IsValid);
        Assert.Contains("closed", parsed.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RequiresADescription()
    {
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\n---\nBody.");

        Assert.False(parsed.IsValid);
        Assert.Contains("description", parsed.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Code-Review")]
    [InlineData("code_review")]
    [InlineData("-review")]
    [InlineData("review-")]
    [InlineData("code--review")]
    [InlineData("../escape")]
    [InlineData("")]
    public void ValidateName_RefusesAnythingThatIsNotAFolderSafeSlug(string name)
    {
        Assert.NotNull(SkillFrontmatter.ValidateName(name));
    }

    [Theory]
    [InlineData("code-review")]
    [InlineData("a")]
    [InlineData("review2")]
    public void ValidateName_AcceptsLowercaseLettersDigitsAndSingleHyphens(string name)
    {
        Assert.Null(SkillFrontmatter.ValidateName(name));
    }

    [Fact]
    public void Parse_IgnoresFieldsItDoesNotKnow()
    {
        // The specification may grow; refusing an unknown field would make this build older than the format.
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\ndescription: A skill.\nfuture: value\n---\nBody.");

        Assert.True(parsed.IsValid);
        Assert.Equal("alpha", parsed.Name);
    }

    [Fact]
    public void Parse_RejectsAFieldWrittenTwice()
    {
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\ndescription: One.\ndescription: Two.\n---\nBody.");

        Assert.False(parsed.IsValid);
        Assert.Contains("more than once", parsed.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnescapesAQuotedValue()
    {
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\ndescription: \"Line one\\nline two\"\n---\nBody.");

        Assert.True(parsed.IsValid);
        Assert.Equal("Line one\nline two", parsed.Description);
    }

    [Fact]
    public void Parse_ReadsAMetadataBlockWithoutSwallowingTheNextField()
    {
        var parsed = SkillFrontmatter.Parse(
            "---\nname: alpha\ndescription: A skill.\nmetadata:\n  owner: team\n  version: 2\nlicense: MIT\n---\nBody.");

        Assert.True(parsed.IsValid);
        Assert.Equal("team", parsed.Metadata["owner"]);
        Assert.Equal("2", parsed.Metadata["version"]);
        Assert.Equal("MIT", parsed.License);
    }

    [Fact]
    public void Parse_ReadsALiteralBlockScalar()
    {
        var parsed = SkillFrontmatter.Parse("---\nname: alpha\ndescription: |\n  First line.\n  Second line.\n---\nBody.");

        Assert.True(parsed.IsValid);
        Assert.Equal("First line.\nSecond line.", parsed.Description);
    }

    [Fact]
    public void Build_RoundTripsCharactersThatWouldOtherwiseChangeTheDocument()
    {
        var content = SkillFrontmatter.Build("alpha", "Use when the diff has: colons, # hashes and \"quotes\".",
            "Body with a # that is not a comment.");

        var parsed = SkillFrontmatter.Parse(content);

        Assert.True(parsed.IsValid);
        Assert.Equal("Use when the diff has: colons, # hashes and \"quotes\".", parsed.Description);
        Assert.Equal("Body with a # that is not a comment.", parsed.Body.Trim());
    }

    [Fact]
    public void Extract_TakesTheNameTheUserTypedAndKeepsTheModelsBody()
    {
        var draft = SkillGeneration.Extract(
            "```\n---\nname: something-else\ndescription: The model's description.\n---\n\nModel body.\n```",
            "code-review", "The user's description.");

        Assert.True(draft.IsValid);
        var parsed = SkillFrontmatter.Parse(draft.Content);
        Assert.Equal("code-review", parsed.Name);
        Assert.Equal("The model's description.", parsed.Description);
        Assert.Contains("Model body.", parsed.Body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_WrapsABodyOnlyAnswerInFrontmatter()
    {
        var draft = SkillGeneration.Extract("Read the diff, then report the risky hunks.", "code-review", "Review a diff.");

        Assert.True(draft.IsValid);
        var parsed = SkillFrontmatter.Parse(draft.Content);
        Assert.Equal("code-review", parsed.Name);
        Assert.Equal("Review a diff.", parsed.Description);
        Assert.Contains("report the risky hunks", parsed.Body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_KeepsAMalformedAnswerAndSaysWhatIsWrong()
    {
        var draft = SkillGeneration.Extract("just some prose without frontmatter?", "Bad Name", "A skill.");

        Assert.False(draft.IsValid);
        // The prose is still there to repair — the model's answer is not thrown away for a bad name.
        Assert.Contains("just some prose without frontmatter?", draft.Content, System.StringComparison.Ordinal);
        Assert.NotNull(draft.Error);
    }

    [Fact]
    public void Request_AsksForImprovementOnlyWhenExistingContentIsGiven()
    {
        var fresh = SkillGeneration.Request("alpha", "A skill.");
        var improve = SkillGeneration.Request("alpha", "A skill.", existingContent: "---\nname: alpha\ndescription: A skill.\n---\nOld body.");

        Assert.Contains("SKILL.md", fresh[0].Content, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Improve the existing skill", fresh[0].Content, System.StringComparison.Ordinal);
        Assert.Contains("Improve the existing skill", improve[0].Content, System.StringComparison.Ordinal);
        Assert.Contains("Old body.", improve[1].Content, System.StringComparison.Ordinal);
        // No provider or model is named in the prompt the extension builds; the caller supplies the client.
        var text = string.Join("\n", fresh.Select(message => message.Content));
        Assert.DoesNotContain("openai", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anthropic", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpt-", text, System.StringComparison.OrdinalIgnoreCase);
    }
}
