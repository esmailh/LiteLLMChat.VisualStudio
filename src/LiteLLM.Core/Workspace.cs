using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>The folders one conversation may read and change: the solution the chat was started in, and any
/// project the user added beside it.
///
/// Every path a model sends is resolved through here, so the rule is written once and read, written and
/// listed the same way — <see cref="PathGuard"/> still does the confinement and the link check, per folder.
/// A solution on its own behaves exactly as it did before this existed: one root, relative paths under it,
/// and labels with no prefix.
///
/// Paths are addressed by name rather than by walking out of the solution. A file in an added project is
/// written <c>OtherApp/src/Foo.cs</c> in both directions — that is how the model names it and how a row
/// labels it — and a path with no such prefix is in the solution, which wins whenever it already has that
/// path of its own.</summary>
public sealed class Workspace
{
    /// <summary>Most project folders one conversation may add. Each one widens what a model can read and
    /// write, so the list is bounded rather than left to grow with every folder the user picks.</summary>
    public const int MaxExtraRoots = 8;

    private readonly List<string> _roots = new List<string>();
    private readonly List<string> _labels = new List<string>();
    /// <summary>Whether the first root is the solution. With a solution open, the extras are everything after
    /// it; with none, <see cref="Primary"/> is itself an added project, so the cap has to count differently or a
    /// chat with no solution could hold one folder more than <see cref="WhyNotAdd"/> allows.</summary>
    private readonly bool _solution;

    public Workspace(string? solutionRoot, IEnumerable<string>? extraRoots = null)
    {
        Add(solutionRoot);
        _solution = _roots.Count > 0;
        if (extraRoots == null) return;
        var added = 0;
        foreach (var root in extraRoots)
        {
            if (added >= MaxExtraRoots) break;
            var before = _roots.Count;
            Add(root);
            if (_roots.Count > before) added++;   // a duplicate is not a folder the cap was spent on
        }
    }

    /// <summary>How many folders beyond the solution this workspace holds.</summary>
    private int ExtraCount => _solution ? Math.Max(0, _roots.Count - 1) : _roots.Count;

    /// <summary>The folders this workspace may touch, the solution first. Empty when nothing is open and
    /// nothing was added, which is the one case where a path is not confined — as it was before.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>The solution, which is where commands run and what the automatic build checks. Null only when
    /// no solution, no open document and no added project could be resolved.</summary>
    public string? Primary => _roots.Count == 0 ? null : _roots[0];

    /// <summary>True once the user has added a project, which is what makes the prefix rule worth explaining
    /// to the model.</summary>
    public bool HasExtra => _roots.Count > 1;

    /// <summary>The name a root is addressed by, or "" when the path is not one of the roots. Its folder name,
    /// made unique among the extras, so two checkouts of the same repository stay distinguishable.</summary>
    public string LabelOf(string? root)
    {
        var normalized = Normalize(root);
        for (var i = 0; i < _roots.Count; i++)
            if (string.Equals(_roots[i], normalized, StringComparison.OrdinalIgnoreCase)) return _labels[i];
        return "";
    }

