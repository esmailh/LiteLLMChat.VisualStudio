using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>Where a declaration was found: the file, and the line and column of its name. Absolute paths are used
/// only to open the editor; nothing here is ever put into a request to a model.</summary>
public sealed class SymbolLocation
{
    public SymbolLocation(string symbol, string name, string owner, string path, int line, int column, bool isType)
    {
        Symbol = symbol; Name = name; Owner = owner; Path = path; Line = line; Column = column; IsType = isType;
    }

    /// <summary>The key the index knows this declaration by: <c>Owner.Name</c>, or the bare name of a top-level
    /// type. For a member of a nested type both <c>Outer.Inner.Name</c> and <c>Inner.Name</c> are registered.</summary>
    public string Symbol { get; }
    public string Name { get; }
    public string Owner { get; }
    public string Path { get; }
    public int Line { get; }
    public int Column { get; }
    public bool IsType { get; }
}

/// <summary>An index of the C# types and members declared under a solution root, built without Roslyn.
///
/// It answers one question — "the reply mentioned <c>ChatStateLimits.Sanitize</c>; where is it declared?" — and it
/// answers it by reading source files, not by talking to the language service. That keeps it independent of the
/// editor's state and makes it testable in Core, where the Visual Studio project cannot be. It is deliberately a
/// heuristic: a declaration split across lines the regexes do not recognise is missing, and two types with the same
/// name resolve to the first one found. The alternative is a wrong link or no link, and no link is the quieter
/// failure.</summary>
public sealed class SymbolIndex
{
    /// <summary>Bounds a pathological solution without making a normal one lose symbols. The caller stops
    /// enumerating at this count, and the index itself skips a file larger than <see cref="MaxFileBytes"/>.</summary>
    public const int MaxFiles = 5000;
    public const int MaxFileBytes = 1_000_000;

