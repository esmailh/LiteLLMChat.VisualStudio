using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Which folders a conversation may read and change, and how a path names one of them.
///
/// The rules worth pinning are the two that turn a single root into several without changing the old behavior:
/// an unprefixed path is still the solution (so every stored path, every prompt example and every row written
/// before this existed keeps its meaning), and a path that opens with an added project's name reaches that
/// project unless the solution already has that path. The rest is about not granting more than the user asked
/// for, and about saying why a folder cannot be added rather than accepting it and confusing them later.</summary>
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp;
    private readonly string _solution;
    private readonly string _app;

    public WorkspaceTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "litellm-workspace-" + Guid.NewGuid().ToString("N"));
        _solution = Path.Combine(_temp, "Solution");
        _app = Path.Combine(_temp, "App");
        Directory.CreateDirectory(Path.Combine(_solution, "src"));
        Directory.CreateDirectory(Path.Combine(_app, "src"));
        File.WriteAllText(Path.Combine(_solution, "src", "Foo.cs"), "solution");
        File.WriteAllText(Path.Combine(_app, "src", "Foo.cs"), "app");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (Exception) { }
    }

    [Fact]
    public void AWorkspaceWithNoAddedProjectBehavesExactlyLikeASingleRoot()
    {
        var workspace = new Workspace(_solution);

        Assert.Equal(new[] { Path.GetFullPath(_solution) }, workspace.Roots);
        Assert.Equal(Path.GetFullPath(_solution), workspace.Primary);
        Assert.False(workspace.HasExtra);
        // Nothing is said about projects when there is only the solution: the prompt has to stay byte-for-byte
        // what it was before this existed.
        Assert.Equal("", workspace.Describe());

        var resolved = workspace.Resolve("src/Foo.cs");
        Assert.Equal(Path.Combine(Path.GetFullPath(_solution), "src", "Foo.cs"), resolved);
        // Solution paths keep the form PathGuard has always produced for them; the chat normalizes the separators
        // where it needs to, exactly as it did before an added project could exist.
        Assert.Equal("src" + Path.DirectorySeparatorChar + "Foo.cs", workspace.Relative(resolved));
    }

    [Fact]
    public void AnAddedProjectIsReachedByItsOwnNameAndAddressedTheSameWayBack()
    {
        var workspace = new Workspace(_solution, new[] { _app });

        Assert.True(workspace.HasExtra);
        Assert.Equal(Path.GetFullPath(_solution), workspace.Primary);

        var resolved = workspace.Resolve("App/src/Foo.cs");
        Assert.Equal(Path.Combine(Path.GetFullPath(_app), "src", "Foo.cs"), resolved);
        // The label the model receives is the label it sent: a path has to survive the round trip, or a row it
        // wrote and a call it makes would name two different files.
        Assert.Equal("App/src/Foo.cs", workspace.Relative(resolved).Replace('\\', '/'));
        Assert.Equal("solution", File.ReadAllText(workspace.Resolve("src/Foo.cs")));
        Assert.Equal("app", File.ReadAllText(resolved));
        // An absolute path inside an added project is the same file, and resolves to itself.
        Assert.Equal(resolved, workspace.Resolve(resolved));
    }

    [Fact]
    public void TheSolutionWinsAPathItAlreadyHas()
    {
        // A folder of the solution's own that shares an added project's name must stay reachable, and a stored
        // path written before the project was added must keep pointing at the file it always meant.
        Directory.CreateDirectory(Path.Combine(_solution, "App", "src"));
        File.WriteAllText(Path.Combine(_solution, "App", "src", "Foo.cs"), "inside the solution");
        var workspace = new Workspace(_solution, new[] { _app });

        var resolved = workspace.Resolve("App/src/Foo.cs");
        Assert.Equal(Path.Combine(Path.GetFullPath(_solution), "App", "src", "Foo.cs"), resolved);

        // The added project is still reachable, by the path that is unambiguous.
        Assert.Equal("app", File.ReadAllText(workspace.Resolve(Path.Combine(_app, "src", "Foo.cs"))));
    }

    [Fact]
    public void AnAbsolutePathIsConfinedToTheRootThatOwnsIt()
    {
        var workspace = new Workspace(_solution, new[] { _app });
        var outside = Path.Combine(_temp, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Secret.cs"), "not yours");

        Assert.Throws<ArgumentException>(() => workspace.Resolve(Path.Combine(outside, "Secret.cs")));
        Assert.Equal(Path.Combine(Path.GetFullPath(_app), "src", "Foo.cs"),
            workspace.Resolve(Path.Combine(Path.GetFullPath(_app), "src", "Foo.cs")));
    }

    [Fact]
    public void TheLongestRootOwnsAPathSoAProjectInsideAnotherKeepsItsOwnFiles()
    {
        var inner = Path.Combine(_app, "inner");
        Directory.CreateDirectory(inner);
        var workspace = new Workspace(_solution, new[] { _app, inner });

        Assert.Equal(Path.GetFullPath(inner), workspace.Owner(Path.Combine(inner, "Program.cs")));
        Assert.Equal("inner/Program.cs", workspace.Relative(Path.Combine(inner, "Program.cs")).Replace('\\', '/'));
    }

    [Fact]
    public void TwoProjectsWithTheSameFolderNameAreNumberedRatherThanShadowed()
    {
        var second = Path.Combine(_temp, "Copy", "App");
        Directory.CreateDirectory(second);
        var workspace = new Workspace(_solution, new[] { _app, second });

        Assert.Equal("App", workspace.LabelOf(_app));
        Assert.Equal("App (2)", workspace.LabelOf(second));
        Assert.Equal(Path.GetFullPath(second), workspace.Resolve("App (2)"));
    }

    [Fact]
    public void WhyNotAddExplainsEveryFolderItRefuses()
    {
        var workspace = new Workspace(_solution, new[] { _app });
        var sibling = Path.Combine(_temp, "Sibling");
        Directory.CreateDirectory(sibling);

        Assert.Null(workspace.WhyNotAdd(sibling));
        Assert.Contains("already part", workspace.WhyNotAdd(_app)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already reachable", workspace.WhyNotAdd(Path.Combine(_app, "src"))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contains", workspace.WhyNotAdd(_temp)!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(workspace.WhyNotAdd("not a path"));
        Assert.NotNull(workspace.WhyNotAdd(Path.Combine(_temp, "does-not-exist")));
    }

    [Fact]
    public void NormalizeTakesTheFormsAUserActuallyPastes()
    {
        Assert.Equal(Path.GetFullPath(_app), Workspace.Normalize("\"" + _app + "\""));
        Assert.Equal(Path.GetFullPath(_app), Workspace.Normalize(_app + Path.DirectorySeparatorChar));
        Assert.Equal("", Workspace.Normalize("relative\\folder"));
        Assert.Equal("", Workspace.Normalize(null));
    }

    [Fact]
    public void AddedProjectsAreCappedAndDeduplicated()
    {
        var roots = Enumerable.Range(0, Workspace.MaxExtraRoots + 4)
            .Select(i => Path.Combine(_temp, "Project" + i))
            .ToList();
        var workspace = new Workspace(_solution, roots.Concat(new[] { roots[0] }));

        // The solution plus at most the cap, and no folder twice.
        Assert.Equal(1 + Workspace.MaxExtraRoots, workspace.Roots.Count);
        Assert.Equal(workspace.Roots.Count, workspace.Roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheCapCountsAddedProjectsEvenWithNoSolutionOpen()
    {
        // Without a solution the first root is itself an added project, so the cap has to count it — otherwise
        // a chat with nothing open would accept one folder more than its own refusal message promises.
        var roots = Enumerable.Range(0, Workspace.MaxExtraRoots + 2)
            .Select(i => Path.Combine(_temp, "NoSolution" + i))
            .ToList();
        foreach (var root in roots) Directory.CreateDirectory(root);
        var workspace = new Workspace(null, roots);

        Assert.Equal(Workspace.MaxExtraRoots, workspace.Roots.Count);
        Assert.Contains("at most", workspace.WhyNotAdd(roots[Workspace.MaxExtraRoots])!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionNamesTheProjectsAndHowAPathReachesThem()
    {
        var text = new Workspace(_solution, new[] { _app }).Describe();

        Assert.Contains("App", text, StringComparison.Ordinal);
        Assert.Contains("App/src/Foo.cs", text, StringComparison.Ordinal);
        Assert.Contains("solution", text, StringComparison.OrdinalIgnoreCase);
    }
}
