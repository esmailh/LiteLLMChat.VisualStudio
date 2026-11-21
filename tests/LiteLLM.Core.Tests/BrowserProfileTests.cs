using System;
using System.IO;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Where the agent's browser keeps its profile, and what it is started with. The profile is the feature — a
/// sign-in that survives the turn — and the flags are what make the window drivable without ever touching the profile
/// the user browses in.</summary>
public sealed class BrowserProfileTests
{
    [Fact]
    public void Directory_IsUnderTheExtensionsOwnData()
    {
        var directory = BrowserProfile.Directory(@"C:\Users\ada\AppData\Local");

        Assert.Equal(Path.Combine(@"C:\Users\ada\AppData\Local", "LiteLLM.VisualStudio", "browser"), directory);
    }

    [Fact]
    public void Directory_UsesTheMachinesProfileByDefault()
    {
        var directory = BrowserProfile.Directory();

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("LiteLLM.VisualStudio", "browser"), directory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Arguments_AskForAPortTheBrowserMayChooseAndAProfileOfItsOwn()
    {
        var arguments = BrowserProfile.Arguments(@"C:\Users\ada\Local\browser", "https://example.com/login");

        Assert.Contains("--remote-debugging-port=0", arguments, StringComparison.Ordinal);
        Assert.Contains("--user-data-dir=\"C:\\Users\\ada\\Local\\browser\"", arguments, StringComparison.Ordinal);
        Assert.Contains("--no-first-run", arguments, StringComparison.Ordinal);
        Assert.Contains("--no-default-browser-check", arguments, StringComparison.Ordinal);
        // The address is last, so a page that takes arguments of its own cannot be read as one of these.
        Assert.EndsWith("--new-window \"https://example.com/login\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_OpenTheProfilesOwnPageWhenNoAddressIsGiven()
    {
        var arguments = BrowserProfile.Arguments(@"C:\p", null);

        Assert.DoesNotContain("--new-window", arguments, StringComparison.Ordinal);
        Assert.Contains("--user-data-dir=\"C:\\p\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_RefuseToGuessAProfile()
    {
        Assert.Throws<ArgumentException>(() => BrowserProfile.Arguments("", "https://example.com"));
    }
}
