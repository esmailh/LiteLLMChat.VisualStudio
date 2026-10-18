using System;

namespace LiteLLM.Core;

public enum McpDecision
{
    /// <summary>Run it without asking.</summary>
    Allow,
    /// <summary>Show the approval card.</summary>
    Ask,
    /// <summary>Do not run it.</summary>
    Deny
}

/// <summary>The approval rule, kept pure so the order can be tested without a window.
///
/// A hint is not a grant: <c>readOnlyHint</c> only helps when the user turned the exception on both globally and for
/// this server, and a tool with <c>destructiveHint</c> or <c>openWorldHint</c> still asks. Allow-all is the existing
/// permission-mode bypass and outranks the lists, exactly as it does for the built-in tools.</summary>
public static class McpPolicy
{
    public static McpDecision Decide(McpToolInfo? tool, bool allowAll, bool autoApproveReadOnly)
    {
        if (tool == null) return McpDecision.Deny;
        var server = tool.Server;

        if (!server.Enabled) return McpDecision.Deny;
        if (Contains(server.DisabledTools, tool.Name)) return McpDecision.Deny;

        if (allowAll) return McpDecision.Allow;
        if (Contains(server.AllowedTools, tool.Name)) return McpDecision.Allow;

        if (autoApproveReadOnly && server.AutoApproveReadOnly &&
            tool.ReadOnlyHint && !tool.DestructiveHint && !tool.OpenWorldHint)
            return McpDecision.Allow;

        return McpDecision.Ask;
    }

    private static bool Contains(System.Collections.Generic.List<string>? names, string name)
    {
        if (names == null) return false;
        foreach (var candidate in names)
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
        return false;
    }
}
