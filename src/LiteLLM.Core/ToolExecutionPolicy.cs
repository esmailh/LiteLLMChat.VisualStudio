using System;
using System.Collections.Generic;

namespace LiteLLM.Core;

public static class ToolExecutionPolicy
{
    private static readonly HashSet<string> Editing = new HashSet<string>(StringComparer.Ordinal)
    { "apply_patch", "create_file", "replace_string_in_file", "multi_replace_string_in_file", "create_directory" };

    /// <summary>The tools that run something, reach the network, or put a window on the user's desktop. They share the
    /// terminal switch because they are all side effects the same user decision covers: a command, the program it
    /// leaves running in the background, the request it sends to that program, and the browser window the agent opens
    /// and then acts inside.</summary>
    private static readonly HashSet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        "run_powershell", "run_cmd", "run_git", "run_glab", "browser"
    };

    public static string? Refusal(string name, bool toolsEnabled, bool editingEnabled, bool terminalEnabled)
    {
        if (!toolsEnabled) return "Tool calling is disabled.";
        if (Editing.Contains(name) && !editingEnabled) return "File editing is disabled.";
        if (Terminal.Contains(name) && !terminalEnabled) return "Terminal tools are disabled.";
        return null;
    }
}
