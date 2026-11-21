using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One page the browser is willing to be driven: the browser's own name for it, where it is, and the socket
/// a client attaches to. Only <see cref="Type"/> <c>page</c> is a tab a user would recognise — a service worker, a
/// frame and the inspector itself are targets that answer and show nothing.</summary>
public sealed class DevToolsTarget
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string WebSocketUrl { get; set; } = "";
}

/// <summary>Where the browser's DevTools endpoint is, and which of its pages a call means.
///
/// A Chromium browser started with <c>--remote-debugging-port=0</c> picks a free port itself and writes it, with the
/// path of its browser-level socket, into <c>DevToolsActivePort</c> inside the profile it was given. Asking for port
/// 0 is what keeps this from fighting another tool for 9222, and reading that file is the only way to learn the port
/// afterwards. Both halves are plain text and plain JSON, so both are decided here rather than inside the socket.</summary>
public static class DevToolsEndpoint
{
    public const string PortFileName = "DevToolsActivePort";

    /// <summary>The file the port and socket path are written to, inside the profile.</summary>
    public static string PortFile(string profileDirectory) => Path.Combine(profileDirectory, PortFileName);

    /// <summary>Reads what the browser wrote. A missing second line is tolerated because the port is what a client
    /// needs and the socket path is only used to recognise the browser it belongs to.</summary>
    public static bool TryParseActivePort(string? text, out int port, out string browserPath)
    {
        port = 0;
        browserPath = "";
        if (text == null) return false;
        // Chrome writes this file with the platform's own line ending, so both are trimmed rather than split on one.
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0) return false;
        if (!int.TryParse(lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        if (value <= 0 || value > 65535) return false;
        port = value;
        if (lines.Length > 1)
        {
            var second = lines[1].Trim();
            if (second.StartsWith("/", StringComparison.Ordinal)) browserPath = second;
        }
        return true;
    }

    public static string VersionUrl(int port) =>
        "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/json/version";

    public static string ListUrl(int port) =>
        "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/json/list";

    /// <summary>A new tab. The address is a query-string value rather than an argument, so it is escaped: a page with
    /// its own <c>&amp;</c> would otherwise arrive at the browser cut in half.</summary>
    public static string NewTargetUrl(int port, string url) =>
        "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/json/new?" + Uri.EscapeDataString(url);

    /// <summary>Every entry that names a socket. An entry without an id or a socket cannot be driven, and keeping it
    /// would let the choice below return a page that cannot be attached to.</summary>
    public static IReadOnlyList<DevToolsTarget> ParseTargets(JToken? list)
    {
        var targets = new List<DevToolsTarget>();
        if (list is not JArray array) return targets;
        foreach (var item in array)
        {
            if (item is not JObject entry) continue;
            var id = Text(entry, "id");
            var socket = Text(entry, "webSocketDebuggerUrl");
            if (id.Length == 0 || socket.Length == 0) continue;
            targets.Add(new DevToolsTarget
            {
                Id = id,
                Type = Text(entry, "type"),
                Url = Text(entry, "url"),
                Title = Text(entry, "title"),
                WebSocketUrl = socket
            });
        }
        return targets;
    }

    /// <summary>Which page a call means, in the order that keeps a conversation on one tab: the one this session was
    /// last driving, then the address it just asked for (the tab the launch opened), then the first real page rather
    /// than a blank one. Null means there is no tab at all, which is a sentence for the model rather than a guess.</summary>
    public static DevToolsTarget? Choose(IReadOnlyList<DevToolsTarget> targets, string? rememberedId, string? wantedUrl)
    {
        var pages = new List<DevToolsTarget>();
        foreach (var target in targets)
            if (IsPage(target)) pages.Add(target);
        if (pages.Count == 0) return null;

        if (!string.IsNullOrEmpty(rememberedId))
            foreach (var page in pages)
                if (string.Equals(page.Id, rememberedId, StringComparison.Ordinal)) return page;

        if (!string.IsNullOrEmpty(wantedUrl) && Uri.TryCreate(wantedUrl, UriKind.Absolute, out var wanted))
        {
            foreach (var page in pages)
                if (SameAddress(page.Url, wanted)) return page;
            // The launch follows redirects before the first list is read — a sign-in page that bounces to a host of its
            // own is the ordinary case — so the same host is still the tab that was asked for.
            foreach (var page in pages)
                if (SameHost(page.Url, wanted)) return page;
        }

        foreach (var page in pages)
            if (!IsBlank(page.Url)) return page;
        return pages[0];
    }

    private static bool IsPage(DevToolsTarget target) =>
        string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase) &&
        !target.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlank(string url) =>
        url.Length == 0 || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(string candidate, Uri wanted) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
        Uri.Compare(parsed, wanted, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool SameHost(string candidate, Uri wanted) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
        Uri.Compare(parsed, wanted, UriComponents.SchemeAndServer,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string Text(JObject entry, string name) =>
        entry[name]?.Type == JTokenType.String ? entry[name]!.Value<string>() ?? "" : "";
}