    private static readonly Regex TypePattern = new Regex(
        @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|new|file)\s+)*" +
        @"(?<kind>class|struct|interface|enum|record)\b(?:\s+(?:class|struct))?\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberPattern = new Regex(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|partial|extern|unsafe|new|readonly|ref|required|event|const|volatile)\s+)*" +
        @"(?:[\w<>\[\],.?]+\s+)+(?<name>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*(?:\(|\{|=>|;|=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<SymbolLocation> _locations;
    private readonly Dictionary<string, List<SymbolLocation>> _byKey;

    private SymbolIndex(List<SymbolLocation> locations)
    {
        _locations = locations;
        _byKey = new Dictionary<string, List<SymbolLocation>>(StringComparer.Ordinal);
        foreach (var location in locations)
        {
            Add(location.Symbol, location);
            if (!string.Equals(location.Symbol, location.Name, StringComparison.Ordinal)) Add(location.Name, location);
            // A nested type is also legible by its immediate owner alone, so both `Outer.Inner.Member` and
            // `Inner.Member` find it. The user's reply may use either.
            if (location.Owner.Length > 0)
            {
                var immediate = location.Owner.Substring(location.Owner.LastIndexOf('.') + 1);
                var key = immediate + "." + location.Name;
                if (!string.Equals(key, location.Symbol, StringComparison.Ordinal)) Add(key, location);
            }
        }
    }

    public int Count => _locations.Count;

    /// <summary>Reads the given files. A path that cannot be read is skipped: one locked file must not cost the
    /// whole index, and the link that depended on it simply does not appear.</summary>
    public static SymbolIndex Build(IEnumerable<string>? files)
    {
        var locations = new List<SymbolLocation>();
        foreach (var file in files ?? Enumerable.Empty<string>())
        {
            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileBytes) continue;
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                continue;
            }
            Scan(file, lines, locations);
        }
        locations.Sort((left, right) =>
        {
            var byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            if (byPath != 0) return byPath;
            var byLine = left.Line.CompareTo(right.Line);
            return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
        });
        return new SymbolIndex(locations);
    }

    /// <summary>The declaration a reply's symbol refers to, or null when this solution does not declare it. A
    /// dotted name is tried exactly first (<c>Outer.Inner</c>), then by its immediate owner
    /// (<c>Inner.Member</c>). It is deliberately not reduced to its final segment: the reply's `Task.Run` must not
    /// open the first class in the solution that happens to have a <c>Run</c>, and a bare <c>Run</c> still resolves
    /// through the single-name path below.</summary>
    public SymbolLocation? Find(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var value = (reference ?? "").Trim();
        if (_byKey.TryGetValue(value, out var exact) && exact.Count > 0) return Pick(exact);

        var dot = value.LastIndexOf('.');
        if (dot <= 0) return PickFrom(value);
        var owner = value.Substring(0, dot);
        var name = value.Substring(dot + 1);
        var ownerSimple = owner.Substring(owner.LastIndexOf('.') + 1);
        return _byKey.TryGetValue(ownerSimple + "." + name, out var owned) && owned.Count > 0 ? Pick(owned) : null;
    }

    private SymbolLocation? PickFrom(string key) =>
        _byKey.TryGetValue(key, out var list) && list.Count > 0 ? Pick(list) : null;

    /// <summary>A type is the likelier meaning of an ambiguous bare name, and it is the target that opens at the
    /// class rather than at a method that happens to share the name.</summary>
    private static SymbolLocation Pick(List<SymbolLocation> list) =>
        list.FirstOrDefault(l => l.IsType) ?? list[0];

    private void Add(string key, SymbolLocation location)
    {
        if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<SymbolLocation>();
        list.Add(location);
    }

    private sealed class OpenType
    {
        public string Name = "";
        public string Qualified = "";
        public int BodyDepth;
    }

    private static void Scan(string path, string[] lines, List<SymbolLocation> found)
    {
        var stack = new List<OpenType>();
        var depth = 0;
        var blockComment = false;
        var pendingName = "";
        var pendingQualified = "";
        var awaitingBrace = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var code = Strip(lines[i], ref blockComment);
            var startDepth = depth;
            var ownerQualified = stack.Count > 0 ? stack[stack.Count - 1].Qualified : "";
            var ownerSimple = stack.Count > 0 ? stack[stack.Count - 1].Name : "";
            var type = TypePattern.Match(code);
            OpenType? push = null;

            if (type.Success)
            {
                var name = type.Groups["name"].Value;
                var qualified = ownerQualified.Length == 0 ? name : ownerQualified + "." + name;
                found.Add(new SymbolLocation(qualified, name, ownerSimple, path, i + 1, type.Groups["name"].Index + 1, true));
                if (code.IndexOf('{') >= 0)
                    push = new OpenType { Name = name, Qualified = qualified };
                else if (code.IndexOf(';') < 0)
                {
                    // `class Foo : Bar` puts the brace on a later line; wait for it rather than losing the span.
                    pendingName = name; pendingQualified = qualified; awaitingBrace = true;
                }
            }
            else if (awaitingBrace && code.IndexOf('{') >= 0)
            {
                push = new OpenType { Name = pendingName, Qualified = pendingQualified };
                awaitingBrace = false;
            }
            else if (awaitingBrace && code.IndexOf(';') >= 0)
            {
                // A positional record (`record Point(int X);`) has no body to span.
                awaitingBrace = false;
            }
            else if (stack.Count > 0 && startDepth == stack[stack.Count - 1].BodyDepth)
            {
                var member = MemberPattern.Match(code);
                if (member.Success)
                {
                    var name = member.Groups["name"].Value;
                    var qualified = stack[stack.Count - 1].Qualified + "." + name;
                    found.Add(new SymbolLocation(qualified, name, stack[stack.Count - 1].Qualified, path, i + 1, member.Groups["name"].Index + 1, false));
                }
            }

            depth += DepthDelta(code);
            if (push != null)
            {
                push.BodyDepth = depth;
                stack.Add(push);
            }
            while (stack.Count > 0 && depth < stack[stack.Count - 1].BodyDepth)
                stack.RemoveAt(stack.Count - 1);
        }
    }

    private static int DepthDelta(string code)
    {
        var delta = 0;
        foreach (var c in code)
        {
            if (c == '{') delta++;
            else if (c == '}') delta--;
        }
        return delta;
    }

    /// <summary>Blanks comments and string/character literals, keeping every character's index, so a brace or the
    /// word `class` inside a comment cannot move the span and the column reported for a real declaration is the
    /// column a person sees. Block-comment state carries to the next line.</summary>
    private static string Strip(string line, ref bool blockComment)
    {
        var chars = line.ToCharArray();
        var i = 0;
        if (blockComment)
        {
            var end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0) { Blank(chars, 0, chars.Length); return new string(chars); }
            Blank(chars, 0, end + 2);
            i = end + 2;
            blockComment = false;
        }
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                Blank(chars, i, line.Length - i);
                break;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { Blank(chars, i, line.Length - i); blockComment = true; break; }
                Blank(chars, i, end + 2 - i);
                i = end + 2;
                continue;
            }
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                var start = i;
                i += 2;
                while (i < line.Length)
                {
                    if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                    if (line[i] == '"') { i++; break; }
                    i++;
                }
                Blank(chars, start, Math.Min(i, line.Length) - start);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                var start = i;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (line[i] == c) { i++; break; }
                    i++;
                }
                Blank(chars, start, Math.Min(i, line.Length) - start);
                continue;
            }
            i++;
        }
        return new string(chars);
    }

    private static void Blank(char[] chars, int start, int length)
    {
        var end = Math.Min(chars.Length, start + length);
        for (var i = Math.Max(0, start); i < end; i++) chars[i] = ' ';
    }
}
