using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>One configured server the current message named, that this turn cannot offer.</summary>
public sealed class McpDisabledMention
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public McpInactiveReason Reason { get; set; }

    /// <summary>Every token in the message that resolved to this server, without its leading <c>#</c>.</summary>
    public List<string> Tokens { get; } = new List<string>();

    /// <summary>The transcript row. It names the server and says why, because a name the user typed that silently
    /// resolves to nothing reads as a server that does not exist.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The part that hangs under the row: which control turns it on, and that nothing was retried.</summary>
    public string Detail { get; set; } = "";

    /// <summary>The system-prompt sentence. It tells the model to say the server is unavailable rather than trying
    /// again or substituting a different tool.</summary>
    public string Hint { get; set; } = "";
}

/// <summary>A message that names a server the user configured but this turn cannot offer.
///
/// <see cref="McpToolSelection"/> resolves against the tools that were actually offered, so a server that is turned
/// off is invisible to it. This check fills that gap: the tokens are the same grammar, but they are matched against
/// configured servers instead of connected tools. It never connects anything — the user asked, and the answer is a
/// sentence, not a retry.</summary>
public static class McpDisabledMentions
{
    public static IReadOnlyList<McpDisabledMention> Find(UserSettings? settings, string? solutionRoot, string? message)
    {
        var found = new List<McpDisabledMention>();
        if (settings == null || string.IsNullOrWhiteSpace(message)) return found;

        var mentions = McpToolMentions.Parse(message);
        if (mentions.Count == 0) return found;

        var availability = McpAvailability.For(settings, solutionRoot);
        if (!availability.Servers.Any(server => !server.IsActive)) return found;

        var byId = new Dictionary<string, McpDisabledMention>(StringComparer.Ordinal);
        foreach (var mention in mentions)
        {
            foreach (var server in availability.Servers)
            {
                if (server.IsActive || !Matches(mention.Text, server.Server)) continue;
                // An active server with the same name answers the token; the selection path will report it, and
                // saying "turned off" about a different server the user did not mean would be worse than silence.
                if (availability.Servers.Any(other => other.IsActive && Matches(mention.Text, other.Server))) continue;

                if (!byId.TryGetValue(server.Server.Id, out var entry))
                {
                    entry = Describe(server.Server, server.Reason);
                    byId[server.Server.Id] = entry;
                    found.Add(entry);
                }
                if (!entry.Tokens.Contains(mention.Text, StringComparer.Ordinal)) entry.Tokens.Add(mention.Text);
            }
        }
        return found;
    }

    /// <summary>The selection hint with the unavailable servers appended. Keeping one hint means the request builder
    /// has one block about MCP tools, not two that can contradict each other.</summary>
    public static string? Combine(string? selectionHint, IReadOnlyList<McpDisabledMention>? mentions)
    {
        if (mentions == null || mentions.Count == 0) return selectionHint;
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(selectionHint)) text.Append(selectionHint).Append('\n');
        text.Append("Unavailable MCP servers\n");
        foreach (var mention in mentions.Take(8))
            text.Append("- ").Append(mention.Hint).Append('\n');
        return text.ToString().TrimEnd();
    }

    /// <summary>True for a token that names this server: its own name or slug, a server-qualified tool, or the
    /// exposed <c>mcp__server__tool</c> form. A bare tool name is not evidence of a server, so it never matches.</summary>
    private static bool Matches(string token, McpServerConfiguration server)
    {
        if (string.IsNullOrEmpty(token) || server == null) return false;
        var name = server.Name ?? "";
        var slug = McpToolNaming.Slug(name);
        if (Same(token, name) || Same(token, slug)) return true;
        if (Qualified(token, name) || Qualified(token, slug)) return true;
        return token.StartsWith("mcp__" + slug + "__", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string left, string right) =>
        left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Qualified(string token, string prefix) =>
        prefix.Length > 0 && token.Length > prefix.Length &&
        (token[prefix.Length] == '.' || token[prefix.Length] == '/') &&
        token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static McpDisabledMention Describe(McpServerConfiguration server, McpInactiveReason reason)
    {
        var name = ChatStateLimits.OneLine(string.IsNullOrWhiteSpace(server.Name) ? "(unnamed server)" : server.Name, 80);
        var mention = new McpDisabledMention { Server = server, Reason = reason };
        switch (reason)
        {
            case McpInactiveReason.FeatureOff:
                mention.Summary = "MCP · '" + name + "' is configured, but MCP is turned off, so this turn has no MCP tools.";
                mention.Detail = "Turn MCP on in Tools ▸ Options ▸ LiteLLM, or click the MCP chip to open Dashboard ▸ MCP.";
                break;
            case McpInactiveReason.ToolCallingOff:
                mention.Summary = "MCP · '" + name + "' is configured, but tool calling is off.";
                mention.Detail = "Turn tool calling on in Tools ▸ Options ▸ LiteLLM.";
                break;
            case McpInactiveReason.OtherSolution:
                mention.Summary = "MCP · '" + name + "' is limited to another solution, so its tools are not offered this turn.";
                mention.Detail = (string.IsNullOrWhiteSpace(server.SolutionRoot)
                                     ? "It is limited to another solution."
                                     : "It is scoped to " + server.SolutionRoot + ".") +
                                 " Change its solution scope in Dashboard ▸ MCP.";
                break;
            default:
                mention.Summary = "MCP · '" + name + "' is turned off for this solution, so its tools are not offered this turn.";
                mention.Detail = "Enable it in Dashboard ▸ MCP (or click the MCP chip in the chat). Nothing was retried; " +
                                 "the server is used from the next message once it is on.";
                break;
        }
        mention.Hint = "The user's message names the MCP server '" + name + "', whose tools are not offered this turn " +
                       "because " + ReasonText(reason) + ". Say plainly that it is unavailable and why; do not retry, " +
                       "do not call a substitute tool, and do not claim that one ran. The user can enable it in " +
                       "Dashboard ▸ MCP.";
        return mention;
    }

    private static string ReasonText(McpInactiveReason reason)
    {
        switch (reason)
        {
            case McpInactiveReason.FeatureOff: return "MCP is turned off";
            case McpInactiveReason.ToolCallingOff: return "tool calling is off";
            case McpInactiveReason.OtherSolution: return "it is limited to another solution";
            default: return "the server is turned off";
        }
    }
}
