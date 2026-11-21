using System;
using System.IO;

namespace LiteLLM.Core;

/// <summary>The browser the agent drives: its own profile, and the command line that gives it a debugging endpoint.
///
/// The profile is the whole of the feature. A separate directory under <c>%LOCALAPPDATA%</c> means the sign-in the
/// user does in this window is kept for the next turn and the one after a restart, and it also means nothing here can
/// see, change or sign out of the browser they use themselves — Chromium holds one profile per directory, so the two
/// cannot be the same session even if the same person is behind both. Driving the user's own profile instead was
/// refused: it is locked while their Chrome is running, and reaching into it would expose every site they are signed
/// in to rather than the one page this turn was given.
///
/// <c>--remote-debugging-port=0</c> is deliberate as well: a fixed port collides with other tooling and with a second
/// Visual Studio, and the port the browser actually took is read back from the profile.</summary>
public static class BrowserProfile
{
    /// <summary>The folder under the extension's own data, named for what it holds rather than for the tool.</summary>
    public const string FolderName = "browser";

    public static string Directory(string? localAppData = null)
    {
        var root = string.IsNullOrEmpty(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData!;
        return Path.Combine(root, "LiteLLM.VisualStudio", FolderName);
    }

    /// <summary>The command line, url last so a page that takes arguments of its own is not read as one of these.
    ///
    /// The profile path is quoted because a user name with a space in it is the normal case, and the address is quoted
    /// for the same reason. Neither can end the quote early: the address is validated before this is reached, and a
    /// profile path is this machine's own path rather than anything the model sent.</summary>
    public static string Arguments(string profileDirectory, string? url)
    {
        if (string.IsNullOrEmpty(profileDirectory)) throw new ArgumentException("A browser profile directory is required.", nameof(profileDirectory));
        var text = "--remote-debugging-port=0" +
                   " --user-data-dir=\"" + profileDirectory + "\"" +
                   " --no-first-run" +
                   " --no-default-browser-check" +
                   // A profile whose last window was closed by a crash is asked whether to restore it, on every launch,
                   // and the question would sit in front of the page the call is waiting for.
                   " --hide-crash-restore-bubble";
        if (!string.IsNullOrEmpty(url)) text += " --new-window \"" + url + "\"";
        return text;
    }
}
