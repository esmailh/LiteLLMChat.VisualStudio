using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>Why one configured server is, or is not, part of the current solution's MCP offer.</summary>
public enum McpInactiveReason
{
    /// <summary>The server is enabled and its scope covers this solution.</summary>
    None,
    /// <summary>The MCP feature switch itself is off.</summary>
    FeatureOff,
    /// <summary>MCP needs tool calling, and that switch is off.</summary>
    ToolCallingOff,
    /// <summary>The user turned this server off.</summary>
    ServerOff,
    /// <summary>The server is scoped to a different solution.</summary>
    OtherSolution
}

/// <summary>One configured server and the reason it is or is not offered.</summary>
public sealed class McpServerAvailability
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public McpInactiveReason Reason { get; set; }
    public bool IsActive => Reason == McpInactiveReason.None;

    public string Describe()
    {
        switch (Reason)
        {
            case McpInactiveReason.None: return "enabled";
            case McpInactiveReason.ToolCallingOff: return "tool calling is off";
            case McpInactiveReason.ServerOff: return "turned off";
            case McpInactiveReason.OtherSolution: return "limited to another solution";
            default: return "MCP is turned off";
        }
    }
}

/// <summary>What MCP looks like for the open solution at one moment.
///
/// The chat draws this as a persistent chip rather than a row in the transcript: the absence of MCP tools is a state,
/// not something that happened during a turn, and a row per turn made the same sentence repeat for every message. The
/// state is configuration, not connectivity — an enabled server that failed to connect still counts as enabled, because
/// the user asked for it and the tooltip is where the failure belongs.
///
/// It lives in Core so the state and its reasons can be tested without a window.</summary>
public sealed class McpAvailability
{
    private McpAvailability(McpAvailabilityState state, IReadOnlyList<McpServerAvailability> servers,
        UserSettings? settings, string? solutionRoot)
    {
        State = state;
        Servers = servers;
        EnabledCount = servers.Count(server => server.IsActive);
        Headline = BuildHeadline(state, servers, settings);
        Detail = BuildDetail(state, servers, Headline);
        Fingerprint = BuildFingerprint(settings, solutionRoot, servers);
    }

    public McpAvailabilityState State { get; }

    /// <summary>Every configured server, in the order the settings hold them.</summary>
    public IReadOnlyList<McpServerAvailability> Servers { get; }

    /// <summary>Servers that are enabled and scoped to the open solution. The chip is "on" when this is at least one.</summary>
    public IReadOnlyList<McpServerConfiguration> EnabledServers =>
        Servers.Where(server => server.IsActive).Select(server => server.Server).ToList();

    public int EnabledCount { get; }
    public int ConfiguredCount => Servers.Count;
    public bool IsEnabled => State == McpAvailabilityState.Enabled;

    /// <summary>The state in one sentence. The window uses it as the first line of the tooltip and adds the live
    /// connection state under it; <see cref="Detail"/> is the same thing for a caller that has none.</summary>
    public string Headline { get; }

    /// <summary>The tooltip: the state in one sentence, then one line per configured server, then where to change it.</summary>
    public string Detail { get; }

    /// <summary>Changes when anything that affects the answer changes. The window compares it instead of re-reading
    /// connection state on every turn.</summary>
    public string Fingerprint { get; }

    public static McpAvailability For(UserSettings? settings, string? solutionRoot)
    {
        var configured = (settings?.McpServers ?? new List<McpServerConfiguration>())
            .Where(server => server != null)
            .ToList();
        var servers = new List<McpServerAvailability>(configured.Count);
        foreach (var server in configured)
            servers.Add(new McpServerAvailability
            {
                Server = server,
                Reason = settings == null ? McpInactiveReason.FeatureOff : ReasonFor(settings, server, solutionRoot)
            });

        McpAvailabilityState state;
        if (settings == null || !settings.EnableMcp || !settings.EnableToolCalling)
            state = McpAvailabilityState.Off;
        else if (servers.Count == 0)
            state = McpAvailabilityState.NotConfigured;
        else if (!servers.Any(server => server.IsActive))
            state = McpAvailabilityState.AllDisabled;
        else
            state = McpAvailabilityState.Enabled;

        return new McpAvailability(state, servers, settings, solutionRoot);
    }

    /// <summary>The one place the precedence lives: a feature switch explains every server, then the server's own
    /// switch, then its solution scope. The order matters — "turned off" on a server while MCP itself is off would
    /// send the user to the wrong control.</summary>
    public static McpInactiveReason ReasonFor(UserSettings settings, McpServerConfiguration server, string? solutionRoot)
    {
        if (!settings.EnableMcp) return McpInactiveReason.FeatureOff;
        if (!settings.EnableToolCalling) return McpInactiveReason.ToolCallingOff;
        if (!server.Enabled) return McpInactiveReason.ServerOff;
        if (!server.AppliesTo(solutionRoot)) return McpInactiveReason.OtherSolution;
        return McpInactiveReason.None;
    }

    private static string BuildHeadline(McpAvailabilityState state, IReadOnlyList<McpServerAvailability> servers,
        UserSettings? settings)
    {
        switch (state)
        {
            case McpAvailabilityState.Off:
                return settings != null && settings.EnableMcp
                    ? "MCP is off because tool calling is turned off in Tools ▸ Options ▸ LiteLLM."
                    : "MCP is turned off in Tools ▸ Options ▸ LiteLLM.";
            case McpAvailabilityState.NotConfigured:
                return "No MCP servers are configured.";
            case McpAvailabilityState.AllDisabled:
                return "No MCP server is enabled for this solution.";
            default:
                var active = servers.Count(server => server.IsActive);
                return active + " MCP server" + (active == 1 ? " is" : "s are") + " enabled for this solution.";
        }
    }

    private static string BuildDetail(McpAvailabilityState state, IReadOnlyList<McpServerAvailability> servers,
        string headline)
    {
        var lines = new List<string> { headline };

        // With the feature off the headline already says so, and a list of servers under it would only repeat the same
        // reason once per row.
        if (state != McpAvailabilityState.Off)
            foreach (var server in servers)
                lines.Add("· " + ChatStateLimits.OneLine(server.Server.Name, 80) + " — " + server.Describe());

        lines.Add("Click to open Dashboard ▸ MCP.");
        return string.Join("\n", lines);
    }

    private static string BuildFingerprint(UserSettings? settings, string? solutionRoot,
        IReadOnlyList<McpServerAvailability> servers)
    {
        var text = new StringBuilder();
        text.Append(settings == null ? '0' : settings.EnableMcp ? '1' : '0');
        text.Append(settings == null ? '0' : settings.EnableToolCalling ? '1' : '0');
        text.Append('\u0002').Append(solutionRoot ?? "");
        foreach (var server in servers)
            text.Append('\n')
                .Append(server.Server.Id).Append('\u0001')
                .Append(server.Server.Name).Append('\u0001')
                .Append(server.Server.Enabled ? '1' : '0').Append('\u0001')
                .Append(server.Server.SolutionRoot);
        return text.ToString();
    }
}

/// <summary>The four answers the chip can give. <see cref="Off"/>, <see cref="NotConfigured"/> and
/// <see cref="AllDisabled"/> all draw the same "off" chip; the distinction lives in the tooltip.</summary>
public enum McpAvailabilityState
{
    Off,
    NotConfigured,
    AllDisabled,
    Enabled
}
