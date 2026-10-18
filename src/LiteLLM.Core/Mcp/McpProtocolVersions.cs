using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public enum McpEra
{
    Legacy,
    Modern
}

/// <summary>The two protocol eras this client speaks.
///
/// 2026-07-28 removed the handshake: every request carries the version, client identity and capabilities in its
/// own <c>_meta</c>, and a server may answer with <c>resultType: "input_required"</c> instead of a result. Everything
/// before it is the session-oriented protocol that starts with <c>initialize</c>. Both are spoken because both are
/// in use, and a server's era is detected rather than configured.</summary>
public static class McpProtocolVersions
{
    public const string Modern = "2026-07-28";
    public const string Legacy = "2025-11-25";
    private const string Oldest = "2024-11-05";

    /// <summary>The versions this client knows how to be. A server in between is answered with <see cref="Legacy"/>,
    /// which is wire-compatible with every revision from 2024-11-05 through 2025-11-25 for the methods used here.</summary>
    public static readonly string[] Known = { Modern, Legacy, "2025-06-18", "2025-03-26", Oldest };

    public static bool IsModern(string? version) => string.Equals(version, Modern, StringComparison.Ordinal);

    public static McpEra EraOf(string? version) => IsModern(version) ? McpEra.Modern : McpEra.Legacy;

    /// <summary>What to call a server that answered <paramref name="serverVersion"/> to our offer. An exact known
    /// version is kept; an unknown newer one is treated as modern; an unknown older one is kept when it is still at
    /// least the oldest revision we understand; anything else falls back to our current legacy version.</summary>
    public static string Negotiate(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return Legacy;
        foreach (var known in Known)
            if (string.Equals(known, serverVersion, StringComparison.Ordinal)) return known;

        var comparable = Parse(serverVersion);
        if (comparable == null) return Legacy;
        if (comparable >= Parse(Modern)!.Value) return Modern;
        if (comparable >= Parse(Oldest)!.Value) return serverVersion!;
        return Legacy;
    }

    private static DateTime? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        return DateTime.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : (DateTime?)null;
    }

    public static JObject ClientInfo => new JObject
    {
        ["name"] = "LiteLLM.VisualStudio",
        ["version"] = typeof(McpProtocolVersions).Assembly.GetName().Version?.ToString() ?? "0.0.0"
    };

    public static JObject Capabilities(bool roots, bool sampling, bool elicitation)
    {
        var capabilities = new JObject
        {
            ["tools"] = new JObject { ["listChanged"] = true },
            ["resources"] = new JObject { ["subscribe"] = true, ["listChanged"] = true },
            ["prompts"] = new JObject { ["listChanged"] = true }
        };
        if (roots) capabilities["roots"] = new JObject { ["listChanged"] = true };
        if (sampling) capabilities["sampling"] = new JObject();
        if (elicitation) capabilities["elicitation"] = new JObject();
        return capabilities;
    }

    /// <summary>Per-request metadata for the stateless era. The keys are the ones the 2026-07-28 revision defines;
    /// callers pass the same capabilities every time because there is no session to have negotiated them once.</summary>
    public static JObject Meta(JObject capabilities, string version = Modern) => new JObject
    {
        ["io.modelcontextprotocol/protocolVersion"] = version,
        ["io.modelcontextprotocol/clientInfo"] = ClientInfo,
        ["io.modelcontextprotocol/clientCapabilities"] = capabilities
    };

    /// <summary>The methods that may answer with <c>resultType: "input_required"</c>. The revision restricts the
    /// retry to these three, so a server that asks during any other call is answered with an error instead.</summary>
    public static bool SupportsInputRequired(string method) =>
        method == "tools/call" || method == "prompts/get" || method == "resources/read";
}
