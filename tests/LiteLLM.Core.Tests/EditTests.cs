using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The logic that decides whether a model-authored edit is safe to apply. Every case here is one that
/// actually happens: a hunk that no longer matches after the file moved on, an oldString that appears twice, and
/// a model writing "\n" against a CRLF file.</summary>
public sealed class EditTests
{
    [Fact]
    public void Patch_ParsesAddedUpdatedAndDeletedFiles()
    {
        var files = PatchApply.Parse(@"*** Begin Patch
*** Add File: src/New.cs
+one
+two
*** Update File: src/Old.cs
@@
 keep
-gone
+kept
*** Delete File: src/Dead.cs
*** End Patch");

        Assert.Equal(3, files.Count);
        Assert.True(files[0].IsAdd); Assert.Equal("src/New.cs", files[0].Path); Assert.Equal("one\ntwo\n", files[0].NewContent);
        Assert.True(files[1].IsUpdate); Assert.Equal("src/Old.cs", files[1].Path);
        Assert.Equal(new[] { "keep", "gone" }, files[1].Hunks[0].Before);
        Assert.Equal(new[] { "keep", "kept" }, files[1].Hunks[0].After);
        Assert.True(files[2].Delete);
    }

    [Fact]
    public void Patch_AppliesAContextHunk()
    {
        var file = PatchApply.Parse("*** Begin Patch\n*** Update File: f.cs\n@@\n context before\n-old line\n+new line\n context after\n*** End Patch")[0];
        Assert.Equal("context before\nnew line\ncontext after\n", PatchApply.Apply(file, "context before\nold line\ncontext after\n"));
    }

    [Fact]
    public void Patch_HunksApplyInOrderSoTheSecondOneCannotReuseTheFirstOneSLocation()
    {
        var file = PatchApply.Parse(@"*** Begin Patch
*** Update File: f.cs
@@
 header
-value
+first
@@
 header
-value
+second
*** End Patch")[0];
        // Both hunks target an identical block; only the moving index keeps them apart.
        Assert.Equal("header\nfirst\nheader\nsecond\n", PatchApply.Apply(file, "header\nvalue\nheader\nvalue\n"));
    }

    [Fact]
    public void Patch_EndOfFileAnchorsTheHunkToTheEnd()
    {
        // Note "+c", not "+ c": everything after the prefix is the line, including any leading space.
        var file = PatchApply.Parse("*** Begin Patch\n*** Update File: f.cs\n@@\n a\n b\n+c\n*** End of File\n*** End Patch")[0];
        Assert.Equal("a\nb\nc\n", PatchApply.Apply(file, "a\nb\n"));
    }

    [Fact]
    public void Patch_RefusesAHunkThatDoesNotMatchAndNamesTheFile()
    {
        var file = PatchApply.Parse("*** Begin Patch\n*** Update File: src/Foo.cs\n@@\n not there\n*** End Patch")[0];
        var failure = Assert.Throws<EditFailure>(() => PatchApply.Apply(file, "something else\n"));
        Assert.Contains("src/Foo.cs", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_RefusesAPatchWithNoBeginMarker()
    {
        var failure = Assert.Throws<EditFailure>(() => PatchApply.Parse("*** Update File: f.cs\n@@\n a\n*** End Patch"));
        Assert.Contains("Begin Patch", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_ReadsAMoveTarget()
    {
        var file = PatchApply.Parse("*** Begin Patch\n*** Update File: old.cs\n*** Move to: new.cs\n@@\n-a\n+b\n*** End Patch")[0];
        Assert.Equal("new.cs", file.MoveTo);
    }

    [Fact]
    public void Replace_RequiresExactlyOneMatch()
    {
        Assert.Equal("x B y", StringReplacer.ReplaceOnce("x A y", "A", "B", "f.cs"));

        Assert.Contains("No exact match", Assert.Throws<EditFailure>(() => StringReplacer.ReplaceOnce("zzz", "A", "B", "f.cs")).Message);
        Assert.Contains("2 matches", Assert.Throws<EditFailure>(() => StringReplacer.ReplaceOnce("A A", "A", "B", "f.cs")).Message);
    }

    [Fact]
    public void Replace_RetriesWithAlignedLineEndingsWhenTheModelWroteLfForACrlfFile()
    {
        var text = "one\r\ntwo\r\nthree\r\n";
        Assert.Equal("one\r\nTWO\r\nTHREE\r\n", StringReplacer.ReplaceOnce(text, "two\nthree", "TWO\nTHREE", "f.cs"));
    }

    [Fact]
    public void PathGuard_ConfinesEveryPathToTheSolutionRoot()
    {
        var root = @"C:\work\sol";
        Assert.Equal(@"C:\work\sol\src\Foo.cs", PathGuard.Resolve(root, "src/Foo.cs"));
        Assert.Equal(@"C:\work\sol\src\Foo.cs", PathGuard.Resolve(root, @"C:\work\sol\src\Foo.cs"));

        Assert.Throws<ArgumentException>(() => PathGuard.Resolve(root, @"..\..\elsewhere\Foo.cs"));
        Assert.Throws<ArgumentException>(() => PathGuard.Resolve(root, @"C:\other\Foo.cs"));

        // A whole-segment comparison, so a sibling folder with a longer name is not mistaken for a child.
        Assert.False(PathGuard.IsUnder(@"C:\work\src", @"C:\work\src2\Foo.cs"));
        Assert.True(PathGuard.IsUnder(@"C:\work\src", @"C:\work\src\Foo.cs"));
        Assert.Equal("src/Foo.cs", PathGuard.Relative(root, @"C:\work\sol\src\Foo.cs").Replace('\\', '/'));
    }

    [Fact]
    public void TextFile_KeepsTheBomTheLineEndingsAndTheEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs");
        try
        {
            var bom = new UTF8Encoding(true);
            File.WriteAllBytes(path, bom.GetPreamble().Concat(bom.GetBytes("one\r\ntwo\r\n")).ToArray());

            var file = TextFile.Read(path);
            Assert.True(file.Preamble); Assert.Equal("\r\n", file.NewLine); Assert.Equal("one\r\ntwo\r\n", file.Text);

            // The model writes "\n"; the file keeps its own convention and its byte-order mark.
            TextFile.Write(path, file, file.Conform("one\nTWO\n"));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
            Assert.Equal("one\r\nTWO\r\n", TextFile.Read(path).Text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AgentPrompt_AddsTheSystemMessageAndNeverRewritesTheStoredMessage()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "remove the ActorId filter" } };
        var plain = AgentPrompt.Build(history, null, true, true);
        Assert.Equal("system", plain[0].Role); Assert.Equal(2, plain.Count); Assert.Equal("remove the ActorId filter", plain[1].Content);

        var attached = AgentPrompt.Build(history, new[] { new ChatContextItem { Kind = "file", Label = "src/Foo.cs", Preview = @"C:\s\src\Foo.cs" } }, true, true);
        Assert.Contains("src/Foo.cs", attached[1].Content, StringComparison.Ordinal);
        Assert.Contains("read_file", attached[1].Content, StringComparison.Ordinal);
        Assert.Equal("remove the ActorId filter", history[0].Content);   // what the chat renders must not change
    }

    [Fact]
    public void AgentPrompt_AddsTheProjectsInstructionsAsTheirOwnLastSection()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "why does the build fail" } };
        var built = AgentPrompt.Build(history, null, "agent", true, true, false, "From AGENTS.md:\nUse MSBuild, not dotnet build.");

        Assert.Equal("system", built[0].Role);
        Assert.Contains("Repository instructions", built[0].Content, StringComparison.Ordinal);
        Assert.Contains("Use MSBuild, not dotnet build.", built[0].Content, StringComparison.Ordinal);
        // Appended after the extension's own rules rather than woven into them, so a project's file cannot be read
        // as though it were the part that decides which tools exist.
        Assert.True(built[0].Content.IndexOf("Repository instructions", StringComparison.Ordinal)
            > built[0].Content.IndexOf("Editing files", StringComparison.Ordinal));
        Assert.Equal("why does the build fail", built[1].Content);
    }

    [Fact]
    public void AgentPrompt_LeavesTheSystemMessageAloneWhenTheProjectHasNone()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hello" } };
        var built = AgentPrompt.Build(history, null, "agent", true, true, false, "");

        Assert.Equal(AgentPrompt.Instructions("agent", true, true, false), built[0].Content);
        Assert.DoesNotContain("Repository instructions", built[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentPrompt_AddsTheCurrentErrorsAfterEverythingElse()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "why does the build fail" } };
        var errors = BuildDiagnostics.ToStandingNote("error: CS0246: The type or namespace name 'Bar' could not be found — C:\\s\\Baz.cs:3");
        var built = AgentPrompt.Build(history, null, "agent", true, true, false, "From AGENTS.md:\nUse MSBuild.", errors);

        Assert.Contains("Current errors in the solution", built[0].Content, StringComparison.Ordinal);
        Assert.Contains("CS0246", built[0].Content, StringComparison.Ordinal);
        // Last section of the system message, and the transcript is still only what the user typed: the block is
        // current state, so it lives in the request and not in the conversation.
        Assert.True(built[0].Content.IndexOf("Current errors in the solution", StringComparison.Ordinal)
            > built[0].Content.IndexOf("Repository instructions", StringComparison.Ordinal));
        Assert.Equal(2, built.Count);
        Assert.Equal("why does the build fail", built[1].Content);
    }

    [Fact]
    public void AgentPrompt_LeavesTheSystemMessageAloneForACleanSolution()
    {
        var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hello" } };
        var built = AgentPrompt.Build(history, null, "agent", true, true, false, "", "");

        Assert.Equal(AgentPrompt.Instructions("agent", true, true, false), built[0].Content);
        Assert.DoesNotContain("Current errors", built[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentPrompt_OnlyPromisesEditingToolsWhenTheyAreActuallyOffered()
    {
        Assert.Contains("apply_patch", AgentPrompt.Instructions(true, true));
        Assert.DoesNotContain("apply_patch", AgentPrompt.Instructions(true, false));
        Assert.DoesNotContain("apply_patch", AgentPrompt.Instructions(false, false));
        Assert.Contains("no tool for changing files", AgentPrompt.Instructions(true, false));
    }

    [Fact]
    public void AgentPrompt_OnlyPromisesReadingToolsWhenTheyAreOffered()
    {
        Assert.Contains("read_file", AgentPrompt.Instructions(true, true));
        Assert.DoesNotContain("read_file", AgentPrompt.Instructions(false, false));
        Assert.DoesNotContain("list_files", AgentPrompt.Instructions(false, false));
        Assert.Contains("no tools in this conversation", AgentPrompt.Instructions(false, false));
    }

    [Fact]
    public void AgentPrompt_InlinesAnAttachedFileWhenNoToolCanReadIt()
    {
        var attached = new List<ChatContextItem> { new ChatContextItem { Label = "src/Foo.cs", Kind = "file", Preview = @"C:\s\src\Foo.cs" } };

        // A selection is already text and is passed through untouched, and the reader is never asked for it.
        var mixed = new List<ChatContextItem>(attached)
        {
            new ChatContextItem { Label = "Selection · 2 lines", Kind = "selection", Preview = "one\ntwo" }
        };
        var read = new List<string>();
        var passed = AgentPrompt.InlineFiles(mixed, path => { read.Add(path); return "class Foo { }\n"; });
        Assert.Equal("selection", passed[1].Kind);
        Assert.Equal(@"C:\s\src\Foo.cs", Assert.Single(read));

        var inline = AgentPrompt.InlineFiles(attached, _ => "class Foo { }\n");
        Assert.Equal("file-text", inline[0].Kind);
        Assert.Equal("src/Foo.cs", inline[0].Label);
        Assert.Contains("class Foo", inline[0].Preview, StringComparison.Ordinal);

        // A file that cannot be read keeps its path, which is then all the model can ask about.
        Assert.Equal("file", AgentPrompt.InlineFiles(attached, _ => null)[0].Kind);

        var built = AgentPrompt.Build(
            new List<ChatMessage> { new ChatMessage { Role = "user", Content = "what does this do?" } },
            inline, false, false);
        Assert.Contains("class Foo", built[1].Content, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", built[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TextChange_ReducesAnEditToTheOneLineThatMoved()
    {
        var change = TextChange.Between("a\nb\nc\n", "a\nB\nc\n");
        Assert.Equal(2, change.StartLine);
        Assert.Equal(new[] { "b" }, change.Removed);
        Assert.Equal(new[] { "B" }, change.Added);

        // A file whose lines all shifted still reports the one line that differs, because the lines after it match.
        var shifted = TextChange.Between("a\nb\nc\n", "a\nb\nc\nd\n");
        Assert.Equal(4, shifted.StartLine);
        Assert.Empty(shifted.Removed);
        Assert.Equal(new[] { "d" }, shifted.Added);

        var deleted = TextChange.Between("a\nb\nc\n", "a\nc\n");
        Assert.Equal(2, deleted.StartLine);
        Assert.Equal(new[] { "b" }, deleted.Removed);
        Assert.Empty(deleted.Added);

        Assert.True(TextChange.Between("a\nb\n", "a\nb\n").IsEmpty);
    }

    [Fact]
    public void TextChange_FindsTheEditInACrlfFileWrittenWithLf()
    {
        // The model writes "\n"; the file on disk is CRLF. The reduced change must still land on the right line.
        var change = TextChange.Between("one\r\ntwo\r\nthree\r\n", "one\r\nTWO\r\nthree\r\n");
        Assert.Equal(2, change.StartLine);
        Assert.Equal(new[] { "two" }, change.Removed);
        Assert.Equal(new[] { "TWO" }, change.Added);
    }

    [Fact]
    public void Diff_NamesTheLineTheEditLandsOn()
    {
        var text = "a\nb\nc\n";
        var diff = Diff.ForReplacement("src/Foo.cs", text, text.IndexOf("c", StringComparison.Ordinal), "c", "C");
        Assert.Contains("--- src/Foo.cs", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -3,1 +3,1 @@", diff, StringComparison.Ordinal);
        Assert.Contains("-c\n+C\n", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_DescribesAReducedRegion()
    {
        var diff = Diff.ForRegion("src/Foo.cs", TextChange.Between("a\nb\nc\n", "a\nc\n"));
        Assert.Contains("--- src/Foo.cs", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -2,1 +2,0 @@", diff, StringComparison.Ordinal);
        Assert.Contains("-b\n", diff, StringComparison.Ordinal);
        Assert.Equal("+3 −1", Diff.Summary(1, 3));
    }
}
