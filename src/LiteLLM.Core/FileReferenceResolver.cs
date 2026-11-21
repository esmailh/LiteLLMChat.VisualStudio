using System;
using System.Collections.Generic;
using System.IO;

namespace LiteLLM.Core;

/// <summary>Turns the file name a reply carries into the file it means.
///
/// A model names a file, not a path: "StoreGroupService.cs", not "src/StoreGrouper/Services/StoreGroupService.cs".
/// Resolving such a name against the solution root alone answers "not found" for every file that sits in a folder —
/// which is nearly all of them — so a link could be drawn and still fail when clicked. The names come from one
/// reading of the conversation's folders, which is what makes the answer a lookup: a reply is drawn again on every
/// streamed chunk, and asking the disk each time would be paid for by the typing.
///
/// Two files of the same name are a real possibility, and the rule is fixed rather than clever: the shortest
/// workspace path wins, and an alphabetical tie-break keeps two runs of the same reply agreeing with each other.
/// </summary>
public sealed class FileReferenceResolver
{
    private readonly Workspace _workspace;
    /// <summary>Workspace-relative and '/'-separated, which is how the chat shows a path.</summary>
    private readonly List<string> _paths;
    /// <summary>The same paths by file name, so a bare name is a dictionary lookup rather than a scan.</summary>
    private readonly Dictionary<string, List<string>> _byName;
    /// <summary>Answers already given, so a reply that is redrawn on every streamed chunk costs no disk read for
    /// a name it has already linked. A negative answer is kept too: a name nothing matched stays unanswered until
    /// the list itself is replaced.</summary>
    private readonly Dictionary<string, string?> _answers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new object();

    /// <param name="paths">The files the conversation can reach, workspace-relative and '/'-separated — what
    /// <see cref="Workspace.Relative"/> returns. A rooted path here is ignored: the list is a list of names.</param>
    public FileReferenceResolver(Workspace workspace, IEnumerable<string>? paths)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _paths = new List<string>();
        _byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths ?? Array.Empty<string>())
        {
            var path = Clean(raw);
            if (path.Length == 0 || Path.IsPathRooted(path)) continue;
            _paths.Add(path);
            var name = NameOf(path);
            if (!_byName.TryGetValue(name, out var group))
                _byName[name] = group = new List<string>();
            group.Add(path);
        }
    }

    /// <summary>How many names were offered. Zero means the folders could not be read, and the click that follows
    /// says the file was not found rather than pretending nothing was there to look at.</summary>
    public int Count => _paths.Count;

    /// <summary>Whether this name is a file the conversation can reach. The parser asks this to tell a bare name
    /// apart from the words in front of it, which is what keeps "the check in Foo.cs:12" from linking the prose.</summary>
    public bool Exists(string? candidate) => Find(candidate) != null;

    /// <summary>The full path of the file the name means, or null when nothing in the conversation's folders does.
    /// An absolute path must exist as written: the model named one place, and a same-named file elsewhere is a
    /// different file, so it is never substituted.</summary>
    public string? Find(string? candidate)
    {
        var value = Clean(candidate);
        if (value.Length == 0) return null;

        lock (_gate)
        {
            if (_answers.TryGetValue(value, out var known)) return known;
        }
        var found = Resolve(value);
        lock (_gate)
        {
            _answers[value] = found;
        }
        return found;
    }

    private string? Resolve(string value)
    {
        // The name as written, when it already points at a file: a path the model got right costs one check.
        var direct = FullPath(value);
        if (direct != null) return direct;
        if (Path.IsPathRooted(value)) return null;

        // A bare name is answered from the dictionary; a path written from part-way down has to be compared
        // against every name, because which prefix it is missing is exactly what is unknown.
        var bare = value.IndexOf('/') < 0;
        IReadOnlyList<string> candidates = bare
            ? _byName.TryGetValue(value, out var group) ? group : (IReadOnlyList<string>)Array.Empty<string>()
            : _paths;

        string? best = null;
        var bestSegments = int.MaxValue;
        foreach (var relative in candidates)
        {
            if (!bare && !EndsWithSegment(relative, value)) continue;
            var segments = Segments(relative);
            if (best != null && (segments > bestSegments ||
                                 (segments == bestSegments && string.Compare(relative, best, StringComparison.OrdinalIgnoreCase) >= 0)))
                continue;
            best = relative;
            bestSegments = segments;
        }
        return best == null ? null : FullPath(best);
    }

    /// <summary>True when <paramref name="path"/> ends with <paramref name="tail"/> on a folder boundary, so
    /// "Foo.cs" is never taken for the end of "BarFoo.cs".</summary>
    private static bool EndsWithSegment(string path, string tail) =>
        string.Equals(path, tail, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase);

    private static int Segments(string path)
    {
        var count = 0;
        foreach (var c in path)
            if (c == '/') count++;
        return count;
    }

    private string? FullPath(string path)
    {
        try
        {
            var full = _workspace.Resolve(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            // Outside every root, unreadable, or not a path at all. The text came from a model and the click came
            // from the user: the answer is "no file", never an exception thrown out of drawing a reply.
            return null;
        }
    }

    private static string NameOf(string path)
    {
        var at = path.LastIndexOf('/');
        return at >= 0 ? path.Substring(at + 1) : path;
    }

    private static string Clean(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "";
        var value = candidate!.Trim().Trim('"').Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value.Substring(2);
        return value;
    }
}
