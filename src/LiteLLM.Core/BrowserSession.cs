using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What one action came back with.
///
/// <see cref="Note"/> is what happened — "Clicked button "Sign in"" — and <see cref="Text"/> is what the page says
/// afterwards, because the next thing a person does is look. <see cref="NoChromium"/> exists so the caller can fall
/// back to an ordinary browser and say plainly that this one cannot be read, instead of losing the page entirely.</summary>
public sealed class BrowserPage
{
    public bool Ok { get; set; } = true;
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Clipped { get; set; }
    public string Note { get; set; } = "";
    public string Error { get; set; } = "";
    public bool NoChromium { get; set; }

    public static BrowserPage Failed(string error) => new BrowserPage { Ok = false, Error = error };
}

/// <summary>The five things a turn can do to a page. An interface rather than a class because the tool's wording is
/// worth testing against a page that never exists, and because the real one owns a process and a socket.</summary>
public interface IBrowserSession : IDisposable
{
    Task<BrowserPage> OpenAsync(Uri url, CancellationToken token);
    Task<BrowserPage> ReadAsync(CancellationToken token);
    Task<BrowserPage> ClickAsync(string target, CancellationToken token);
    Task<BrowserPage> FillAsync(string target, string text, CancellationToken token);
    Task<BrowserPage> ScriptAsync(string code, CancellationToken token);
}

/// <summary>The agent's own browser: one Chromium process on a profile this extension keeps, driven over its DevTools
/// protocol. It is what turns "sign in, then tell me" into "sign in, and I will read the page, press the buttons and
/// call the API with that session".
///
/// Nothing here reads the browser's cookie store. The session is used by *being* in the page — a script that calls the
/// site's own API carries the cookies the browser holds, including the HttpOnly ones no client can read — which is
/// both the only way that works and the reason no token has to pass through the transcript.
///
/// The window is not stopped when a turn ends: it is the user's window once it is open, the sign-in in it is meant to
/// still be there next time, and a browser that disappeared between turns would make every credential prompt a fresh
/// one. <see cref="Dispose"/> drops the socket, not the process.</summary>
public sealed class BrowserSession : IBrowserSession
{
    private readonly BrowserEnvironment _environment;
    private readonly IDevTools _devtools;
    private readonly string _profile;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly TimeSpan _timeout;
    private ICdpConnection? _connection;
    private string _connectionUrl = "";
    private string? _targetId;
    private int _port;
    private bool _disposed;

