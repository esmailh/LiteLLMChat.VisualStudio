using System;
using System.IO;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Finding a solution's test projects and the assemblies they built. Real directories, because the
/// part worth checking is what is on disk: which project files count as test projects, and that the assembly
/// handed to the runner is the one the last build left behind rather than one named in the project file.</summary>
public sealed class TestProjectTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "litellm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Project(string root, string name, string body)
    {
        var path = Path.Combine(root, name + ".csproj");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>A built assembly where the SDK puts one: under bin, one folder per configuration and
    /// framework.</summary>
    private static string Built(string root, string name, string configuration, DateTime writtenUtc)
    {
        var directory = Path.Combine(root, "bin", configuration, "net48");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        File.WriteAllText(path, "not really an assembly");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private const string TestProjectFile =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <ItemGroup>\n" +
        "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />\n" +
        "    <PackageReference Include=\"xunit\" Version=\"2.9.2\" />\n" +
        "  </ItemGroup>\n" +
        "</Project>\n";

    [Fact]
    public void TestProject_RecognisesAProjectThatReferencesATestFramework()
    {
        Assert.True(TestProject.IsTestProject(TestProjectFile));
        Assert.True(TestProject.IsTestProject("<PackageReference Include=\"NUnit\" Version=\"4.0.0\" />"));
        Assert.True(TestProject.IsTestProject("<PackageReference Include=\"MSTest.TestAdapter\" Version=\"3.0.0\" />"));
    }

    [Fact]
    public void TestProject_LeavesAnOrdinaryLibraryAlone()
    {
        // The name is not evidence: a library called Foo.Tests that references no framework cannot be run,
        // and running it would report a failure to verify that is really a project this check never wanted.
        Assert.False(TestProject.IsTestProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Foo.Tests</AssemblyName></PropertyGroup></Project>"));
        Assert.False(TestProject.IsTestProject(""));
        Assert.False(TestProject.IsTestProject(null));
    }

    [Fact]
    public void TestProject_FindsTheAssemblyTheLastBuildLeftBehind()
    {
        var root = NewRoot();
        try
        {
            var project = Project(root, "Foo.Tests", TestProjectFile);
            Built(root, "Foo.Tests", "Release", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newest = Built(root, "Foo.Tests", "Debug", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            // The configuration that matters is the one the build just used, which is the one it wrote last.
            Assert.Equal(newest, TestProject.FindBuiltAssembly(project, TestProjectFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TestProject_UsesTheAssemblyNameTheProjectAsksFor()
    {
        var root = NewRoot();
        try
        {
            var body = TestProjectFile.Replace("<Project Sdk=\"Microsoft.NET.Sdk\">", "<PropertyGroup><AssemblyName>Renamed</AssemblyName></PropertyGroup>");
            var project = Project(root, "Foo.Tests", body);
            Built(root, "Foo.Tests", "Debug", DateTime.UtcNow);

            // The project's own file is called Foo.Tests, and the assembly it builds is not.
            Assert.Null(TestProject.FindBuiltAssembly(project, body));
            Assert.NotNull(TestProject.FindBuiltAssembly(project, body.Replace("Renamed", "Foo.Tests")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TestProject_SaysNothingAboutAProjectThatWasNeverBuilt()
    {
        var root = NewRoot();
        try
        {
            var project = Project(root, "Foo.Tests", TestProjectFile);
            Assert.Null(TestProject.FindBuiltAssembly(project, TestProjectFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TestProject_SurvivesAProjectFileItCannotMakeSenseOf()
    {
        Assert.Null(TestProject.FindBuiltAssembly(null, TestProjectFile));
        Assert.Null(TestProject.FindBuiltAssembly("", TestProjectFile));
        Assert.Null(TestProject.FindBuiltAssembly("   ", TestProjectFile));
    }
}
