using System;
using System.IO;

namespace LiteLLM.Core;

/// <summary>Finding a solution's test projects and the assemblies they built, which is what a test run needs
/// and what Visual Studio's automation model does not report in a form worth trusting: the output path differs
/// by project flavour and by target framework, while <c>bin</c> under the project's own directory does not.
///
/// Everything here reads a project file's text rather than evaluating it, so a project whose test reference
/// comes from a shared props file is not recognized. That is a deliberate limit: the cost of missing one is
/// that the check does not run, which is visible, where guessing wrong would run the wrong assembly.</summary>
public static class TestProject
{
    /// <summary>Most test assemblies one run covers. A solution with more test projects than this is beyond
    /// what a per-round check should spend, and the runner says which of them it ran rather than the report
    /// quietly standing for all of them.</summary>
    public const int MaxAssemblies = 4;

    private static readonly string[] Markers =
    {
        "Microsoft.NET.Test.Sdk", "xunit", "nunit", "MSTest.TestAdapter", "Microsoft.Testing.Platform"
    };

    /// <summary>Whether a project file builds tests, judged by the test framework it references. A project
    /// named "…Tests" that references none is an ordinary library and is left alone: what is run has to be
    /// something a test runner can read, and the name is not evidence of that.</summary>
    public static bool IsTestProject(string? projectFileText)
    {
        var text = projectFileText ?? "";
        foreach (var marker in Markers)
            if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>The newest assembly this project built, or null when it has not been built. Newest rather
    /// than a path worked out from the configuration, because the configuration that matters is the one the
    /// build just used and this is the file that build left behind.</summary>
    public static string? FindBuiltAssembly(string? projectFilePath, string? projectFileText)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) return null;
        string directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)); }
        catch (Exception) { return null; }
        if (string.IsNullOrEmpty(directory)) return null;

        var name = AssemblyName(projectFileText, Path.GetFileNameWithoutExtension(projectFilePath));
        if (name.Length == 0) return null;

        try
        {
            var bin = Path.Combine(directory, "bin");
            if (!Directory.Exists(bin)) return null;
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories))
            {
                var time = File.GetLastWriteTimeUtc(file);
                if (newest == null || time > newestTime) { newest = file; newestTime = time; }
            }
            return newest;
        }
        // An unreadable or locked tree costs the test run, not the turn: the caller reports that nothing was
        // verified, which is the truth either way.
        catch (Exception) { return null; }
    }

    /// <summary>The assembly name, which is what the SDK builds and what the runner is given. An explicit
    /// <c>&lt;AssemblyName&gt;</c> wins; otherwise the project file's own name is it.</summary>
    private static string AssemblyName(string? projectFileText, string fallback)
    {
        var text = projectFileText ?? "";
        var open = text.IndexOf("<AssemblyName>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            var start = open + "<AssemblyName>".Length;
            var end = text.IndexOf("</AssemblyName>", start, StringComparison.OrdinalIgnoreCase);
            if (end > start)
            {
                var name = text.Substring(start, end - start).Trim();
                if (name.Length > 0) return name;
            }
        }
        return fallback ?? "";
    }
}
