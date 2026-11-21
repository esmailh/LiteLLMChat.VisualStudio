using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What the user is asked before one browser action: what it is, on which page, and the part the model wrote
/// — a selector, a field's text or the script itself. The card names all three, because "the agent wants to use your
/// browser" is not a question anyone can answer.</summary>
public sealed class BrowserProposal
{
    public string Action { get; set; } = "";
    public string Ask { get; set; } = "";
    public string Url { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>What a browser call hands back. Its own type rather than the extension's <c>ToolResult</c>, because this
/// half lives in Core and has to be reachable from the tests; the caller copies the three fields across.</summary>
public sealed class BrowserToolResult
{
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Ok { get; set; } = true;
}

/// <summary>The browser tool: one interface over the five things a person does on a page — open it, read it, click it,
/// type in it, and run something in it.
///
/// It is the agent's own browser rather than the user's (<see cref="BrowserSession"/>), and that difference is the
/// whole feature: the page it opens is a page it can read, the buttons it sees are buttons it can press, and the
/// sign-in done there is a session it can then use — including for the site's API, through a script that runs in the
/// page. The alternative, telling the model it may not look, was what made "help me finish this in Grafana" end in an
/// apology.
///
/// It is gated like a command (<see cref="ToolExecutionPolicy"/> keeps it under the terminal switch) and confirmed
/// through a card for every action, because acting inside a browser window is a side effect the user is entitled to
/// refuse — and one they should see announced rather than discover. The Claude backend never sees any of it: the CLI
/// runs its own harness.</summary>
public static class BrowserTools
{
    public const string Name = "browser";

    private static readonly string[] Actions = { "open", "read", "click", "type", "script" };

    public static Func<BrowserProposal, Task<bool>>? Confirm;

    public static readonly ToolDefinition Definition = new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = Name,
            ["description"] = "Drive the agent's own Chromium browser: a real Chrome or Edge window with a profile of its own, " +
                              "separate from the user's normal browser, kept between turns so a sign-in done in it stays signed in. " +
                              "Actions: \"open\" (url) navigates to a page; \"read\" reports the current page's address, title and visible text; " +
                              "\"click\" (target) clicks an element, by CSS selector or by its visible text; \"type\" (target, text) fills a field " +
                              "with the input event the page listens for; \"script\" (code) runs JavaScript in the page — one expression, or " +
                              "statements with an explicit return, and a promise is awaited, so await fetch('/api/...', {credentials:'same-origin'}) " +
                              "calls the site's own API with the signed-in session. Every action answers with the page as it is afterwards. " +
                              "Use it instead of asking the user to click or paste: read the page, press the button, and report what happened. " +
                              "Never print a password, cookie or token into the chat.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["action"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("open", "read", "click", "type", "script"),
                        ["description"] = "What to do on the page."
                    },
                    ["url"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "For open: absolute address including its scheme, https://host/path. Only http and https."
                    },
                    ["target"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "For click and type: a CSS selector, or for click the visible text of the link or button."
                    },
                    ["text"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "For type: the value to put in the field. An empty string clears it."
                    },
                    ["code"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "For script: JavaScript to run in the page. A single expression returns its value; statements need return."
                    }
                },
                ["required"] = new JArray("action")
            }
        }
    };

    public static bool Handles(string name) => name == Name;

    /// <param name="session">The window's browser, or null when this window cannot offer one.</param>
    /// <param name="open">The fallback launch for a machine with no Chromium, defaulting to
    /// <see cref="BrowserLauncher.Open"/>. A test supplies its own so the sentence the model reads can be checked
    /// without a browser window appearing.</param>
    public static async Task<BrowserToolResult> ExecuteAsync(JObject args, CancellationToken token,
        IBrowserSession? session, Func<Uri, BrowserLaunch>? open = null)
    {
        if (!ToolArguments.TryText(args, "action", out var action, out var error))
            return Fail(error, "browser · invalid arguments");
        action = (action ?? "").Trim().ToLowerInvariant();
        if (action.Length == 0)
            return Fail("browser needs an action. Use \"open\" with a url, or \"read\", \"click\", \"type\" or \"script\" on the page already open.",
                "browser · invalid arguments");
        if (Array.IndexOf(Actions, action) < 0)
            return Fail("`action` must be one of open, read, click, type or script; it arrived as \"" + action + "\".",
                "browser · invalid arguments");

        switch (action)
        {
            case "open": return await OpenAsync(args, token, session, open).ConfigureAwait(false);
            case "read": return await ReadAsync(token, session).ConfigureAwait(false);
            case "click": return await ClickAsync(args, token, session).ConfigureAwait(false);
            case "type": return await FillAsync(args, token, session).ConfigureAwait(false);
            default: return await ScriptAsync(args, token, session).ConfigureAwait(false);
        }
    }

    private static async Task<BrowserToolResult> OpenAsync(JObject args, CancellationToken token, IBrowserSession? session,
        Func<Uri, BrowserLaunch>? open)
    {
        if (!ToolArguments.TryText(args, "url", out var text, out var error))
            return Fail(error, "browser · invalid arguments");
        if (!BrowserLauncher.TryParseUrl(text, out var url, out error))
            return Fail(error, "browser · invalid address");
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled before the page opened, so nothing was opened.", "browser · cancelled");
        var refused = await AskAsync(new BrowserProposal { Action = "open", Url = url.AbsoluteUri, Ask = "Open this page in the agent's browser?" }).ConfigureAwait(false);
        if (refused != null) return refused;
        if (session == null) return Unavailable();

        var page = await session.OpenAsync(url, token).ConfigureAwait(false);
        if (page.Ok) return Done(page, "browser · opened " + url.Host);
        if (!page.NoChromium) return Fail(page.Error, "browser · failed");

        // No Chromium means nothing here can be driven, so the page is handed to whatever browser there is and the
        // model is told the truth about it: it is on the user's screen, and it is not visible to any tool.
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
            return Fail("No Chromium browser is installed, and the page could not be opened anywhere else either. Give the user this address to open themselves: " +
                        url.AbsoluteUri + (launch.Error.Length == 0 ? "" : "\nWhat failed: " + launch.Error), "browser · failed");

        return new BrowserToolResult
        {
            Content = "Opened in the system browser: " + url.AbsoluteUri + "\n" +
                      "No Chrome or Edge is installed, so this is the user's own browser and no tool of yours can read that tab, click in it, or use the cookies, tokens or account it holds. " +
                      "Tell the user what to do on the page, then wait for them to say they are done before you continue from what they report. " +
                      "Never ask for a password, a token or a one-time code in this chat; ask them to enter it on the page instead.",
            Summary = "browser · opened " + url.Host
        };
    }

    private static async Task<BrowserToolResult> ReadAsync(CancellationToken token, IBrowserSession? session)
    {
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled, so the page was not read.", "browser · cancelled");
        var refused = await AskAsync(new BrowserProposal { Action = "read", Ask = "Let the agent read the page in its browser?" }).ConfigureAwait(false);
        if (refused != null) return refused;
        if (session == null) return Unavailable();

        var page = await session.ReadAsync(token).ConfigureAwait(false);
        return page.Ok ? Done(page, "browser · read " + (page.Url.Length > 0 ? Host(page.Url) : "page")) : Fail(page.Error, "browser · failed");
    }

    private static async Task<BrowserToolResult> ClickAsync(JObject args, CancellationToken token, IBrowserSession? session)
    {
        if (!ToolArguments.TryText(args, "target", out var target, out var error))
            return Fail(error, "browser · invalid arguments");
        target = target.Trim();
        if (target.Length == 0)
            return Fail("click needs a target: a CSS selector, or the visible text of the link or button to press.", "browser · invalid arguments");
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled, so nothing was clicked.", "browser · cancelled");
        var refused = await AskAsync(new BrowserProposal
        {
            Action = "click",
            Ask = "Click " + Quote(target) + " in the agent's browser?",
            Detail = target
        }).ConfigureAwait(false);
        if (refused != null) return refused;
        if (session == null) return Unavailable();

        var page = await session.ClickAsync(target, token).ConfigureAwait(false);
        return page.Ok ? Done(page, "browser · clicked " + Clip(target, 60)) : Fail(page.Error, "browser · failed");
    }

    private static async Task<BrowserToolResult> FillAsync(JObject args, CancellationToken token, IBrowserSession? session)
    {
        if (!ToolArguments.TryText(args, "target", out var target, out var error))
            return Fail(error, "browser · invalid arguments");
        if (args.Property("text") == null)
            return Fail("type needs `text`: the value to put in the field. Send an empty string to clear it.", "browser · invalid arguments");
        if (!ToolArguments.TryText(args, "text", out var text, out error))
            return Fail(error, "browser · invalid arguments");
        target = target.Trim();
        if (target.Length == 0)
            return Fail("type needs a target: the CSS selector of the field to fill.", "browser · invalid arguments");
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled, so nothing was typed.", "browser · cancelled");
        var refused = await AskAsync(new BrowserProposal
        {
            Action = "type",
            Ask = "Fill the field " + Quote(target) + " in the agent's browser?",
            Detail = target + (text.Length == 0 ? " (cleared)" : "\n-> " + Clip(text, 200))
        }).ConfigureAwait(false);
        if (refused != null) return refused;
        if (session == null) return Unavailable();

        var page = await session.FillAsync(target, text, token).ConfigureAwait(false);
        return page.Ok ? Done(page, "browser · filled " + Clip(target, 60)) : Fail(page.Error, "browser · failed");
    }

    private static async Task<BrowserToolResult> ScriptAsync(JObject args, CancellationToken token, IBrowserSession? session)
    {
        if (!ToolArguments.TryText(args, "code", out var code, out var error))
            return Fail(error, "browser · invalid arguments");
        if (code.Trim().Length == 0)
            return Fail("script needs `code`: the JavaScript to run in the page.", "browser · invalid arguments");
        if (token.IsCancellationRequested)
            return Fail("The turn was cancelled, so nothing was run.", "browser · cancelled");
        var refused = await AskAsync(new BrowserProposal
        {
            Action = "script",
            Ask = "Run this script on the page in the agent's browser?",
            Detail = Clip(code.Trim(), 2000)
        }).ConfigureAwait(false);
        if (refused != null) return refused;
        if (session == null) return Unavailable();

        var page = await session.ScriptAsync(code, token).ConfigureAwait(false);
        return page.Ok ? Done(page, "browser · script on " + (page.Url.Length > 0 ? Host(page.Url) : "page")) : Fail(page.Error, "browser · failed");
    }

    /// <summary>Null means the user agreed; anything else is the answer to hand back.</summary>
    private static async Task<BrowserToolResult?> AskAsync(BrowserProposal proposal)
    {
        var confirm = Confirm;
        if (confirm == null)
            return Fail("The browser is unavailable in this window, so nothing was done.", "browser · unavailable");
        if (!await confirm(proposal).ConfigureAwait(false))
            return Fail("The user declined this browser action, so it was not performed. Do not repeat it unless they ask; if the page needs something done, tell them what and wait.",
                "browser · skipped");
        return null;
    }

    private static BrowserToolResult Unavailable() =>
        Fail("The browser is unavailable in this window, so nothing was done.", "browser · unavailable");

    /// <summary>One page, as the model reads it: what just happened, where it is now, and what it says. The clipping is
    /// stated rather than implied, because a model that cannot tell a short page from a cut one retries a read that
    /// already gave it everything it is going to get.</summary>
    private static BrowserToolResult Done(BrowserPage page, string summary)
    {
        var content = (page.Note.Length > 0 ? page.Note + "\n\n" : "") +
                      (page.Url.Length > 0 ? page.Url : "") +
                      (page.Title.Length > 0 ? "\nTitle: " + page.Title : "");
        var text = page.Text.Trim();
        content += text.Length == 0
            ? "\n\nThe page has no visible text."
            : (page.Clipped
                ? "\n\nThe page's text is longer than one answer; the first " + BrowserScripts.MaxTextChars + " characters are below. Use script to read a specific part:\n"
                : "\n\nVisible text:\n") + text;
        return new BrowserToolResult { Content = content, Summary = summary };
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : url;

    private static string Quote(string value) => "\"" + Clip(value, 60) + "\"";

    private static string Clip(string value, int limit) =>
        value.Length > limit ? value.Substring(0, limit) + "…" : value;

    private static BrowserToolResult Fail(string content, string summary) =>
        new BrowserToolResult { Content = content, Summary = summary, Ok = false };
}
