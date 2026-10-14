using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32;

namespace LiteLLM.Core;

/// <summary>Resolves the small, fixed set of executables used by Git orchestration without searching the working directory.</summary>
public sealed class WindowsExecutableResolver
{
    private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "git.exe", "gh.exe", "glab.exe"
    };

    public string Resolve(string executableName)
    {
        ValidateName(executableName);
        foreach (var candidate in Candidates(executableName))
        {
            var resolved = ExistingAbsoluteFile(candidate);
            if (resolved != null) return resolved;
        }
        throw new Win32Exception(executableName + " was not found in a trusted installation location or absolute PATH entry.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Allowed.Contains(name) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only git.exe, gh.exe, and glab.exe may be resolved.", nameof(name));
    }

    private static IEnumerable<string> Candidates(string name)
    {
        if (name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var install in RegistryValues(@"SOFTWARE\GitForWindows", "InstallPath"))
            {
                yield return Path.Combine(install, "cmd", name);
                yield return Path.Combine(install, "bin", name);
            }
            foreach (var root in ProgramFilesRoots())
            {
                yield return Path.Combine(root, "Git", "cmd", name);
                yield return Path.Combine(root, "Git", "bin", name);
            }
        }
        else
        {
            foreach (var appPath in RegistryValues(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name, null))
                yield return appPath;
            if (name.Equals("gh.exe", StringComparison.OrdinalIgnoreCase))
                foreach (var root in ProgramFilesRoots()) yield return Path.Combine(root, "GitHub CLI", name);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.None))
        {
            var directory = entry.Trim().Trim('"');
            // Empty and relative PATH components can resolve against a repository-controlled directory.
            if (directory.Length == 0 || !Path.IsPathRooted(directory)) continue;
            yield return Path.Combine(directory, name);
        }
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) && seen.Add(value)) yield return value;
        }
    }

    private static IEnumerable<string> RegistryValues(string subKey, string valueName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string value = null;
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(subKey))
                    value = key == null ? null : key.GetValue(valueName) as string;
            }
            catch (Exception) { }
            if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim().Trim('"');
        }
    }

    private static string ExistingAbsoluteFile(string candidate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) return null;
            var full = Path.GetFullPath(candidate);
            return File.Exists(full) ? full : null;
        }
        catch (Exception) { return null; }
    }
}
