using System;
using System.Linq;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The two plain-text decisions the DevTools endpoint asks of the session: where the browser wrote its port,
/// and which of its pages a call means. Both are what a browser could get wrong quietly, so both are checked here
/// rather than by watching a window appear.</summary>
public sealed class DevToolsEndpointTests
{
    [Theory]
    [InlineData("50001\n/devtools/browser/abc", 50001, "/devtools/browser/abc")]
    [InlineData("50001\r\n/devtools/browser/abc\r\n", 50001, "/devtools/browser/abc")]
    [InlineData(" 50001 \n /devtools/browser/abc ", 50001, "/devtools/browser/abc")]
    [InlineData("50001", 50001, "")]
    public void TryParseActivePort_ReadsWhatTheBrowserWrote(string text, int port, string path)
    {
        Assert.True(DevToolsEndpoint.TryParseActivePort(text, out var parsed, out var parsedPath));
        Assert.Equal(port, parsed);
        Assert.Equal(path, parsedPath);
    }

    /// <summary>A port line without a path is still a port; anything else is a file that has stopped meaning what it
    /// used to, and using it would point a call at whatever now holds that number.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void TryParseActivePort_RefusesWhatIsNotAPort(string text) =>
        Assert.False(DevToolsEndpoint.TryParseActivePort(text, out _, out _));

    [Fact]
    public void TryParseActivePort_RefusesNoFileAtAll() =>
        Assert.False(DevToolsEndpoint.TryParseActivePort(null, out _, out _));

    [Fact]
    public void Urls_NameThisMachinesEndpointOnly()
    {
        Assert.Equal("http://127.0.0.1:50001/json/version", DevToolsEndpoint.VersionUrl(50001));
        Assert.Equal("http://127.0.0.1:50001/json/list", DevToolsEndpoint.ListUrl(50001));
        // A page with a query string of its own must arrive whole, not cut at its first '&'.
        Assert.Equal("http://127.0.0.1:50001/json/new?https%3A%2F%2Fexample.com%2F%3Fa%3D1%26b%3D2",
            DevToolsEndpoint.NewTargetUrl(50001, "https://example.com/?a=1&b=2"));
    }

    [Fact]
    public void ParseTargets_KeepsOnlyEntriesThatCanBeAttachedTo()
    {
        var targets = DevToolsEndpoint.ParseTargets(new JArray(
            new JObject { ["id"] = "A1", ["type"] = "page", ["url"] = "https://example.com/", ["title"] = "Example", ["webSocketDebuggerUrl"] = "ws://a" },
            new JObject { ["id"] = "A2", ["type"] = "page", ["url"] = "https://example.com/" },
            new JObject { ["type"] = "page", ["webSocketDebuggerUrl"] = "ws://c" },
            new JObject { ["id"] = "A4", ["webSocketDebuggerUrl"] = "ws://d" },
            "not an object"));

        // An entry with an id and a socket is a target even when it does not say what kind it is; which of them is a
        // page is the choice's question, not the parse's.
        Assert.Equal(2, targets.Count);
        Assert.Equal("A1", targets[0].Id);
        Assert.Equal("page", targets[0].Type);
        Assert.Equal("Example", targets[0].Title);
        Assert.Equal("ws://a", targets[0].WebSocketUrl);
        Assert.Equal("A4", targets[1].Id);
    }

    [Fact]
    public void ParseTargets_SurvivesNoListAtAll()
    {
        Assert.Empty(DevToolsEndpoint.ParseTargets(null));
        Assert.Empty(DevToolsEndpoint.ParseTargets(new JObject()));
    }

    /// <summary>The remembered tab wins, because a conversation that keeps jumping between two tabs of the same site
    /// reads a page it did not ask for.</summary>
    [Fact]
    public void Choose_PrefersTheTabThisSessionWasDriving()
    {
        var chosen = DevToolsEndpoint.Choose(Targets(), "B2", "https://example.com/login");

        Assert.Equal("B2", chosen!.Id);
    }

    [Fact]
    public void Choose_TakesTheAddressItJustAskedFor()
    {
        var chosen = DevToolsEndpoint.Choose(Targets(), null, "https://example.com/login");

        Assert.Equal("A1", chosen!.Id);
    }

    /// <summary>A sign-in page that bounces to another host is the ordinary case; the tab that was asked for is still
    /// the one the browser opened, and starting a second one would leave two sign-ins half done.</summary>
    [Fact]
    public void Choose_FollowsARedirectToTheSameHost()
    {
        var chosen = DevToolsEndpoint.Choose(Targets(), null, "https://example.com/login?next=%2Fhome");

        Assert.Equal("A1", chosen!.Id);
    }

    [Fact]
    public void Choose_TakesTheFirstRealPageRatherThanABlankOne()
    {
        var chosen = DevToolsEndpoint.Choose(Targets(), null, null);

        Assert.Equal("A1", chosen!.Id);
    }

    [Fact]
    public void Choose_HasNoAnswerWhenNothingIsAPage()
    {
        Assert.Null(DevToolsEndpoint.Choose(new[]
        {
            new DevToolsTarget { Id = "S1", Type = "service_worker", Url = "https://example.com/sw.js", WebSocketUrl = "ws://s" },
            new DevToolsTarget { Id = "D1", Type = "page", Url = "devtools://devtools/bundled/inspector.html", WebSocketUrl = "ws://d" }
        }, null, null));
    }

    [Fact]
    public void Choose_IsNotFooledByGarbageInTheList()
    {
        Assert.Null(DevToolsEndpoint.Choose(Array.Empty<DevToolsTarget>(), "A1", "https://example.com/login"));
    }

    private static DevToolsTarget[] Targets() => new[]
    {
        new DevToolsTarget { Id = "A1", Type = "page", Url = "https://example.com/login", WebSocketUrl = "ws://a1" },
        new DevToolsTarget { Id = "B2", Type = "page", Url = "https://example.com/other", WebSocketUrl = "ws://b2" },
        new DevToolsTarget { Id = "S1", Type = "service_worker", Url = "https://example.com/sw.js", WebSocketUrl = "ws://s1" }
    };
}
