using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LiteLLM.Core;

/// <summary>What came of one attempt to put an address in front of the user.
///
/// <see cref="Opened"/> is the only field that decides what the model is told; the rest is there so a failure can say
/// which roads were tried and a success can say which one worked. <see cref="ProcessId"/> is the browser's when the
/// launch produced one, and null when it did not — a pid nobody can stop is not worth reporting as if it could.</summary>
public sealed class BrowserLaunch
{
    public bool Opened { get; set; }
    public int? ProcessId { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>The two things opening a browser does, kept behind a seam so a test can watch the road taken.
///
/// <see cref="Start"/> is the launch itself and <see cref="Exists"/> is how a known browser is found on disk. Both are
/// ordinary functions with the system's own behaviour as the default: a test replaces them, production uses them as
/// they are.</summary>
public sealed class BrowserEnvironment
{
    public Func<ProcessStartInfo, int?> Start { get; set; } = StartProcess;
    public Func<string, bool> Exists { get; set; } = File.Exists;

    private static int? StartProcess(ProcessStartInfo info)
    {
        // The process is disposed at once: what the user needs is the window, not a handle. A launch that hands back
        // no process — Windows does that when the association is served by an already running program — is still a
        // launch, so null here means "opened, pid unknown", not a failure.
        using (var process = Process.Start(info))
        {
            if (process == null) return null;
            try { return process.HasExited ? (int?)null : process.Id; }
            catch (Exception) { return null; }
        }
    }
}

/// <summary>Opens one web address in whatever browser the user has.
///
/// This exists because the tools' whole world is the workspace: a sign-in, a consent screen or a key that is only
/// shown once on a page is something the agent can ask for but cannot do. shelling out to a command would work and
/// was what opencode did, but it drags the command card, a shell and its output into a step that has none of them.
///
/// Three roads, in the order a person would try them: the URL association Windows holds (the user's default browser),
/// <c>cmd /c start</c> which goes through the same association by a different door, and finally the browsers this
/// machine has installed, because a broken association is exactly the failure a user cannot fix from the chat. The
/// address is validated before any of this: only http and https, because <c>file:</c> and <c>javascript:</c> are not a
/// page a user is being asked to sign in to and would be a way to run or reach something else entirely.</summary>
public static class BrowserLauncher
{
    public const int MaxUrlLength = 2048;

    /// <summary>Checks an address before anything is launched. The messages are written for the model to act on: a
    /// refusal that says which argument is wrong and what a correct one looks like is the difference between a retry
    /// and a turn spent apologising.</summary>
    public static bool TryParseUrl(string? text, out Uri url, out string error)
    {
        url = null!;
        error = "";
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            error = "open_browser needs a page to open. Call it again with an absolute http or https address, for example https://example.com/login.";
            return false;
        }
        if (value.Length > MaxUrlLength)
        {
            error = "That address is longer than " + MaxUrlLength + " characters, which is not a page worth opening. Pass the address on its own, without any text around it.";
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            // A space or a quote in an address is a sentence the model meant to say rather than a URL, and control
            // characters would end up on a command line. Refused before they reach a shell.
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '"' || c == '<' || c == '>')
            {
                error = "That address contains a space, a quote, a control character or a bracket, so it is not a URL that can be opened. Pass the address alone, with no spaces and no text around it.";
                return false;
            }
        }
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // A scheme that is not http(s) is named as such — refusing `file:` and `javascript:` is the point of this
            // branch. Anything without "://" is the far more common mistake: a host the model wrote bare.
            error = value.IndexOf("://", StringComparison.Ordinal) >= 0
                ? "Only http and https addresses can be opened in the browser, and that one uses another scheme. Pass the page's http or https address instead."
                : "open_browser needs the full address including its scheme. Call it again with https:// in front, for example https://" + value.Trim('/') + ".";
            return false;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            error = "That is not a valid absolute address, so no page could be opened: " + value;
            return false;
        }
        url = parsed;
        return true;
    }

    /// <summary>Opens the address and returns at once: the call's answer is that the browser was handed the page, not
    /// that the user has finished with it.</summary>
    public static BrowserLaunch Open(Uri url, BrowserEnvironment? environment = null)
    {
        if (url == null) throw new ArgumentNullException(nameof(url));
        environment ??= new BrowserEnvironment();
        var address = url.AbsoluteUri;
        var errors = new List<string>();

        // The association Windows holds — the user's own default browser, extension and profile included.
        var launch = Try("default browser", address, environment, keepProcessId: true,
            () => new ProcessStartInfo(address) { UseShellExecute = true }, errors);
        if (launch != null) return launch;

        // The same association through cmd's own start. It exists because Process.Start answers some shell-execute
        // failures that this does not, and vice versa; the pid here is cmd's and has already exited by the time it
        // could be used, so it is not reported.
        launch = Try("start", address, environment, keepProcessId: false,
            () => new ProcessStartInfo("cmd.exe", "/d /s /c start \"\" \"" + address + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }, errors);
        if (launch != null) return launch;

        // A broken or missing association is the failure the user cannot repair from the chat, so the installed
        // browsers are named and started directly.
        foreach (var candidate in Candidates(environment))
        {
            var path = candidate.Value;
            launch = Try(candidate.Key, address, environment, keepProcessId: true,
                () => new ProcessStartInfo(path, "\"" + address + "\"") { UseShellExecute = false }, errors);
            if (launch != null) return launch;
        }

        return new BrowserLaunch { Opened = false, Url = address, Error = string.Join("; ", errors) };
    }

    /// <summary>Every browser this machine has, in the order worth trying. A candidate is only offered when it is
    /// there: naming a program that does not exist would turn one refusal into a second, worse one.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Candidates(BrowserEnvironment environment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Installed())
        {
            if (!seen.Add(pair.Value)) continue;
            if (environment.Exists(pair.Value)) yield return pair;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Installed()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Pair("chrome", root, "Google", "Chrome", "Application", "chrome.exe");
            yield return Pair("edge", root, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Pair("firefox", root, "Mozilla Firefox", "firefox.exe");
        }
    }

    private static KeyValuePair<string, string> Pair(string name, string root, params string[] parts)
    {
        var path = root;
        foreach (var part in parts) path = Path.Combine(path, part);
        return new KeyValuePair<string, string>(name, path);
    }

    private static BrowserLaunch? Try(string method, string address, BrowserEnvironment environment, bool keepProcessId,
        Func<ProcessStartInfo> start, List<string> errors)
    {
        try
        {
            var pid = environment.Start(start());
            return new BrowserLaunch
            {
                Opened = true,
                ProcessId = keepProcessId ? pid : null,
                Method = method,
                Url = address
            };
        }
        catch (Exception ex)
        {
            errors.Add(method + ": " + ex.Message);
            return null;
        }
    }
}
