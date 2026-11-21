using System;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>What the model is told when it acts on its browser. The session itself is <see cref="BrowserSessionTests"/>'s
/// subject; here the question is the sentence and the order of the decisions — an address is checked before anything is
/// asked, and nothing is asked at all for a call that could never run.</summary>
public sealed class BrowserToolsTests
{
    [Fact]
    public async Task Execute_OpensTheAddressAndReportsThePage()
    {
        var session = new FakeSession();
        var asked = (BrowserProposal?)null;
        var result = await Run(Arguments("open", url: "https://grafana.example.com/login"), session,
            confirm: proposal => { asked = proposal; return true; });

        Assert.True(result.Ok);
        Assert.Equal("https://grafana.example.com/login", session.Opened);
        Assert.Equal("https://grafana.example.com/login", asked!.Url);
        Assert.Contains("Opened https://grafana.example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("https://grafana.example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("Title: Sign in", result.Content, StringComparison.Ordinal);
        Assert.Contains("Welcome to Grafana", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · opened grafana.example.com", result.Summary);
    }

    [Fact]
    public async Task Execute_ReadsThePageWithoutNavigatingIt()
    {
        var session = new FakeSession();
        var result = await Run(Arguments("read"), session, confirm: _ => true);

        Assert.True(result.Ok);
        Assert.Equal("read", session.Called);
        Assert.Contains("Welcome to Grafana", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · read grafana.example.com", result.Summary);
    }

    /// <summary>The action is what the card names, so a click's card has to carry the target rather than the word
    /// "click": the user is being asked whether this element may be pressed.</summary>
    [Fact]
    public async Task Execute_NamesTheTargetOnTheCardBeforeClicking()
    {
        var session = new FakeSession();
        var asked = (BrowserProposal?)null;
        var result = await Run(Arguments("click", target: "Sign in"), session,
            confirm: proposal => { asked = proposal; return true; });

        Assert.True(result.Ok);
        Assert.Contains("Sign in", asked!.Ask, StringComparison.Ordinal);
        Assert.Equal("Sign in", asked.Detail);
        Assert.Equal("click", session.Called);
        Assert.Contains("Clicked button \"Sign in\"", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · clicked Sign in", result.Summary);
    }

    [Fact]
    public async Task Execute_FillsAFieldWithTheValueItWasGiven()
    {
        var session = new FakeSession();
        var result = await Run(Arguments("type", target: "#user", text: "admin"), session, confirm: _ => true);

        Assert.True(result.Ok);
        Assert.Equal("#user", session.Target);
        Assert.Equal("admin", session.Filled);
        Assert.Contains("Filled input", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Clearing a field is what an empty string means, so it is a value rather than a missing argument.</summary>
    [Fact]
    public async Task Execute_AcceptsAnEmptyStringAsAClearedField()
    {
        var session = new FakeSession();
        var result = await Run(Arguments("type", target: "#user", text: ""), session, confirm: _ => true);

        Assert.True(result.Ok);
        Assert.Equal("", session.Filled);
    }

    [Fact]
    public async Task Execute_ReportsTheScriptsOwnAnswer()
    {
        var session = new FakeSession();
        session.Page = new BrowserPage
        {
            Url = "https://grafana.example.com/api/health",
            Title = "Grafana",
            Text = "",
            Note = "Script result:\n{\"database\":\"ok\"}"
        };
        var result = await Run(Arguments("script", code: "await fetch('/api/health').then(r => r.text())"), session, confirm: _ => true);

        Assert.True(result.Ok);
        Assert.Equal("await fetch('/api/health').then(r => r.text())", session.Code);
        Assert.Contains("Script result:", result.Content, StringComparison.Ordinal);
        Assert.Contains("database", result.Content, StringComparison.Ordinal);
        Assert.Contains("The page has no visible text.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_SaysSoWhenTheCallNamesNoAction()
    {
        var result = await Run(new JObject(), new FakeSession(), confirm: _ => true);

        Assert.False(result.Ok);
        Assert.Contains("open", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · invalid arguments", result.Summary);
    }

    [Fact]
    public async Task Execute_RefusesAnActionItDoesNotHave()
    {
        var session = new FakeSession();
        var result = await Run(Arguments("scroll"), session, confirm: _ => true);

        Assert.False(result.Ok);
        Assert.Null(session.Called);
        Assert.Equal("browser · invalid arguments", result.Summary);
    }

    [Theory]
    [InlineData("grafana.example.com")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://example.com/a b")]
    public async Task Execute_RefusesAnAddressThatIsNotAPage(string text)
    {
        var session = new FakeSession();
        var asked = false;
        var result = await Run(Arguments("open", url: text), session, confirm: _ => { asked = true; return true; });

        Assert.False(result.Ok);
        Assert.False(asked);
        Assert.Null(session.Opened);
        Assert.Equal("browser · invalid address", result.Summary);
    }

    [Fact]
    public async Task Execute_ReportsADeclineWithoutTouchingThePage()
    {
        var session = new FakeSession();
        var result = await Run(Arguments("click", target: "Delete"), session, confirm: _ => false);

        Assert.False(result.Ok);
        Assert.Null(session.Called);
        Assert.Contains("declined", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · skipped", result.Summary);
    }

    [Fact]
    public async Task Execute_ReportsAFailedActionInThePagesOwnWords()
    {
        var session = new FakeSession { Page = BrowserPage.Failed("No visible element on the page matched that target.") };
        var result = await Run(Arguments("click", target: "Sign in"), session, confirm: _ => true);

        Assert.False(result.Ok);
        Assert.Contains("matched that target", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · failed", result.Summary);
    }

    /// <summary>A page that was cut has to say so, or the model retries a read that already gave it what it will get.</summary>
    [Fact]
    public async Task Execute_SaysWhenThePageTextWasClipped()
    {
        var session = new FakeSession();
        session.Page = new BrowserPage
        {
            Url = "https://grafana.example.com/d/1",
            Title = "Dashboard",
            Text = new string('x', BrowserScripts.MaxTextChars),
            Clipped = true
        };
        var result = await Run(Arguments("read"), session, confirm: _ => true);

        Assert.True(result.Ok);
        Assert.Contains("longer than one answer", result.Content, StringComparison.Ordinal);
        Assert.Contains(BrowserScripts.MaxTextChars.ToString(), result.Content, StringComparison.Ordinal);
    }

    /// <summary>A window with no handler denies, as it does for the other cards: the tool is never run on the theory
    /// that nobody would have minded.</summary>
    [Fact]
    public async Task Execute_SaysSoWhenNoCardIsWired()
    {
        var result = await Run(Arguments("read"), new FakeSession(), confirm: null);

        Assert.False(result.Ok);
        Assert.Equal("browser · unavailable", result.Summary);
    }

    [Fact]
    public async Task Execute_SaysSoWhenTheWindowHasNoBrowser()
    {
        var result = await Run(Arguments("read"), null, confirm: _ => true);

        Assert.False(result.Ok);
        Assert.Equal("browser · unavailable", result.Summary);
    }

    [Fact]
    public async Task Execute_DoesNotActForACancelledTurn()
    {
        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            var session = new FakeSession();
            BrowserTools.Confirm = _ => Task.FromResult(true);
            try
            {
                var result = await BrowserTools.ExecuteAsync(Arguments("click", target: "Sign in"), source.Token, session);
                Assert.False(result.Ok);
                Assert.Null(session.Called);
                Assert.Equal("browser · cancelled", result.Summary);
            }
            finally { BrowserTools.Confirm = null; }
        }
    }

    /// <summary>The one case where the page is still opened for the user: a machine with no Chromium has nothing that
    /// could be driven, and losing the page as well would leave the sign-in impossible rather than merely manual.</summary>
    [Fact]
    public async Task Execute_OpensInTheSystemBrowserWhenNoChromiumIsInstalled()
    {
        var session = new FakeSession { Page = new BrowserPage { Ok = false, NoChromium = true, Error = "no chrome" } };
        var opened = (Uri?)null;
        var result = await Run(Arguments("open", url: "https://grafana.example.com/login"), session, confirm: _ => true,
            open: url => { opened = url; return new BrowserLaunch { Opened = true, Url = url.AbsoluteUri }; });

        Assert.True(result.Ok);
        Assert.Equal("https://grafana.example.com/login", opened!.AbsoluteUri);
        Assert.Contains("Opened in the system browser", result.Content, StringComparison.Ordinal);
        Assert.Contains("no tool of yours can read that tab", result.Content, StringComparison.Ordinal);
        Assert.Contains("Never ask for a password", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ReportsAFailedLaunchWithTheAddressTheUserNeeds()
    {
        var session = new FakeSession { Page = new BrowserPage { Ok = false, NoChromium = true } };
        var result = await Run(Arguments("open", url: "https://example.com/login"), session, confirm: _ => true,
            open: _ => new BrowserLaunch { Opened = false, Error = "blocked by policy" });

        Assert.False(result.Ok);
        Assert.Contains("could not be opened", result.Content, StringComparison.Ordinal);
        Assert.Contains("https://example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("blocked by policy", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · failed", result.Summary);
    }

    [Fact]
    public void Definition_NamesTheActionsAndIsGatedByTheTerminalSwitch()
    {
        Assert.True(BrowserTools.Handles("browser"));
        Assert.False(BrowserTools.Handles("open_browser"));
        Assert.False(BrowserTools.Handles("run_cmd"));

        var function = BrowserTools.Definition.Function;
        Assert.Equal("browser", function["name"]!.Value<string>());
        Assert.Equal("string", function["parameters"]!["properties"]!["action"]!["type"]!.Value<string>());
        var actions = function["parameters"]!["properties"]!["action"]!["enum"]!.ToObject<string[]>()!;
        Assert.Equal(new[] { "open", "read", "click", "type", "script" }, actions);
        Assert.Contains("action", function["parameters"]!["required"]!.ToObject<string[]>()!);
        // The tool is offered by the terminal switch, so the policy has to gate it by that name.
        Assert.NotNull(ToolExecutionPolicy.Refusal("browser", true, true, false));
        Assert.Null(ToolExecutionPolicy.Refusal("browser", true, true, true));
    }

    private static async Task<BrowserToolResult> Run(JObject args, FakeSession? session, Func<BrowserProposal, bool>? confirm,
        Func<Uri, BrowserLaunch>? open = null)
    {
        BrowserTools.Confirm = confirm == null ? (Func<BrowserProposal, Task<bool>>?)null : proposal => Task.FromResult(confirm(proposal));
        try
        {
            return await BrowserTools.ExecuteAsync(args, CancellationToken.None, session, open);
        }
        finally { BrowserTools.Confirm = null; }
    }

    private static JObject Arguments(string action, string? url = null, string? target = null, string? text = null, string? code = null)
    {
        var args = new JObject { ["action"] = action };
        if (url != null) args["url"] = url;
        if (target != null) args["target"] = target;
        if (text != null) args["text"] = text;
        if (code != null) args["code"] = code;
        return args;
    }

    private sealed class FakeSession : IBrowserSession
    {
        public BrowserPage Page { get; set; } = new BrowserPage
        {
            Url = "https://grafana.example.com/login",
            Title = "Sign in",
            Text = "Welcome to Grafana",
            Note = "Opened https://grafana.example.com/login."
        };

        public string? Called { get; private set; }
        public string? Opened { get; private set; }
        public string? Target { get; private set; }
        public string? Filled { get; private set; }
        public string? Code { get; private set; }

        public Task<BrowserPage> OpenAsync(Uri url, CancellationToken token)
        {
            Called = "open"; Opened = url.AbsoluteUri; return Task.FromResult(Page);
        }

        public Task<BrowserPage> ReadAsync(CancellationToken token)
        {
            Called = "read"; return Task.FromResult(Page);
        }

        public Task<BrowserPage> ClickAsync(string target, CancellationToken token)
        {
            Called = "click"; Target = target; return Task.FromResult(Page.Ok ? With("Clicked button \"Sign in\" by its text.") : Page);
        }

        public Task<BrowserPage> FillAsync(string target, string text, CancellationToken token)
        {
            Called = "type"; Target = target; Filled = text;
            return Task.FromResult(Page.Ok ? With("Filled input \"#user\".") : Page);
        }

        public Task<BrowserPage> ScriptAsync(string code, CancellationToken token)
        {
            Called = "script"; Code = code; return Task.FromResult(Page);
        }

        private BrowserPage With(string note) => new BrowserPage
        {
            Url = Page.Url,
            Title = Page.Title,
            Text = Page.Text,
            Clipped = Page.Clipped,
            Note = note
        };

        public void Dispose() { }
    }
}
