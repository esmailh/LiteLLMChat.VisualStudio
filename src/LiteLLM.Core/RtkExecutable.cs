using System;
using System.IO;
using System.ComponentModel;

namespace LiteLLM.Core;

public static class RtkExecutable
{
    // Unlike user commands, this automatic helper never searches PATH or the solution directory.
    public static string Resolve()
    {
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) continue;
            var candidate = Path.Combine(root, "rtk", "rtk.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new Win32Exception("RTK is not installed in Program Files.");
    }
}
