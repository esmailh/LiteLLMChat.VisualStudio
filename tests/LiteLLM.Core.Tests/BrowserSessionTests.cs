using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>The session's own decisions, with the process and the socket replaced: when to reuse a browser, when to
/// start one, which tab a call means, and what happens when the page refuses. What a browser would actually do with
/// these calls is not something a test can check; what the session tells the model is.</summary>
public sealed class BrowserSessionTests
{
    private static readonly string Profile = Path.Combine("C:", "fake", "browser");

    [Fact]
    public async Task Open_StartsTheAgentsBrowserOnItsOwnProfileAndNavigatesIt()
    {
        var launch = new FakeLaunch { WritesPortFile = true };
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.True(page.Ok);
        var started = Assert.Single(launch.Started);
        Assert.EndsWith("chrome.exe", started.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--remote-debugging-port=0", started.Arguments, StringComparison.Ordinal);
        Assert.Contains("--user-data-dir=\"" + Profile + "\"", started.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"https://example.com/login\"", started.Arguments, StringComparison.Ordinal);
        Assert.Equal("ws://127.0.0.1:50001/devtools/page/A1", devtools.ConnectedTo);
        Assert.Contains(devtools.Connection.Calls, call => call.Method == "Page.navigate" &&
                                                           (string?)call.Parameters!["url"] == "https://example.com/login");
        Assert.Equal("https://example.com/login", page.Url);
        Assert.Equal("Sign in", page.Title);
        Assert.Equal("Welcome", page.Text);
        Assert.StartsWith("Opened https://example.com/login", page.Note, StringComparison.Ordinal);
    }

    /// <summary>The profile file is what makes a second turn — or a second Visual Studio launch — land on the browser
    /// that already has the sign-in, instead of starting a window beside it.</summary>
    [Fact]
    public async Task Open_ReusesTheBrowserThatIsAlreadyRunning()
    {
        var launch = new FakeLaunch();
        launch.Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/abc";
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Empty(launch.Started);
        Assert.Equal(1, devtools.Probes);
    }

    /// <summary>A port that answers is not proof it is ours: the socket path the browser writes is what ties the
    /// endpoint to this profile, and a stale one must send the call to a new window rather than to someone else's.</summary>
    [Fact]
    public async Task Open_StartsANewBrowserWhenThePortBelongsToAnotherOne()
    {
        var launch = new FakeLaunch { WritesPortFile = true };
        launch.Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/somebody-else";
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Single(launch.Started);
    }

    [Fact]
    public async Task Open_SaysWhichAddressCouldNotBeLoaded()
    {
        var launch = new FakeLaunch { WritesPortFile = true };
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        devtools.Connection.NavigateReply = new JObject { ["errorText"] = "net::ERR_NAME_NOT_RESOLVED" };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.False(page.Ok);
        Assert.Contains("net::ERR_NAME_NOT_RESOLVED", page.Error, StringComparison.Ordinal);
        Assert.Contains("https://example.com/login", page.Error, StringComparison.Ordinal);
    }

    /// <summary>A window whose last tab was closed has no page to act on; acting on one of the browser's own inspector
    /// targets would be worse than saying so.</summary>
    [Fact]
    public async Task Read_SaysSoWhenNoPageIsOpen()
    {
        var launch = new FakeLaunch();
        launch.Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/abc";
        var devtools = new FakeDevTools { List = new JArray() };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.ReadAsync(CancellationToken.None);

        Assert.False(page.Ok);
        Assert.Contains("action open", page.Error, StringComparison.Ordinal);
    }

    /// <summary>Opening a page in a browser that has no tab creates one, rather than starting a second window on the
    /// same profile — which the browser would refuse to do anyway.</summary>
    [Fact]
    public async Task Open_CreatesATabWhenTheWindowHasNone()
    {
        var launch = new FakeLaunch();
        launch.Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/abc";
        var devtools = new FakeDevTools { List = new JArray() };
        devtools.Created = new DevToolsTarget
        {
            Id = "B2",
            Type = "page",
            Url = "https://example.com/login",
            WebSocketUrl = "ws://127.0.0.1:50001/devtools/page/B2"
        };
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal("https://example.com/login", devtools.CreatedFor);
        Assert.Equal("ws://127.0.0.1:50001/devtools/page/B2", devtools.ConnectedTo);
    }

    [Fact]
    public async Task Click_ReportsWhatThePageSaysItClicked()
    {
        var devtools = Running(new JObject { ["ok"] = true, ["how"] = "text", ["what"] = "button \"Sign in\"" });
        using var session = Ready(devtools);

        var page = await session.ClickAsync("Sign in", CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Contains("Clicked button \"Sign in\" by its text.", page.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_ReportsThePagesOwnRefusal()
    {
        var devtools = Running(new JObject { ["ok"] = false, ["error"] = "No visible element on the page matched that target." });
        using var session = Ready(devtools);

        var page = await session.ClickAsync("Sign in", CancellationToken.None);

        Assert.False(page.Ok);
        Assert.Contains("matched that target", page.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ReturnsTheValueItKept()
    {
        var devtools = Running(new JObject { ["ok"] = true }, scriptValue: "{\"database\":\"ok\"}");
        using var session = Ready(devtools);

        var page = await session.ScriptAsync("await fetch('/api/health').then(r => r.text())", CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Contains("Script result:", page.Note, StringComparison.Ordinal);
        Assert.Contains("database", page.Note, StringComparison.Ordinal);
    }

    /// <summary>A script that throws in the page comes back as a protocol exception; the model has to read the page's
    /// own message, not the word "error".</summary>
    [Fact]
    public async Task Script_ReportsThePagesException()
    {
        var devtools = Running(new JObject());
        devtools.Connection.Exception = "TypeError: Cannot read properties of null (reading 'value')\n    at <anonymous>:1:1";
        using var session = Ready(devtools);

        var page = await session.ScriptAsync("null.value", CancellationToken.None);

        Assert.False(page.Ok);
        Assert.Contains("TypeError", page.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("at <anonymous>", page.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_ReportsAMachineWithNoChromiumInsteadOfStartingSomethingElse()
    {
        var launch = new FakeLaunch { HasChromium = false };
        var devtools = new FakeDevTools();
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.False(page.Ok);
        Assert.True(page.NoChromium);
        Assert.Empty(launch.Started);
    }

    [Fact]
    public async Task Open_SaysSoWhenTheBrowserNeverReportsAnEndpoint()
    {
        var launch = new FakeLaunch { WritesPortFile = false };
        var devtools = new FakeDevTools();
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(300));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.False(page.Ok);
        Assert.Contains("did not report a debugging endpoint", page.Error, StringComparison.Ordinal);
        Assert.Contains(Profile, page.Error, StringComparison.Ordinal);
    }

    /// <summary>A page that never says it is loaded must not hang the turn: the text it has rendered so far is still
    /// worth reading, and the note says the read was taken early.</summary>
    [Fact]
    public async Task Open_ReadsAPageThatNeverSaysItHasLoaded()
    {
        var launch = new FakeLaunch { WritesPortFile = true };
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        devtools.Connection.ReadyState = "loading";
        using var session = new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(300));

        var page = await session.OpenAsync(new Uri("https://example.com/login"), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Contains("did not report itself loaded", page.Note, StringComparison.Ordinal);
    }

    private static FakeDevTools Running(JObject answer, string scriptValue = "")
    {
        var devtools = new FakeDevTools { List = PageList("https://example.com/login") };
        devtools.Connection.ClickReply = answer;
        devtools.Connection.ScriptReply = answer.DeepClone() as JObject;
        if (scriptValue.Length > 0) devtools.Connection.ScriptReply!["value"] = scriptValue;
        return devtools;
    }

    private static BrowserSession Ready(FakeDevTools devtools)
    {
        var launch = new FakeLaunch();
        launch.Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/abc";
        return new BrowserSession(launch.Environment(), devtools, Profile, TimeSpan.FromMilliseconds(400));
    }

    private static JArray PageList(string url) => new JArray(new JObject
    {
        ["id"] = "A1",
        ["type"] = "page",
        ["url"] = url,
        ["title"] = "Sign in",
        ["webSocketDebuggerUrl"] = "ws://127.0.0.1:50001/devtools/page/A1"
    });

    private sealed class FakeLaunch
    {
        public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();
        public Dictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool HasChromium { get; set; } = true;
        public bool WritesPortFile { get; set; }

        public BrowserEnvironment Environment() => new BrowserEnvironment
        {
            Start = info =>
            {
                Started.Add(info);
                if (WritesPortFile) Files[DevToolsEndpoint.PortFile(Profile)] = "50001\n/devtools/browser/abc";
                return 4242;
            },
            Exists = path => Files.ContainsKey(path) ||
                             (HasChromium && path.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)),
            ReadText = path => Files.TryGetValue(path, out var text) ? text : null
        };
    }

    private sealed class FakeDevTools : IDevTools
    {
        public JObject? Version { get; set; } = new JObject
        {
            ["webSocketDebuggerUrl"] = "ws://127.0.0.1:50001/devtools/browser/abc"
        };

        public JArray? List { get; set; }
        public DevToolsTarget? Created { get; set; }
        public FakeConnection Connection { get; } = new FakeConnection();
        public int Probes { get; private set; }
        public string? ConnectedTo { get; private set; }
        public string? CreatedFor { get; private set; }

        public Task<JObject?> ProbeAsync(int port, CancellationToken token)
        {
            Probes++;
            return Task.FromResult(Version);
        }

        public Task<JArray?> ListAsync(int port, CancellationToken token) => Task.FromResult(List);

        public Task<DevToolsTarget?> NewTargetAsync(int port, string url, CancellationToken token)
        {
            CreatedFor = url;
            return Task.FromResult(Created);
        }

        public ICdpConnection Connect(string webSocketUrl)
        {
            ConnectedTo = webSocketUrl;
            return Connection;
        }
    }

    private sealed class FakeConnection : ICdpConnection
    {
        public List<(string Method, JObject? Parameters)> Calls { get; } = new List<(string, JObject?)>();
        public JObject NavigateReply { get; set; } = new JObject();
        public JObject ClickReply { get; set; } = new JObject();
        public JObject ScriptReply { get; set; } = new JObject();
        public string ReadyState { get; set; } = "complete";
        public string? Exception { get; set; }

        public Task<JObject> CallAsync(string method, JObject? parameters, CancellationToken token)
        {
            Calls.Add((method, parameters));
            if (method == "Page.navigate") return Task.FromResult(NavigateReply);
            var expression = (string?)parameters?["expression"] ?? "";
            if (Exception != null)
                return Task.FromResult(new JObject
                {
                    ["exceptionDetails"] = new JObject
                    {
                        ["text"] = "Uncaught",
                        ["exception"] = new JObject { ["description"] = Exception }
                    }
                });
            if (expression == "document.readyState") return Value(ReadyState);
            if (expression == BrowserScripts.Read) return Value(Page());
            if (expression.Contains("function depth")) return Value(ClickReply);
            if (expression.Contains("getOwnPropertyDescriptor"))
                return Value(new JObject { ["ok"] = true, ["what"] = "input \"#user\" filled with 5 characters" });
            return Value(ScriptReply);
        }

        /// <summary>What the page says about itself, which every action's answer carries.</summary>
        public JObject Page()
        {
            var page = new JObject
            {
                ["url"] = "https://example.com/login",
                ["title"] = "Sign in",
                ["text"] = "Welcome",
                ["clipped"] = false
            };
            return page;
        }

        private static Task<JObject> Value(JToken value) =>
            Task.FromResult(new JObject { ["result"] = new JObject { ["value"] = value } });

        public void Dispose() { }
    }
}
