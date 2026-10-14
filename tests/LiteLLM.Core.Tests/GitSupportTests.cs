using System;
using System.Collections.Generic;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class GitSupportTests
{
    public static IEnumerable<object[]> CommandLines()
    {
        yield return new object[] { new[] { "" } };
        yield return new object[] { new[] { "git", "push", "origin", "main" } };
        yield return new object[] { new[] { "spaces here", "tab\there", "" } };
        yield return new object[] { new[] { @"C:\path with spaces\", "a\"b", @"plain\path" } };
        yield return new object[] { new[] { "\\\"", "two\\\\\"quotes\"" } };
    }

    [Theory]
    [MemberData(nameof(CommandLines))]
    public void CommandLineBuilderAndParserRoundTrip(string[] values)
    {
        Assert.Equal(values, GitSupport.ParseCommandLine(GitSupport.BuildCommandLine(values)));
    }

    [Fact]
    public void ParserAcceptsLegacyQuotedArguments()
    {
        Assert.Equal(new[] { "", "a b", "c", "say \"hi\"" },
            GitSupport.ParseCommandLine("\"\" \"a b\" c \"say \\\"hi\\\"\""));
    }

    [Fact]
    public void ArgumentValidationIsStrict()
    {
        Assert.Throws<ArgumentNullException>(() => GitSupport.BuildCommandLine(null!));
        Assert.Throws<ArgumentException>(() => GitSupport.BuildCommandLine(new string[] { null! }));
        Assert.Throws<ArgumentNullException>(() => GitSupport.ParseCommandLine(null!));
    }

    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", "https", "github.com")]
    [InlineData("http://example.com/repo", "http", "example.com")]
    [InlineData("ssh://git@example.com:2222/repo.git", "ssh", "example.com")]
    [InlineData("git@example.com:org/repo.git", "ssh", "example.com")]
    [InlineData("example.com:org/repo.git", "ssh", "example.com")]
    [InlineData("git://example.com/org/repo.git", "git", "example.com")]
    [InlineData("ssh://git@[2001:db8::1]:22/repo", "ssh", "2001:db8::1")]
    public void ParsesSupportedRemotes(string value, string protocol, string host)
    {
        Assert.True(GitSupport.TryParseRemote(value, out var remote));
        Assert.Equal(protocol, remote.Protocol);
        Assert.Equal(host, remote.Host);
        Assert.Equal(value, remote.Original);
    }

    [Fact]
    public void RejectsNullRemoteValue()
    {
        Assert.False(GitSupport.TryParseRemote(null!, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("origin")]
    [InlineData("C:\\repo")]
    [InlineData("../repo")]
    [InlineData("file:///repo")]
    [InlineData("ftp://example.com/repo")]
    public void RejectsNonRemoteValues(string value)
    {
        Assert.False(GitSupport.TryParseRemote(value, out _));
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://host/repo'")]
    [InlineData("remote: HTTP Basic: Access denied")]
    [InlineData("git@host: Permission denied (publickey).")]
    [InlineData("fatal: could not read Username for 'https://host': terminal prompts disabled")]
    [InlineData("Invalid username or password")]
    public void RecognizesStrongAuthenticationFailures(string output)
    {
        Assert.True(GitSupport.IsAuthenticationFailure(output));
    }

    [Fact]
    public void NullIsNotAnAuthenticationFailure()
    {
        Assert.False(GitSupport.IsAuthenticationFailure(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fatal: repository not found")]
    [InlineData("Permission denied: cannot write working tree")]
    [InlineData("Could not resolve host: example.com")]
    [InlineData("The requested URL returned error: 403")]
    [InlineData("Connection timed out")]
    public void DoesNotMisclassifyOtherFailures(string output)
    {
        Assert.False(GitSupport.IsAuthenticationFailure(output));
    }

    [Theory]
    [InlineData("https://user:secret@example.com/a", "https://***@example.com/a")]
    [InlineData("ssh://git:p%40ss@example.com/a", "ssh://***@example.com/a")]
    [InlineData("error for https://token@example.com/a", "error for https://***@example.com/a")]
    [InlineData("git@example.com:org/repo.git", "git@example.com:org/repo.git")]
    public void RedactsUrlCredentials(string input, string expected)
    {
        Assert.Equal(expected, GitSupport.RedactCredentials(input));
    }

    [Theory]
    [InlineData(new[] { "push", "origin", "main" }, "origin")]
    [InlineData(new[] { "pull", "--rebase", "upstream", "dev" }, "upstream")]
    [InlineData(new[] { "fetch", "--depth", "1", "https://host/repo" }, "https://host/repo")]
    [InlineData(new[] { "fetch", "--repo=mirror" }, "mirror")]
    [InlineData(new[] { "fetch", "--repo", "mirror" }, "mirror")]
    [InlineData(new[] { "-C", "work tree", "push", "--force", "origin" }, "origin")]
    [InlineData(new[] { "push", "--", "-odd-remote" }, "-odd-remote")]
    public void ExtractsExplicitRemote(string[] args, string expected)
    {
        Assert.Equal(expected, GitSupport.GetExplicitRemote(args));
    }

    [Fact]
    public void DoesNotInventImplicitRemote()
    {
        Assert.Null(GitSupport.GetExplicitRemote(new[] { "push" }));
        Assert.Null(GitSupport.GetExplicitRemote(new[] { "pull", "--rebase" }));
        Assert.Null(GitSupport.GetExplicitRemote(new[] { "status" }));
        Assert.Null(GitSupport.GetExplicitRemote(new[] { "fetch", "--depth", "1" }));
    }

    [Theory]
    [InlineData("--git-dir=C:\\repo\\.git")]
    [InlineData("--work-tree=C:\\repo")]
    [InlineData("--namespace=integration")]
    [InlineData("--exec-path=C:\\Git\\libexec")]
    public void FindsOperationAfterEqualsFormGlobalOption(string option)
    {
        Assert.Equal("push", GitSupport.GetOperation(new[] { option, "push", "origin", "main" }));
    }

    [Fact]
    public void ArgumentValidationRejectsControlCharactersAndNullElements()
    {
        GitSupport.ValidateArguments(new[] { "push", "origin", "main" });
        Assert.Throws<ArgumentException>(() => GitSupport.ValidateArguments(new[] { "push\0origin" }));
        Assert.Throws<ArgumentException>(() => GitSupport.ValidateArguments(new[] { "push\rorigin" }));
        Assert.Throws<ArgumentException>(() => GitSupport.ValidateArguments(new[] { "push\norigin" }));
        Assert.Throws<ArgumentException>(() => GitSupport.ValidateArguments(new string[] { null! }));
        Assert.Throws<ArgumentNullException>(() => GitSupport.ValidateArguments(null!));
    }
}
