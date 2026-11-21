using System;

namespace LiteLLM.Core;

/// <summary>The one-time move from the old automatic MCP offer to the current one.
///
/// Sixteen was the old default and every save wrote it down, so a settings file that says sixteen is the default it
/// was created with rather than a choice somebody made. Kept as-is it would go on hiding every tool past a real
/// server's alphabetical first sixteen — Grafana lists sixty-five, and the read tools a question about a dashboard
/// needs are all past the first sixteen — so the value is lifted once. The marker is what makes it once: from the
/// next load on, sixteen means sixteen, and somebody who lowers the cap deliberately keeps it lowered.
///
/// This lives in Core because the options page and the saved settings are two copies of one number, and the move has
/// to be made where they meet, on a rule that can be tested without Visual Studio.</summary>
public static class McpToolCap
{
    /// <summary>The value the old default wrote.</summary>
    public const int LegacyAutomaticTools = 16;

    /// <summary>What to use as the cap: <paramref name="configured"/> once the move has been recorded, and the
    /// current default when it is still the old one.</summary>
    public static int Upgrade(int configured, bool alreadyUpgraded)
    {
        if (alreadyUpgraded) return configured;
        return configured == LegacyAutomaticTools ? McpToolSnapshot.DefaultAutomaticTools : configured;
    }

    /// <summary>A hand-edited file could carry a zero or a negative, and neither means "offer nothing": a value
    /// below one is read as the default rather than as a turn with no MCP tool.</summary>
    public static int Normalize(int configured) => Math.Max(1, configured);
}
