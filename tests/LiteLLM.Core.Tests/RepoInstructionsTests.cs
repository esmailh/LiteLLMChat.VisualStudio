using System;
using System.IO;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Reading the project's own instructions. These are real files, so the tests write real files: the part
/// worth checking is that each place a project might have put them is actually looked in, and that a project which
/// says nothing — or says too much — costs the request the right amount.</summary>
public sealed class RepoInstructionsTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "litellm-instructions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string name, string body)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }

    [Fact]
    public void RepoInstructions_ReadsAgentsMd()
    {
        var root = NewRoot();
        try
        {
            Write(root, "AGENTS.md", "Build with MSBuild, never with dotnet build.");
            var block = RepoInstructions.Load(root);

            Assert.Contains("Build with MSBuild, never with dotnet build.", block, StringComparison.Ordinal);
            Assert.Contains("AGENTS.md", block, StringComparison.Ordinal);   // the model can name the file it was given
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_ReadsClaudeMd()
    {
        var root = NewRoot();
        try
        {
            Write(root, "CLAUDE.md", "The test project references Core and nothing else.");
            Assert.Contains("references Core and nothing else", RepoInstructions.Load(root), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_ReadsCopilotInstructionsFromTheGithubFolder()
    {
        var root = NewRoot();
        try
        {
            Write(root, ".github/copilot-instructions.md", "Prefer the smallest change that works.");
            var block = RepoInstructions.Load(root);

            Assert.Contains("Prefer the smallest change that works.", block, StringComparison.Ordinal);
            Assert.Contains(".github/copilot-instructions.md", block, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_KeepsAgentsMdFirstWhenMoreThanOneIsPresent()
    {
        var root = NewRoot();
        try
        {
            Write(root, "AGENTS.md", "AGENTS-MARKER");
            Write(root, "CLAUDE.md", "CLAUDE-MARKER");
            var block = RepoInstructions.Load(root);

            Assert.Contains("AGENTS-MARKER", block, StringComparison.Ordinal);
            Assert.Contains("CLAUDE-MARKER", block, StringComparison.Ordinal);
            Assert.True(block.IndexOf("AGENTS-MARKER", StringComparison.Ordinal) < block.IndexOf("CLAUDE-MARKER", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_ReturnsNothingForAProjectWithNoInstructions()
    {
        var root = NewRoot();
        try { Assert.Equal("", RepoInstructions.Load(root)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_ReturnsNothingWithoutARoot()
    {
        // No solution open. The request has to be exactly what it was before this existed.
        Assert.Equal("", RepoInstructions.Load(null));
    }

    [Fact]
    public void RepoInstructions_CapsAnOversizedBlockAndMarksWhereItWasCut()
    {
        var root = NewRoot();
        try
        {
            Write(root, "AGENTS.md", new string('x', RepoInstructions.MaxChars + 4000));
            var block = RepoInstructions.Load(root);

            Assert.Contains(ChatStateLimits.TruncationMarker, block, StringComparison.Ordinal);
            Assert.True(block.Length <= RepoInstructions.MaxChars + ChatStateLimits.TruncationMarker.Length,
                "the block was " + block.Length + " characters long");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_KeepsTheFilesItCanReadWhenAnotherIsAbsent()
    {
        var root = NewRoot();
        try
        {
            Write(root, "CLAUDE.md", "CLAUDE-MARKER");
            var block = RepoInstructions.Load(root);

            Assert.Contains("CLAUDE-MARKER", block, StringComparison.Ordinal);
            Assert.DoesNotContain("AGENTS.md", block, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_ReadsNothingFromSomethingThatIsNotAFile()
    {
        // What the reader must do for anything it cannot open: answer null, so the request loses a section rather
        // than the turn failing. A directory is the readable stand-in for a locked or unreadable file.
        var root = NewRoot();
        try
        {
            Assert.Null(RepoInstructions.Read(root));
            Assert.Null(RepoInstructions.Read(Path.Combine(root, "nothing-here.md")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RepoInstructions_IgnoresAnEmptyFile()
    {
        var root = NewRoot();
        try
        {
            Write(root, "AGENTS.md", "   \n\n  ");
            Assert.Equal("", RepoInstructions.Load(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