    /// <summary>The full form of a folder path, or "" when the value is not one. A path copied out of
    /// Explorer arrives quoted, and a trailing separator would make two spellings of the same folder.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var value = path!.Trim().Trim('"').Trim();
        if (value.Length == 0 || !Path.IsPathRooted(value)) return "";
        try
        {
            var full = Path.GetFullPath(value);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // A drive root is "C:\" and must keep its separator; everything else loses it so that two
            // spellings of the same folder compare equal.
            return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return "";
        }
    }

    /// <summary>Resolves a model-supplied path: absolute inside any root, <c>Label/rest</c> inside an added
    /// project, and anything else inside the solution. Throws when the path lands outside every root, naming
    /// the path the model sent rather than the folder that refused it.</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.");
        var value = path!.Trim();

        if (Path.IsPathRooted(value))
        {
            var full = Path.GetFullPath(value);
            var owner = Owner(full) ?? throw Outside(path!);
            return PathGuard.Resolve(owner, full);
        }

        if (Primary == null) return PathGuard.Resolve(null, value);

        // A path that opens with a root's own name means that root: the chat labels every added project that
        // way, and accepting the solution's own folder name too is what makes every label round-trip. The
        // solution still wins a path it genuinely has, so a folder of its own that shares the name stays
        // reachable and a name is never silently redirected away from the file it used to mean.
        var named = NamedRoot(value);
        var namedPrimary = named < 0 && Matches(LabelOf(Primary), Head(value));
        if (named >= 0 || namedPrimary)
        {
            var rest = AfterHead(value);
            var inSolution = Path.GetFullPath(Path.Combine(Primary, value));
            if (rest.Length == 0 || (!File.Exists(inSolution) && !Directory.Exists(inSolution)))
            {
                var root = named >= 0 ? _roots[named] : Primary;
                return rest.Length == 0 ? root : PathGuard.Resolve(root, rest);
            }
        }
        return PathGuard.Resolve(Primary, value);
    }

    /// <summary>The root a resolved path belongs to, longest first so a project added inside another one owns
    /// its own files. Null when the path is outside every root, which the callers report.</summary>
    public string? Owner(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return null;
        string? best = null;
        foreach (var root in _roots)
        {
            if (!PathGuard.IsUnder(root, full!)) continue;
            if (best == null || root.Length > best.Length) best = root;
        }
        return best;
    }

    /// <summary>The form the model and the chat both see: solution-relative for the solution, and
    /// <c>Label/rest</c> for an added project. A path outside every root is returned as it came, because a
    /// label it does not have would be a claim about a folder it is not in.</summary>
    public string Relative(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "";
        var path = full!;
        var owner = Owner(path);
        if (owner == null) return path;
        if (string.Equals(owner, Primary, StringComparison.OrdinalIgnoreCase)) return PathGuard.Relative(owner, path);
        var label = LabelOf(owner);
        if (string.Equals(Normalize(path), Normalize(owner), StringComparison.OrdinalIgnoreCase)) return label;
        return label + "/" + PathGuard.Relative(owner, path).Replace('\\', '/');
    }

    /// <summary>What the model is told about the folders of this turn, or "" when the solution is the only
    /// one — a chat that never added a project reads exactly as it did before. It names paths and the naming
    /// rule only: what a tool may do is still that tool's own check.</summary>
    public string Describe()
    {
        if (!HasExtra) return "";
        var text = new StringBuilder();
        text.Append("The solution is the primary project (").Append(Primary).Append("). The user also added ");
        for (var i = 1; i < _roots.Count; i++)
        {
            if (i > 1) text.Append(i == _roots.Count - 1 ? " and " : ", ");
            text.Append(_labels[i]).Append(" (").Append(Directory.Exists(_roots[i]) ? _roots[i] : _roots[i] + ", currently missing").Append(')');
        }
        text.Append(". Every file tool accepts paths in any of them: a path that begins with an added project's name — ")
            .Append(_labels[1]).Append("/src/Foo.cs — is read from that project, and a path with no such prefix is in the solution, which wins when it already has that path. ")
            .Append("list_files and search_files with no path cover every project. Commands still run from the solution directory, and the automatic build and test check covers the open solution only: to verify an added project, run its own build command.");
        return text.ToString();
    }

    /// <summary>Why <paramref name="path"/> cannot be added as another project, or null when it can. A folder that
    /// is already reachable adds nothing, and one that contains a root would make every listing show that root's
    /// files twice — the second case names the folders to add instead, so the refusal is actionable rather than a
    /// rule the user has to guess.</summary>
    public string? WhyNotAdd(string? path)
    {
        var root = Normalize(path);
        if (root.Length == 0) return "That is not a full folder path.";
        if (!Directory.Exists(root)) return "That folder no longer exists.";
        foreach (var existing in _roots)
        {
            if (string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return "That folder is already part of this conversation.";
            if (PathGuard.IsUnder(existing, root)) return root + " is already reachable as " + LabelOf(existing) + ".";
            if (PathGuard.IsUnder(root, existing)) return root + " contains " + LabelOf(existing) + " — add the folders beside it instead.";
        }
        if (ExtraCount >= MaxExtraRoots) return "A conversation can use at most " + MaxExtraRoots + " added projects.";
        return null;
    }

    private static ArgumentException Outside(string path) =>
        new ArgumentException("Path is outside the open projects: " + path);

    private void Add(string? path)
    {
        var root = Normalize(path);
        if (root.Length == 0) return;
        foreach (var existing in _roots)
            if (string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return;
        _roots.Add(root);
        _labels.Add(UnusedLabel(LabelFor(root)));
    }

    /// <summary>The index of an added project whose label the first segment of <paramref name="path"/> names,
    /// or -1. Only the extras are matched: the solution is what an unprefixed path already means.</summary>
    private int NamedRoot(string path)
    {
        var head = Head(path);
        for (var i = 1; i < _roots.Count; i++)
            if (string.Equals(_labels[i], head, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Head(string path)
    {
        var separator = path.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return separator < 0 ? path : path.Substring(0, separator);
    }

    private static string AfterHead(string path)
    {
        var separator = path.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return separator < 0 ? "" : path.Substring(separator + 1);
    }

    private static string LabelFor(string root)
    {
        var name = Path.GetFileName(root);
        return name.Length > 0 ? name : root;
    }

    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Two projects can genuinely share a folder name — every checkout of a repository is called the
    /// same thing — so the second one is numbered rather than shadowed.</summary>
    private string UnusedLabel(string label)
    {
        var candidate = label;
        for (var n = 2; _labels.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = label + " (" + n + ")";
        return candidate;
    }
}
