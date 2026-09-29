using System;
using System.IO;

namespace LiteLLM.Core;

/// <summary>Confines every model-supplied path to the solution root.
///
/// Reading and writing share this one gate on purpose: a path the model cannot read is also a path it cannot write,
/// and there is no second copy of the rule to drift out of step. Without it the model could name any file on the
/// disk, and an edit tool makes that far more serious than a read tool did.</summary>
public static class PathGuard
{
    /// <summary>Resolves a model-supplied path against the workspace root and refuses anything outside it.</summary>
    public static string Resolve(string? root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.");
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(root ?? Environment.CurrentDirectory, path);
        var full = Path.GetFullPath(combined);
        if (root != null && !IsUnder(root, full)) throw new ArgumentException("Path is outside the solution: " + path);
        return full;
    }

    /// <summary>True when <paramref name="full"/> is the root itself or sits below it. Compared case-insensitively,
    /// as Windows paths are, and on a whole path segment so that "src2" is never taken for a child of "src".</summary>
    public static bool IsUnder(string root, string full)
    {
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The solution-relative form of a path, which is what the model and the chat should see.
    /// The root itself has no relative form, so it is named rather than rendered as an empty string.</summary>
    public static string Relative(string? root, string path)
    {
        if (root == null || !IsUnder(root, path)) return path;
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
        var relative = path.Substring(trimmed.Length).TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length > 0) return relative;
        var named = Path.GetFileName(trimmed);
        return named.Length > 0 ? named : trimmed;
    }
}
