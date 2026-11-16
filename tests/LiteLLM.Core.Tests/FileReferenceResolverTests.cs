using System;
using System.Collections.Generic;
using System.IO;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>A reply names a file, not a path: "StoreGroupService.cs" for one three folders down. Resolving that
/// name against the solution root alone is what made a drawn link fail when clicked, so these tests are about the
/// name reaching the file rather than about the parser recognising the name.</summary>
public sealed class FileReferenceResolverTests
{
    [Fact]
    public void FindsABareNameThatSitsInASubfolder()
    {
        var root = CreateTree("src/StoreGrouper/Services/StoreGroupService.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            Assert.Equal(Full(root, "src/StoreGrouper/Services/StoreGroupService.cs"), resolver.Find("StoreGroupService.cs"));
            Assert.True(resolver.Exists("StoreGroupService.cs"));
            Assert.Equal(1, resolver.Count);
        }
        finally { Remove(root); }
    }

    [Fact]
    public void FindsAPathWrittenFromPartWayDown()
    {
        var root = CreateTree("src/StoreGrouper/Services/StoreGroupService.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            var full = Full(root, "src/StoreGrouper/Services/StoreGroupService.cs");
            Assert.Equal(full, resolver.Find(@"Services\StoreGroupService.cs"));
            Assert.Equal(full, resolver.Find("Services/StoreGroupService.cs"));
            Assert.Equal(full, resolver.Find("src/StoreGrouper/Services/StoreGroupService.cs"));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void FindsAnAbsolutePathThatExists()
    {
        var root = CreateTree("src/App/Program.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            var full = Full(root, "src/App/Program.cs");
            Assert.Equal(full, resolver.Find(full));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void AnAbsolutePathThatDoesNotExistIsNeverSubstituted()
    {
        var root = CreateTree("src/StoreGrouper/Services/StoreGroupService.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            // The model named one place; a file of the same name elsewhere is a different file, and opening it
            // would be worse than saying the name is not there.
            var missing = Path.Combine(Path.GetTempPath(), "litellm-nowhere-" + Guid.NewGuid().ToString("N"), "StoreGroupService.cs");
            Assert.Null(resolver.Find(missing));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void AnUnknownNameIsNotFound()
    {
        var root = CreateTree("src/App/Program.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            Assert.Null(resolver.Find("Nowhere.cs"));
            Assert.False(resolver.Exists("Nowhere.cs"));
            Assert.Null(resolver.Find(""));
            Assert.Null(resolver.Find(null));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void TwoFilesOfTheSameNameResolveToTheShorterPath()
    {
        var root = CreateTree("src/Deep/Nested/Foo.cs", "docs/Foo.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            Assert.Equal(Full(root, "docs/Foo.cs"), resolver.Find("Foo.cs"));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void TwoFilesAtTheSameDepthResolveDeterministically()
    {
        var root = CreateTree("src/B/Foo.cs", "src/A/Foo.cs");
        try
        {
            // Neither is nearer, so the tie is broken by name: two runs of the same reply must agree, whatever
            // order the folders happened to be read in.
            Assert.Equal(Full(root, "src/A/Foo.cs"), Build(new Workspace(root)).Find("Foo.cs"));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void AFileInAnAddedProjectIsFoundByItsBareName()
    {
        var solution = CreateTree("src/App/Program.cs");
        var added = CreateTree("src/OrderService.cs");
        try
        {
            var workspace = new Workspace(solution, new[] { added });
            var resolver = Build(workspace);
            var full = Full(added, "src/OrderService.cs");
            Assert.Equal(full, resolver.Find("OrderService.cs"));
            // ...and by the label the chat shows, so the two ways of naming one file agree.
            Assert.Equal(full, resolver.Find(workspace.Relative(full)));
        }
        finally { Remove(solution); Remove(added); }
    }

    [Fact]
    public void TheParserLinksTheNameRatherThanTheWordsBeforeIt()
    {
        var root = CreateTree("src/StoreGrouper/Services/StoreGroupService.cs", "src/App/My Handler.cs");
        try
        {
            var resolver = Build(new Workspace(root));
            const string text = "the check in StoreGroupService.cs:1896 ran twice";

            var match = Assert.Single(FileReferenceParser.Find(text, resolver.Exists));
            Assert.Equal("StoreGroupService.cs", match.Reference.Path);
            Assert.Equal(1896, match.Reference.Line);
            // The link covers the name, so the sentence in front of it stays a sentence.
            Assert.Equal("StoreGroupService.cs:1896", text.Substring(match.Index, match.Length));

            const string spaced = "see `My Handler.cs` first";
            var other = Assert.Single(FileReferenceParser.Find(spaced, resolver.Exists));
            Assert.Equal("My Handler.cs", other.Reference.Path);
            Assert.Equal("My Handler.cs", spaced.Substring(other.Index, other.Length));
        }
        finally { Remove(root); }
    }

    private static string CreateTree(params string[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "litellm-refs-" + Guid.NewGuid().ToString("N"));
        foreach (var file in files)
        {
            var path = Full(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// test");
        }
        return root;
    }

    private static string Full(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The list the chat reads: every file of every root, named the way the model sees it.</summary>
    private static FileReferenceResolver Build(Workspace workspace)
    {
        var names = new List<string>();
        foreach (var root in workspace.Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                names.Add(workspace.Relative(path));
        }
        return new FileReferenceResolver(workspace, names);
    }

    private static void Remove(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
