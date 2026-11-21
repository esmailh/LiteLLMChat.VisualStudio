using System;
using System.IO;
using System.Text;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>A shell's log is the only report a background program gives, so how it is read is the feature. The
/// two shapes are driven here: the end of the file, which is what says a server is ready, and a named line, which
/// is what makes the rest of the log reachable.</summary>
public sealed class ShellLogTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litellm-shelllog-" + Guid.NewGuid().ToString("N"));

    public ShellLogTests() => Directory.CreateDirectory(_root);

    private string Write(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ReadingFromALineReturnsThatLineAndSaysMoreFollows()
    {
        var path = Write("from-start.log", "first\nsecond\nthird\n");
        var read = ShellLog.Read(path, 2, 1, true);

        Assert.True(read.Found);
        Assert.Equal("second", read.Text);
        Assert.Equal(2, read.FirstLine);
        Assert.Equal(2, read.LastLine);
        Assert.True(read.More);
        Assert.True(read.Earlier);
    }

    [Fact]
    public void ReadingFromTheEndReturnsTheLastLinesAndSaysEarlierOnesWereSkipped()
    {
        var path = Write("tail.log", "one\ntwo\nthree\nfour\nfive\n");
        var read = ShellLog.Read(path, 1, 2, false);

        Assert.Equal("four\nfive", read.Text);
        Assert.Equal(2, read.Count);
        Assert.True(read.Earlier);
        Assert.False(read.More);
    }

    [Fact]
    public void ASmallLogIsAllOfItAndNothingIsClaimedToBeSkipped()
    {
        var path = Write("small.log", "only\nthese\n");
        var read = ShellLog.Read(path, 1, 20, false);

        Assert.Equal("only\nthese", read.Text);
        Assert.False(read.Earlier);
    }

    [Fact]
    public void AnEmptyLogIsFoundAndEmptyRatherThanMissing()
    {
        var path = Write("empty.log", "");
        var read = ShellLog.Read(path, 1, 20, false);

        Assert.True(read.Found);
        Assert.Equal(0, read.Count);
        Assert.Equal(0, read.Length);
    }

    [Fact]
    public void AMissingLogIsReportedAsNotFound()
    {
        var read = ShellLog.Read(Path.Combine(_root, "never-written.log"), 1, 20, false);
        Assert.False(read.Found);
    }

    /// <summary>PowerShell's own redirection writes UTF-16, so a log read as UTF-8 would be the program's output
    /// with a zero between every character. The encoding is taken from the file rather than assumed.</summary>
    [Fact]
    public void AUtf16LogFromPowerShellIsReadAsText()
    {
        var path = Write("utf16.log", "Now listening on: http://localhost:5127\r\n", Encoding.Unicode);
        var read = ShellLog.Read(path, 1, 20, false);

        Assert.Contains("Now listening on: http://localhost:5127", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", read.Text, StringComparison.Ordinal);
    }

    /// <summary>A tail read starts in the middle of the file, so it never sees a byte-order mark. A UTF-16 log read
    /// from the end still has to come back as text.</summary>
    [Fact]
    public void TheEndOfAUtf16LogIsReadWithoutItsByteOrderMark()
    {
        var text = new StringBuilder();
        for (var line = 1; line <= 400; line++) text.Append("line-").Append(line).Append("\r\n");
        var path = Write("utf16-long.log", text.ToString(), Encoding.Unicode);

        var read = ShellLog.Read(path, 1, 3, false);

        Assert.Equal("line-398\nline-399\nline-400", read.Text);
    }

    [Fact]
    public void ALineLongerThanTheLimitIsClippedAndSaysSo()
    {
        var path = Write("long.log", new string('x', 5000));
        var read = ShellLog.Read(path, 1, 1, false, maxChars: 200);

        Assert.Contains("more characters", read.Text, StringComparison.Ordinal);
        Assert.True(read.Text.Length < 400, "text was " + read.Text.Length + " characters");
    }

    [Fact]
    public void ALogAnotherProcessHasOpenForWritingCanStillBeRead()
    {
        var path = Write("live.log", "first line\n");
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var read = ShellLog.Read(path, 1, 10, false);
            Assert.Contains("first line", read.Text, StringComparison.Ordinal);
            writer.Write(Encoding.UTF8.GetBytes("second line\n"), 0, 12);
        }
        Assert.Contains("second line", ShellLog.Read(path, 1, 10, false).Text, StringComparison.Ordinal);
    }
}
