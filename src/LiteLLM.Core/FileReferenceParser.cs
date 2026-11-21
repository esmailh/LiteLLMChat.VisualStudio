using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>Recognises source-file references in agent text. It reads no file itself; a caller that can say
/// whether a name exists may pass that in, which is what tells a bare file name apart from the words in front
/// of it.</summary>
public static class FileReferenceParser
{
    private static readonly Regex ReferencePattern = new Regex(
        @"(?<![\w@:/\\])(?<value>(?:(?:[A-Za-z]:[\\/](?:[\w.@()+'-][\w .@()+'-]*[\\/])*)|(?:\.{1,2}[\\/](?:[\w.@()+'-]+[\\/])*)|(?:[\w.@-]+[\\/](?:[\w.@()+'-]+[\\/])*))?[\w@][\w .@()+'-]*\.(?:cs|csx|vb|fs|fsx|cpp|cxx|cc|c|h|hpp|xaml|xml|json|config|props|targets|sln|csproj|vbproj|fsproj|js|jsx|ts|tsx|css|scss|html|htm|md|txt|sql|py|java|kt|go|rs|sh|ps1|yml|yaml)(?:(?::(?<line>\d+)(?::(?<column>\d+))?)|(?:#L(?<hashline>\d+)(?:C(?<hashcolumn>\d+))?))?)(?![\w/\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <param name="pathExists">Whether a candidate name is a file, or null when the caller cannot say. A name may
    /// contain a space, so a bare name in a sentence is matched together with the word in front of it — every
    /// multi-word name would otherwise be split. Given a way to ask the disk, the prefix is dropped until the name
    /// is one that exists, so "the check in Foo.cs:12" links <c>Foo.cs</c> and not the sentence around it.</param>
    public static IReadOnlyList<FileReferenceMatch> Find(string text, Func<string, bool>? pathExists = null)
    {
        var result = new List<FileReferenceMatch>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (Match match in ReferencePattern.Matches(text))
        {
            FileReference reference;
            if (IsPartOfUrlOrEmail(text, match) || !TryParseMatch(match, out reference)) continue;
            var path = ExistingPath(reference.Path, pathExists);
            if (string.Equals(path, reference.Path, StringComparison.Ordinal))
            {
                result.Add(new FileReferenceMatch(match.Index, match.Length, reference));
                continue;
            }
            // The match still ends where the name and its line do; only its start moved, which is what makes the
            // link cover the name rather than the words that happened to precede it.
            var shift = reference.Path.Length - path.Length;
            result.Add(new FileReferenceMatch(
                match.Index + shift,
                match.Length - shift,
                new FileReference(path, reference.Line, reference.Column)));
        }
        return result;
    }

    /// <summary>The longest trailing run of words in <paramref name="path"/> that names an existing file, or
    /// <paramref name="path"/> itself when none does. Only a separator-free bare name is shortened: a path that
    /// names a folder is matched from its own first segment, so its words are already the name's.</summary>
    public static string ExistingPath(string path, Func<string, bool>? pathExists)
    {
        if (pathExists == null || string.IsNullOrEmpty(path)) return path;
        if (path.IndexOf(' ') < 0) return path;
        if (path.IndexOf('/') >= 0 || path.IndexOf('\\') >= 0) return path;
        if (Exists(pathExists, path)) return path;
        var words = path.Split(' ');
        for (var i = 1; i < words.Length; i++)
        {
            var candidate = string.Join(" ", words, i, words.Length - i);
            if (Exists(pathExists, candidate)) return candidate;
        }
        return path;
    }

    private static bool Exists(Func<string, bool> pathExists, string path)
    {
        // The check reads the disk, and the text came from a model: an unreadable name is not a file, never an
        // exception thrown out of drawing a reply.
        try { return pathExists(path); }
        catch (Exception) { return false; }
    }

    /// <summary>Parses an entire Markdown link target or inline-code value.</summary>
    public static bool TryParse(string value, out FileReference reference)
    {
        reference = null!;
        if (string.IsNullOrWhiteSpace(value) || LooksLikeUrlOrEmail(value)) return false;
        var trimmed = value.Trim();
        var match = ReferencePattern.Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length && TryParseMatch(match, out reference);
    }

    private static bool TryParseMatch(Match match, out FileReference reference)
    {
        reference = null!;
        var value = match.Groups["value"].Value;
        var suffix = 0;
        int? line = ParsePositive(match.Groups["line"]); if (!line.HasValue) line = ParsePositive(match.Groups["hashline"]);
        int? column = ParsePositive(match.Groups["column"]); if (!column.HasValue) column = ParsePositive(match.Groups["hashcolumn"]);
        if ((match.Groups["line"].Success || match.Groups["hashline"].Success) && !line.HasValue) return false;
        if ((match.Groups["column"].Success || match.Groups["hashcolumn"].Success) && !column.HasValue) return false;
        if (match.Groups["line"].Success) suffix = match.Groups["line"].Length + 1 + (match.Groups["column"].Success ? match.Groups["column"].Length + 1 : 0);
        else if (match.Groups["hashline"].Success) suffix = match.Groups["hashline"].Length + 2 + (match.Groups["hashcolumn"].Success ? match.Groups["hashcolumn"].Length + 1 : 0);
        var path = suffix == 0 ? value : value.Substring(0, value.Length - suffix);
        reference = new FileReference(path, line, column);
        return true;
    }

    private static int? ParsePositive(Group group)
    {
        if (!group.Success) return null;
        int value;
        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0 ? value : (int?)null;
    }

    private static bool IsPartOfUrlOrEmail(string text, Match match)
    {
        var start = match.Index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = match.Index + match.Length;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return LooksLikeUrlOrEmail(text.Substring(start, end - start));
    }

    private static bool LooksLikeUrlOrEmail(string value)
        => value.IndexOf("://", StringComparison.Ordinal) >= 0 || value.IndexOf("mailto:", StringComparison.OrdinalIgnoreCase) >= 0 ||
           Regex.IsMatch(value, @"\b[^\s@]+@[^\s@]+\.[^\s@]+\b", RegexOptions.CultureInvariant);
}

public sealed class FileReference
{
    public FileReference(string path, int? line = null, int? column = null) { Path = path; Line = line; Column = column; }
    public string Path { get; }
    public int? Line { get; }
    public int? Column { get; }
}

public sealed class FileReferenceMatch
{
    public FileReferenceMatch(int index, int length, FileReference reference) { Index = index; Length = length; Reference = reference; }
    public int Index { get; }
    public int Length { get; }
    public FileReference Reference { get; }
}
