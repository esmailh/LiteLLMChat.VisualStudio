using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What the user is asked before a page opens: the address, which is the whole of the decision.</summary>
public sealed class BrowserProposal
{
    public string Url { get; set; } = "";
}

/// <summary>What a browser call hands back. Its own type rather than the extension's <c>ToolResult</c>, because this
/// half lives in Core and has to be reachable from the tests; the caller copies the three fields across.</summary>
public sealed class BrowserToolResult
{
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Ok { get; set; } = true;
}

/// <summary>The browser tool. One call puts a page in front of the user and returns; what they do on it — sign in,
/// approve a consent screen, copy a key that is shown once — is the reason the agent asked.
///
/// The tool is the whole of the capability, deliberately. It cannot read the page, click in it or reach the cookies
/// and tokens the browser holds, and the result says so in as many words: an agent that believes it is now signed in
/// to something answers the next question from an assumption instead of asking for the one fact it needs.
///
/// It is gated like a command (<see cref="ToolExecutionPolicy"/> keeps it under the terminal switch) and confirmed
/// through the same card, because opening a window on the user's desktop is a side effect they are entitled to
/// refuse. The Claude backend never sees it: the CLI runs its own harness.</summary>
public static class BrowserTools
{
    public static Func<BrowserProposal, Task<bool>>? Confirm;

    public static readonly ToolDefinition Definition = new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = "open_browser",
            ["description"] = "Open an http or https address in the user's own browser so they can do something on it: sign in to a service, approve a consent screen, create or copy an API key, or look at a page. It returns at once and cannot read the page, click in it, or use the session's cookies. Say what the page is for, wait for the user to confirm they are done, then continue.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["url"] = new JObject { ["type"] = "string", ["description"] = "Absolute address including its scheme, https://host/path. Only http and https can be opened." }
                },
                ["required"] = new JArray("url")
            }
        }
    };

    public static bool Handles(string name) => name == "open_browser";

    /// <param name="open">The launch itself, defaulting to <see cref="BrowserLauncher.Open"/>. A test supplies its own
    /// so the sentence the model reads can be checked without a browser window appearing.</param>
    public static async Task<BrowserToolResult> ExecuteAsync(JObject args, CancellationToken token,
        Func<Uri, BrowserLaunch>? open = null)
    {
        if (!ToolArguments.TryText(args, "url", out var text, out var error))
            return Fail(error, "open browser · invalid arguments");
        if (!BrowserLauncher.TryParseUrl(text, out var url, out error))
            return Fail(error, "open browser · invalid address");
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled before the browser opened, so nothing was opened.", "open browser · cancelled");
        var confirm = Confirm;
        if (confirm == null)
            return Fail("Opening a browser is unavailable in this window, so nothing was opened.", "open browser · unavailable");
        if (!await confirm(new BrowserProposal { Url = url.AbsoluteUri }).ConfigureAwait(false))
            return Fail("The user declined to open the browser, so nothing was opened. Do not call open_browser for this address again unless the user asks for it.", "open browser · skipped");

        BrowserLaunch launch;
        try
        {
            var start = open ?? (address => BrowserLauncher.Open(address));
            launch = start(url);
        }
        catch (Exception ex)
        {
            launch = new BrowserLaunch { Opened = false, Url = url.AbsoluteUri, Error = ex.Message };
        }
        if (!launch.Opened)
            return Fail("The browser could not be opened, so no page was shown. Give the user this address to open themselves: " + url.AbsoluteUri +
                        (launch.Error.Length == 0 ? "" : "\nWhat failed: " + launch.Error), "open browser · failed");

        return new BrowserToolResult
        {
            Content = "Opened in the system browser: " + url.AbsoluteUri + "\n" +
                      "The page is the user's own session. You cannot read that tab, click in it, or use the cookies, tokens or account it holds — none of your tools can. " +
                      "Tell the user what to do on the page, then wait for them to say they are done before you continue from what they report. " +
                      "Never ask for a password, a token or a one-time code in this chat; ask them to enter it on the page instead.",
            Summary = "browser · opened " + url.Host
        };
    }

    private static BrowserToolResult Fail(string content, string summary) =>
        new BrowserToolResult { Content = content, Summary = summary, Ok = false };
}