    /// <param name="environment">The launch and the two file reads, replaced by a test.</param>
    /// <param name="devtools">The HTTP endpoint and the socket, replaced by a test.</param>
    /// <param name="profileDirectory">Where the profile lives, so a test does not have to use the real one.</param>
    /// <param name="timeout">How long a browser gets to report its endpoint, and a page to say it has loaded. Twenty
    /// seconds is long enough for a cold start on a slow profile and short enough that a call does not look hung.</param>
    public BrowserSession(BrowserEnvironment? environment = null, IDevTools? devtools = null, string? profileDirectory = null,
        TimeSpan? timeout = null)
    {
        _environment = environment ?? new BrowserEnvironment();
        _devtools = devtools ?? new HttpDevTools();
        _profile = profileDirectory ?? BrowserProfile.Directory();
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public string Profile => _profile;

    public Task<BrowserPage> OpenAsync(Uri url, CancellationToken token) => RunAsync(async ct =>
    {
        var start = await EnsureRunningAsync(url.AbsoluteUri, ct).ConfigureAwait(false);
        if (start != null) return start;
        var target = await EnsureTargetAsync(url.AbsoluteUri, ct).ConfigureAwait(false);
        if (target == null)
            return BrowserPage.Failed("The browser did not open a page for " + url.AbsoluteUri + ", so there is nothing to look at. Call browser again with action open and the same address.");

        var reply = await Connection().CallAsync("Page.navigate", new JObject { ["url"] = url.AbsoluteUri }, ct).ConfigureAwait(false);
        var refused = Text(reply, "errorText");
        if (refused.Length > 0)
            return BrowserPage.Failed("The page could not be loaded: " + refused + ". The address was " + url.AbsoluteUri + ". Check the address, or tell the user it could not be reached.");

        var loaded = await WaitForReadyAsync(ct).ConfigureAwait(false);
        return await DescribeAsync(loaded
            ? "Opened " + url.AbsoluteUri + "."
            : "Opened " + url.AbsoluteUri + ", but the page did not report itself loaded within " + Seconds(_timeout) + "; what is below is what it had rendered.", ct).ConfigureAwait(false);
    }, token);

    public Task<BrowserPage> ReadAsync(CancellationToken token) => RunAsync(async ct =>
    {
        var opened = await PrepareAsync(ct).ConfigureAwait(false);
        if (opened != null) return opened;
        return await DescribeAsync("", ct).ConfigureAwait(false);
    }, token);

    public Task<BrowserPage> ClickAsync(string target, CancellationToken token) => RunAsync(async ct =>
    {
        var opened = await PrepareAsync(ct).ConfigureAwait(false);
        if (opened != null) return opened;
        var answer = await EvaluateObjectAsync(BrowserScripts.Click(target), ct).ConfigureAwait(false);
        if (answer == null) return BrowserPage.Failed("The page returned no answer to the click, so it did not happen.");
        var error = Refusal(answer, "The click did not happen.");
        if (error.Length > 0) return BrowserPage.Failed(error);
        var how = Text(answer, "how") switch
        {
            "selector" => " by selector",
            "text-fragment" => " by a fragment of its text",
            _ => " by its text"
        };
        return await DescribeAsync("Clicked " + Text(answer, "what") + how + ".", ct).ConfigureAwait(false);
    }, token);

    public Task<BrowserPage> FillAsync(string target, string text, CancellationToken token) => RunAsync(async ct =>
    {
        var opened = await PrepareAsync(ct).ConfigureAwait(false);
        if (opened != null) return opened;
        var answer = await EvaluateObjectAsync(BrowserScripts.Fill(target, text), ct).ConfigureAwait(false);
        if (answer == null) return BrowserPage.Failed("The page returned no answer to the fill, so the field was not filled.");
        var error = Refusal(answer, "The field was not filled.");
        if (error.Length > 0) return BrowserPage.Failed(error);
        return await DescribeAsync("Filled " + Text(answer, "what") + ".", ct).ConfigureAwait(false);
    }, token);

    public Task<BrowserPage> ScriptAsync(string code, CancellationToken token) => RunAsync(async ct =>
    {
        var opened = await PrepareAsync(ct).ConfigureAwait(false);
        if (opened != null) return opened;
        var answer = await EvaluateObjectAsync(BrowserScripts.Script(code), ct).ConfigureAwait(false);
        if (answer == null) return BrowserPage.Failed("The page returned no answer to the script, so nothing was run.");
        var error = Refusal(answer, "The script did not run.");
        if (error.Length > 0) return BrowserPage.Failed(error);
        var value = answer["value"];
        var note = value == null || value.Type == JTokenType.Null
            ? "The script ran and returned nothing."
            : "Script result:\n" + Render(value);
        return await DescribeAsync(note, ct).ConfigureAwait(false);
    }, token);

    /// <summary>Every action but <c>open</c> needs a page to act on. Refusing before the browser starts would be
    /// clearer for a session that has never opened one, but a window the user closed and reopened must not cost them a
    /// turn; the sentence below is the one that says which of the two happened.</summary>
    private async Task<BrowserPage?> PrepareAsync(CancellationToken token)
    {
        var start = await EnsureRunningAsync(null, token).ConfigureAwait(false);
        if (start != null) return start;
        var target = await EnsureTargetAsync(null, token).ConfigureAwait(false);
        return target == null
            ? BrowserPage.Failed("The agent's browser has no page open, so there is nothing to read or act on. Call browser with action open and the address you need.")
            : null;
    }

    /// <summary>One action at a time. A page is a single document, and two calls racing on it would mean the second
    /// reading a page the first was in the middle of replacing.</summary>
    private async Task<BrowserPage> RunAsync(Func<CancellationToken, Task<BrowserPage>> body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed) return BrowserPage.Failed("The browser was closed with the window that owns it. Reopen the chat window to use it again.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await body(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A protocol error or a socket that died is the browser's state, not the model's: the connection is dropped
            // so the next call attaches again instead of failing on a socket that will never answer.
            Drop();
            return BrowserPage.Failed(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Attaches to a browser that is already running on this profile, or starts one. Null means ready.</summary>
    private async Task<BrowserPage?> EnsureRunningAsync(string? url, CancellationToken token)
    {
        if (await TryReuseAsync(token).ConfigureAwait(false)) return null;

        var chromium = BrowserLauncher.FindChromium(_environment);
        if (chromium == null)
            return new BrowserPage
            {
                Ok = false,
                NoChromium = true,
                Error = "Neither Chrome nor Edge was found on this machine, so the agent's own browser cannot be started."
            };

        try
        {
            _environment.Start(new ProcessStartInfo(chromium, BrowserProfile.Arguments(_profile, url)) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            return BrowserPage.Failed("The browser could not be started: " + ex.Message + ". The profile it uses is " + _profile + ".");
        }

        // The browser either puts its endpoint up or it does not; the timeout is long enough for a cold start on a slow
        // profile and short enough that a call does not look hung.
        var attempts = Math.Max(1, (int)(_timeout.TotalMilliseconds / 200));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            if (await TryReuseAsync(token).ConfigureAwait(false)) return null;
        }
        return BrowserPage.Failed("The browser started but did not report a debugging endpoint within " + Seconds(_timeout) + ", so its page cannot be driven. " +
                                  "A window may already be using the profile at " + _profile + " without one; close it and call browser again.");
    }

    /// <summary>Browsers that are still running answer on the port their own profile file names, and the socket path in
    /// that file is what proves the port is this profile's rather than another program's.</summary>
    private async Task<bool> TryReuseAsync(CancellationToken token)
    {
        var path = DevToolsEndpoint.PortFile(_profile);
        if (!_environment.Exists(path)) return false;
        var text = _environment.ReadText(path);
        if (!DevToolsEndpoint.TryParseActivePort(text, out var port, out var browserPath)) return false;
        var version = await _devtools.ProbeAsync(port, token).ConfigureAwait(false);
        if (version == null) return false;
        var socket = Text(version, "webSocketDebuggerUrl");
        if (socket.Length == 0) return false;
        if (browserPath.Length > 0 && !socket.EndsWith(browserPath, StringComparison.Ordinal)) return false;
        if (port != _port)
        {
            // A different browser on the same profile: the old socket belongs to a process that is gone.
            Drop();
            _port = port;
        }
        return true;
    }

    private async Task<DevToolsTarget?> EnsureTargetAsync(string? wantedUrl, CancellationToken token)
    {
        var list = await _devtools.ListAsync(_port, token).ConfigureAwait(false);
        var chosen = DevToolsEndpoint.Choose(DevToolsEndpoint.ParseTargets(list), _targetId, wantedUrl);
        if (chosen == null && !string.IsNullOrEmpty(wantedUrl))
            chosen = await _devtools.NewTargetAsync(_port, wantedUrl!, token).ConfigureAwait(false);
        if (chosen == null) return null;
        if (_connection == null || !string.Equals(_connectionUrl, chosen.WebSocketUrl, StringComparison.Ordinal))
        {
            Drop();
            _connection = _devtools.Connect(chosen.WebSocketUrl);
            _connectionUrl = chosen.WebSocketUrl;
        }
        _targetId = chosen.Id;
        return chosen;
    }

    /// <summary>Waits until the document says it is complete, then gives the first render a moment: a single-page app
    /// reports "complete" while its shell is still empty, and reading it then is reading the wrong page.</summary>
    private async Task<bool> WaitForReadyAsync(CancellationToken token)
    {
        var attempts = Math.Max(1, (int)(_timeout.TotalMilliseconds / 150));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var state = await EvaluateAsync("document.readyState", token).ConfigureAwait(false);
                if (state != null && state.Type == JTokenType.String &&
                    string.Equals(state.Value<string>(), "complete", StringComparison.Ordinal))
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                    return true;
                }
            }
            catch (CdpException)
            {
                // The navigation that just started destroys the context the question was asked in. Asked again, in the
                // context that replaces it, is the answer.
            }
            await Task.Delay(150, token).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<BrowserPage> DescribeAsync(string note, CancellationToken token)
    {
        var value = await EvaluateAsync(BrowserScripts.Read, token).ConfigureAwait(false);
        if (value is not JObject page)
            return BrowserPage.Failed("The page did not describe itself, so nothing could be read from it. It may have been closed; call browser with action read to try again.");
        var clipped = page["clipped"];
        return new BrowserPage
        {
            Url = Text(page, "url"),
            Title = Text(page, "title"),
            Text = Text(page, "text"),
            Clipped = clipped != null && clipped.Type == JTokenType.Boolean && clipped.Value<bool>(),
            Note = note
        };
    }

    private async Task<JToken?> EvaluateAsync(string expression, CancellationToken token)
    {
        var reply = await Connection().CallAsync("Runtime.evaluate", new JObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
            // A click the page asked for a gesture to allow is the ordinary shape of a real button, and the user has
            // already approved the action on the card.
            ["userGesture"] = true
        }, token).ConfigureAwait(false);
        var details = reply["exceptionDetails"];
        if (details != null && details.Type != JTokenType.Null)
            throw new CdpException("The page refused the script: " + BrowserScripts.Describe(details));
        return reply["result"]?["value"];
    }

    private async Task<JObject?> EvaluateObjectAsync(string expression, CancellationToken token)
    {
        var value = await EvaluateAsync(expression, token).ConfigureAwait(false);
        return value as JObject;
    }

    private ICdpConnection Connection() =>
        _connection ?? throw new CdpException("No page is attached, so nothing was sent.");

    private void Drop()
    {
        var connection = _connection;
        _connection = null;
        _connectionUrl = "";
        _targetId = null;
        try { connection?.Dispose(); } catch (Exception) { }
    }

    /// <summary>The sentence inside an answer whose <c>ok</c> is false; empty when the page did what was asked.</summary>
    private static string Refusal(JObject answer, string fallback)
    {
        var ok = answer["ok"];
        if (ok != null && ok.Type == JTokenType.Boolean && ok.Value<bool>()) return "";
        var error = Text(answer, "error");
        return error.Length > 0 ? error : fallback;
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#") + " seconds";

    private static string Render(JToken value)
    {
        var text = value.Type == JTokenType.String ? value.Value<string>() ?? "" : value.ToString(Formatting.None);
        return text.Length > BrowserScripts.MaxResultChars
            ? text.Substring(0, BrowserScripts.MaxResultChars) + "…"
            : text;
    }

    private static string Text(JToken? token, string name)
    {
        var value = token?[name];
        return value != null && value.Type == JTokenType.String ? value.Value<string>() ?? "" : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Drop();
        // The semaphore is left alone on purpose: a call still waiting on it belongs to a turn that is being torn
        // down, and disposing it would turn a cancellation into an ObjectDisposedException in the middle of it.
    }
}
