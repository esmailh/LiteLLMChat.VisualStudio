using System;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>What the model is told when it asks for a page. The launch itself is <see cref="BrowserLauncherTests"/>'s
/// subject; here the question is the sentence, because an agent that thinks it can see the tab answers the next
/// question from a guess instead of asking for the fact it needs.</summary>
public sealed class BrowserToolsTests
{
    [Fact]
    public async Task Execute_OpensThePageAndSaysTheTabIsNotItsOwn()
    {
        var asked = "";
        var result = await Run(Arguments("https://grafana.example.com/login"), confirm: proposal =>
        {
            asked = proposal.Url;
            return true;
        }, open: url => new BrowserLaunch
        {
            Opened = true,
            ProcessId = 1,
            Method = "default browser",
            Url = url.AbsoluteUri
        });

        Assert.True(result.Ok);
        Assert.Equal("https://grafana.example.com/login", asked);
        Assert.Contains("Opened in the system browser: https://grafana.example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("cannot read that tab", result.Content, StringComparison.Ordinal);
        Assert.Contains("Never ask for a password", result.Content, StringComparison.Ordinal);
        Assert.Equal("browser · opened grafana.example.com", result.Summary);
    }

    [Fact]
    public async Task Execute_ReportsADeclineWithoutOpeningAnything()
    {
        var opened = false;
        var result = await Run(Arguments("https://example.com"), confirm: _ => false, open: url =>
        {
            opened = true;
            return new BrowserLaunch { Opened = true };
        });

        Assert.False(result.Ok);
        Assert.False(opened);
        Assert.Contains("declined", result.Content, StringComparison.Ordinal);
        Assert.Equal("open browser · skipped", result.Summary);
    }

    [Theory]
    [InlineData("grafana.example.com")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public async Task Execute_RefusesAnAddressThatIsNotAPage(string text)
    {
        var asked = false;
        var result = await Run(Arguments(text), confirm: _ => { asked = true; return true; },
            open: url => new BrowserLaunch { Opened = true });

        Assert.False(result.Ok);
        Assert.False(asked);
        Assert.Equal("open browser · invalid address", result.Summary);
    }

    [Fact]
    public async Task Execute_SaysSoWhenTheCallNamesNoAddress()
    {
        var result = await Run(new JObject(), confirm: _ => true, open: url => new BrowserLaunch { Opened = true });

        Assert.False(result.Ok);
        Assert.Equal("open browser · invalid address", result.Summary);
    }

    [Fact]
    public async Task Execute_ReportsAFailedLaunchWithTheAddressTheUserNeeds()
    {
        var result = await Run(Arguments("https://example.com/login"), confirm: _ => true,
            open: _ => new BrowserLaunch { Opened = false, Error = "blocked by policy" });

        Assert.False(result.Ok);
        Assert.Contains("could not be opened", result.Content, StringComparison.Ordinal);
        Assert.Contains("https://example.com/login", result.Content, StringComparison.Ordinal);
        Assert.Contains("blocked by policy", result.Content, StringComparison.Ordinal);
        Assert.Equal("open browser · failed", result.Summary);
    }

    /// <summary>A window with no handler denies, as it does for the other cards: the tool is never run on the theory
    /// that nobody would have minded.</summary>
    [Fact]
    public async Task Execute_SaysSoWhenNoCardIsWired()
    {
        var result = await Run(Arguments("https://example.com/login"), confirm: null,
            open: _ => new BrowserLaunch { Opened = true });

        Assert.False(result.Ok);
        Assert.Equal("open browser · unavailable", result.Summary);
    }

    [Fact]
    public async Task Execute_DoesNotOpenAPageForACancelledTurn()
    {
        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            var opened = false;
            BrowserTools.Confirm = _ => Task.FromResult(true);
            try
            {
                var result = await BrowserTools.ExecuteAsync(Arguments("https://example.com/login"), source.Token,
                    url => { opened = true; return new BrowserLaunch { Opened = true }; });
                Assert.False(result.Ok);
                Assert.False(opened);
                Assert.Equal("open browser · cancelled", result.Summary);
            }
            finally { BrowserTools.Confirm = null; }
        }
    }

    [Fact]
    public void Definition_NamesOneRequiredAddress()
    {
        Assert.True(BrowserTools.Handles("open_browser"));
        Assert.False(BrowserTools.Handles("run_cmd"));

        var function = BrowserTools.Definition.Function;
        Assert.Equal("open_browser", function["name"]!.Value<string>());
        Assert.Equal("string", function["parameters"]!["properties"]!["url"]!["type"]!.Value<string>());
        Assert.Contains("url", function["parameters"]!["required"]!.ToObject<string[]>()!);
        // The tool is offered by the terminal switch, so the policy has to gate it by that name.
        Assert.NotNull(ToolExecutionPolicy.Refusal("open_browser", true, true, false));
    }

    private static async Task<BrowserToolResult> Run(JObject args, Func<BrowserProposal, bool>? confirm, Func<Uri, BrowserLaunch> open)
    {
        BrowserTools.Confirm = confirm == null ? (Func<BrowserProposal, Task<bool>>?)null : proposal => Task.FromResult(confirm(proposal));
        try
        {
            return await BrowserTools.ExecuteAsync(args, CancellationToken.None, open);
        }
        finally { BrowserTools.Confirm = null; }
    }

    private static JObject Arguments(string url) => new JObject { ["url"] = url };
}
