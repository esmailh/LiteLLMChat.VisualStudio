using System;
using System.IO;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class SymbolIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litellm-symbols-" + Guid.NewGuid().ToString("N"));
    private string _path = "";

    public SymbolIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private SymbolIndex Build(string text)
    {
        _path = Path.Combine(_root, "Sample.cs");
        File.WriteAllText(_path, text);
        return SymbolIndex.Build(new[] { _path });
    }

    [Fact]
    public void Find_ReturnsTheDeclaredTypeAndItsColumn()
    {
        var index = Build("namespace Demo;\n\npublic sealed class Widget\n{\n}\n");

        var found = index.Find("Widget");
        Assert.NotNull(found);
        Assert.Equal("Widget", found!.Name);
        Assert.True(found.IsType);
        Assert.Equal(_path, found.Path);
        Assert.Equal(3, found.Line);
        // `public sealed class ` is twenty characters, so the name starts at column 21.
        Assert.Equal(21, found.Column);
    }

    [Fact]
    public void Find_ReturnsAMethodByItsOwner()
    {
        var index = Build("public class Widget\n{\n    public void Paint(int color)\n    {\n    }\n}\n");

        var found = index.Find("Widget.Paint");
        Assert.NotNull(found);
        Assert.Equal("Widget.Paint", found!.Symbol);
        Assert.False(found.IsType);
        Assert.Equal(3, found.Line);
        Assert.Equal(17, found.Column);
    }

    [Fact]
    public void Find_PrefersTheOwnerTheReplyNamedOverTheFirstMemberWithThatName()
    {
        var index = Build("public class First\n{\n    public void Run() { }\n}\n\npublic class Second\n{\n    public void Run() { }\n}\n");

        Assert.Equal(8, index.Find("Second.Run")!.Line);
        // With no owner, the first declaration is the one a bare name can stand for.
        Assert.Equal(3, index.Find("Run")!.Line);
    }

    [Fact]
    public void Find_KnowsANestedTypeAndBothSpellingsOfItsMembers()
    {
        var index = Build("public class Outer\n{\n    private class Inner\n    {\n        public void Work() { }\n    }\n}\n");

        Assert.Equal(3, index.Find("Outer.Inner")!.Line);
        Assert.Equal(5, index.Find("Outer.Inner.Work")!.Line);
        Assert.Equal(5, index.Find("Inner.Work")!.Line);
    }

    [Fact]
    public void Find_IgnoresWhatIsOnlyTextInACommentOrAString()
    {
        var index = Build("// class Ghost\n/* class Spirit\n   public void Haunt() { } */\npublic class Real\n{\n    private string text = \"class Fake { }\";\n}\n");

        Assert.Null(index.Find("Ghost"));
        Assert.Null(index.Find("Spirit"));
        Assert.Null(index.Find("Haunt"));
        Assert.Null(index.Find("Fake"));
        Assert.NotNull(index.Find("Real"));
    }

    [Fact]
    public void Find_StillSpansATypeWhoseBraceIsOnTheNextLine()
    {
        var index = Build("public class Split : Base\n{\n    public int Value { get; set; }\n}\n");

        Assert.Equal(1, index.Find("Split")!.Line);
        Assert.Equal(3, index.Find("Split.Value")!.Line);
    }

    [Fact]
    public void Find_HandlesAMemberWhoseTypeHasSpacesInItsArguments()
    {
        var index = Build("public class Holder\n{\n    public Dictionary<string, string> Map { get; } = new Dictionary<string, string>();\n}\n");

        Assert.Equal(3, index.Find("Holder.Map")!.Line);
    }

    [Fact]
    public void Find_DoesNotIndexALocalFunction()
    {
        var index = Build("public class Job\n{\n    public void Run()\n    {\n        void Local() { }\n    }\n}\n");

        Assert.Null(index.Find("Job.Local"));
        Assert.Null(index.Find("Local"));
        Assert.NotNull(index.Find("Job.Run"));
    }

    [Fact]
    public void Find_IndexesAPositionalRecordAsAType()
    {
        var index = Build("public record Point(int X, int Y);\n");

        var found = index.Find("Point");
        Assert.NotNull(found);
        Assert.True(found!.IsType);
        Assert.Equal(1, found.Line);
    }

    [Fact]
    public void Find_DoesNotLinkADottedNameWhoseOwnerItDoesNotDeclare()
    {
        // `Task.Run` in a reply is the framework's method, not this solution's Widget.Run.
        var index = Build("public class Widget\n{\n    public void Run() { }\n}\n");

        Assert.Null(index.Find("Task.Run"));
        Assert.NotNull(index.Find("Widget.Run"));
    }

    [Fact]
    public void Find_ReturnsNullForANameThisSolutionDoesNotDeclare()
    {
        var index = Build("public class Widget\n{\n}\n");

        Assert.Null(index.Find("Missing"));
        Assert.Null(index.Find(""));
        Assert.Null(index.Find(null));
    }

    [Fact]
    public void Build_ReadsEveryFileAndSkipsAPathThatIsNotThere()
    {
        var first = Path.Combine(_root, "First.cs");
        var second = Path.Combine(_root, "Second.cs");
        File.WriteAllText(first, "public class Alpha { }\n");
        File.WriteAllText(second, "public class Beta { }\n");

        var index = SymbolIndex.Build(new[] { first, second, Path.Combine(_root, "Gone.cs") });

        Assert.Equal(first, index.Find("Alpha")!.Path);
        Assert.Equal(second, index.Find("Beta")!.Path);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Find_ResolvesADuplicateNameToTheFirstPathInOrder()
    {
        var second = Path.Combine(_root, "Second.cs");
        var first = Path.Combine(_root, "First.cs");
        File.WriteAllText(second, "public class Shared { }\n");
        File.WriteAllText(first, "public class Shared { }\n");

        var index = SymbolIndex.Build(new[] { second, first });
        // Sorted by path, so the answer does not depend on the order the caller found the files in.
        Assert.Equal(first, index.Find("Shared")!.Path);
    }
}
