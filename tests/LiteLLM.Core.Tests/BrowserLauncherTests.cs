using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The browser tool's two decisions, kept apart from the window that draws them: which addresses may open at
/// all, and which road is taken when the first one fails. Both are ordinary code, so both are checked here rather than
/// by watching a browser appear.</summary>
public sealed class BrowserLauncherTests
{
    [Theory]
    [InlineData("https://grafana.example.com/login")]
    [InlineData("http://localhost:3000/")]
    [InlineData("HTTPS://Example.com/Dashboard?from=now-1h&to=now#panel")]
    [InlineData("  https://example.com  ")]
    public void TryParseUrl_AcceptsHttpAndHttps(string text)
    {
        Assert.True(BrowserLauncher.TryParseUrl(text, out var url, out var error), error);
        Assert.True(url.Scheme == "http" || url.Scheme == "https");
    }

    /// <summary>A host written without its scheme is the mistake a model makes most often, and the answer has to hand
    /// it the correction rather than a sentence about an invalid URI.</summary>
    [Fact]
    public void TryParseUrl_TellsTheModelToPrefixTheScheme()
    {
        Assert.False(BrowserLauncher.TryParseUrl("grafana.example.com/login", out _, out var error));
        Assert.Contains("https://grafana.example.com/login", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:3000")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:network")]
    [InlineData("https://")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/\"quoted\"")]
    public void TryParseUrl_RefusesAnythingThatIsNotAPage(string text) =>
        Assert.False(BrowserLauncher.TryParseUrl(text, out _, out _));

    [Fact]
    public void TryParseUrl_RefusesAnOverlongAddress()
    {
        var text = "https://example.com/" + new string('a', BrowserLauncher.MaxUrlLength);
        Assert.False(BrowserLauncher.TryParseUrl(text, out _, out var error));
        Assert.Contains(BrowserLauncher.MaxUrlLength.ToString(), error, StringComparison.Ordinal);
    }

    /// <summary>The default browser is Windows' own answer to the address, and it is what a person would use.</summary>
    [Fact]
    public void Open_UsesTheDefaultBrowserFirst()
    {
        var calls = new List<ProcessStartInfo>();
        var environment = new BrowserEnvironment
        {
            Start = info => { calls.Add(info); return 4242; },
            Exists = _ => false
        };

        var launch = BrowserLauncher.Open(new Uri("https://example.com/login"), environment);

        Assert.True(launch.Opened);
        Assert.Equal("default browser", launch.Method);
        Assert.Equal(4242, launch.ProcessId);
        Assert.Equal("https://example.com/login", launch.Url);
        var only = Assert.Single(calls);
        Assert.True(only.UseShellExecute);
        Assert.Equal("https://example.com/login", only.FileName);
    }

    [Fact]
    public void Open_FallsBackToCmdStartWhenTheAssociationFails()
    {
        var calls = new List<ProcessStartInfo>();
        var environment = new BrowserEnvironment
        {
            // The fake hands back a pid for every road, and the cmd road must still report none: that pid belongs to
            // cmd, which has exited by the time anyone could use it.
            Start = info =>
            {
                calls.Add(info);
                if (calls.Count == 1) throw new InvalidOperationException("no association");
                return 99;
            },
            Exists = _ => false
        };

        var launch = BrowserLauncher.Open(new Uri("https://example.com/login"), environment);

        Assert.True(launch.Opened);
        Assert.Equal("start", launch.Method);
        Assert.Null(launch.ProcessId);
        Assert.Equal(2, calls.Count);
        Assert.Equal("cmd.exe", calls[1].FileName);
        Assert.Contains("https://example.com/login", calls[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_FallsBackToAnInstalledBrowserWhenNeitherAssociationRoadWorks()
    {
        var calls = new List<ProcessStartInfo>();
        var environment = new BrowserEnvironment
        {
            Start = info =>
            {
                calls.Add(info);
                if (calls.Count <= 2) throw new InvalidOperationException("association is broken");
                return 7;
            },
            Exists = path => path.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)
        };

        var launch = BrowserLauncher.Open(new Uri("https://example.com/login"), environment);

        Assert.True(launch.Opened);
        Assert.Equal("chrome", launch.Method);
        Assert.Equal(7, launch.ProcessId);
        Assert.Equal(3, calls.Count);
        Assert.EndsWith("chrome.exe", calls[2].FileName);
        Assert.Contains("https://example.com/login", calls[2].Arguments, StringComparison.Ordinal);
    }

    /// <summary>A machine with no browser left to try still has to say what went wrong and which address was meant, so
    /// the user can open it themselves.</summary>
    [Fact]
    public void Open_ReportsEveryRoadItTriedWhenAllOfThemFail()
    {
        var environment = new BrowserEnvironment
        {
            Start = _ => throw new InvalidOperationException("blocked"),
            Exists = _ => true
        };

        var launch = BrowserLauncher.Open(new Uri("https://example.com/login"), environment);

        Assert.False(launch.Opened);
        Assert.Equal("https://example.com/login", launch.Url);
        Assert.Contains("default browser", launch.Error, StringComparison.Ordinal);
        Assert.Contains("start", launch.Error, StringComparison.Ordinal);
        Assert.Contains("chrome", launch.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_OnlyOffersBrowsersThatExist()
    {
        var environment = new BrowserEnvironment
        {
            Start = _ => throw new InvalidOperationException("blocked"),
            Exists = _ => false
        };

        var launch = BrowserLauncher.Open(new Uri("https://example.com/login"), environment);

        Assert.False(launch.Opened);
        Assert.DoesNotContain("chrome", launch.Error, StringComparison.Ordinal);
    }
}
