using System;
using System.Collections.Generic;

namespace LiteLLM.Core;

public static class ToolExecutionPolicy
{
    private static readonly HashSet<string> Editing = new HashSet<string>(StringComparer.Ordinal)
    { "apply_patch", "create_file", "replace_string_in_file", "multi_replace_string_in_file", "create_directory" };

    public static string? Refusal(string name, bool toolsEnabled, bool editingEnabled, bool terminalEnabled)
    {
        if (!toolsEnabled) return "Tool calling is disabled.";
        if (Editing.Contains(name) && !editingEnabled) return "File editing is disabled.";
        if ((name == "run_powershell" || name == "run_cmd" || name == "run_git" || name == "run_glab") && !terminalEnabled)
            return "Terminal tools are disabled.";
        return null;
    }
}
