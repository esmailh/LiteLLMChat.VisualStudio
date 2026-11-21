using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class FileReferenceParserTests
{
    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs", null, null)]
    [InlineData("src/Foo.cs:42", "src/Foo.cs", 42, null)]
    [InlineData("src/Foo.cs:42:7", "src/Foo.cs", 42, 7)]
    [InlineData("src/Foo.cs#L42", "src/Foo.cs", 42, null)]
    [InlineData("src/Foo.cs#L42C7", "src/Foo.cs", 42, 7)]
    [InlineData(@"C:\work\Foo.cs:12:3", @"C:\work\Foo.cs", 12, 3)]
    // A folder of the user's own can contain a space — "New folder (3) - Copy" — and an absolute path that is cut
    // at that space names a file that does not exist.
    [InlineData(@"F:\New folder (3) - Copy\src\App\StoreGroupService.cs:1896", @"F:\New folder (3) - Copy\src\App\StoreGroupService.cs", 1896, null)]
    public void ParsesSupportedReferences(string text, string path, int? line, int? column)
    {
        var match = Assert.Single(FileReferenceParser.Find(text));
        Assert.Equal(path, match.Reference.Path);
        Assert.Equal(line, match.Reference.Line);
        Assert.Equal(column, match.Reference.Column);
    }

    [Theory]
    [InlineData("https://example.com/src/Foo.cs")]
    [InlineData("person@example.com")]
    [InlineData("ordinary prose without a file")]
    [InlineData("src/Foo.cs:0")]
    [InlineData("src/Foo.cs#L999999999999999999999")]
    public void RejectsUnsafeOrMalformedText(string text) => Assert.Empty(FileReferenceParser.Find(text));

    [Fact]
    public void UrlDoesNotSuppressSeparateReference()
    {
        var match = Assert.Single(FileReferenceParser.Find("https://example.com/a.cs and src/Foo.cs"));
        Assert.Equal("src/Foo.cs", match.Reference.Path);
    }

    [Fact]
    public void FindsMultipleReferencesAndPreservesRanges()
    {
        const string text = "See src/Foo.cs:2, then tests/FooTests.cs#L8C4.";
        var matches = FileReferenceParser.Find(text);
        Assert.Equal(2, matches.Count);
        Assert.Equal("src/Foo.cs:2", text.Substring(matches[0].Index, matches[0].Length));
        Assert.Equal("tests/FooTests.cs#L8C4", text.Substring(matches[1].Index, matches[1].Length));
    }

    [Fact]
    public void ParsesWholeMarkdownTarget()
    {
        FileReference reference;
        Assert.True(FileReferenceParser.TryParse("src/My File.cs#L5C2", out reference));
        Assert.Equal("src/My File.cs", reference.Path);
        Assert.Equal(5, reference.Line);
        Assert.Equal(2, reference.Column);
    }

    /// <summary>A name may contain a space, so a bare name in a sentence is matched with the word in front of it.
    /// When the disk is asked, the link covers the name the disk has rather than the words around it — the bug
    /// where "the check in Foo.cs:12" reported that the referenced file was not found.</summary>
    [Fact]
    public void DropsProseBeforeABareNameThatOnlyExistsWithoutIt()
    {
        const string text = "the check in StoreGroupService.cs:1896 returns";
        var match = Assert.Single(FileReferenceParser.Find(text, path => path == "StoreGroupService.cs"));
        Assert.Equal("StoreGroupService.cs", match.Reference.Path);
        Assert.Equal(1896, match.Reference.Line);
        Assert.Equal("StoreGroupService.cs:1896", text.Substring(match.Index, match.Length));
    }

    [Fact]
    public void KeepsASpacedFileNameThatIsTheOneOnDisk()
    {
        const string text = "see My File.cs:3 first";
        var match = Assert.Single(FileReferenceParser.Find(text, path => path == "My File.cs"));
        Assert.Equal("My File.cs", match.Reference.Path);
        Assert.Equal(3, match.Reference.Line);
        Assert.Equal("My File.cs:3", text.Substring(match.Index, match.Length));
    }

    [Fact]
    public void KeepsTheMatchAsWrittenWhenNoCandidateExists()
    {
        const string text = "see Missing.cs:4";
        var match = Assert.Single(FileReferenceParser.Find(text, _ => false));
        Assert.Equal("see Missing.cs", match.Reference.Path);
        Assert.Equal("see Missing.cs:4", text.Substring(match.Index, match.Length));
    }

    [Fact]
    public void DoesNotShortenAPathWithAFolder()
    {
        var match = Assert.Single(FileReferenceParser.Find("see src/Services/My File.cs:3", path => path == "My File.cs"));
        Assert.Equal("src/Services/My File.cs", match.Reference.Path);
    }
}
