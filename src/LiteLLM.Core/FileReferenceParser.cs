using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>Recognises source-file references in agent text without touching the file system.</summary>
public static class FileReferenceParser
{
    private static readonly Regex ReferencePattern = new Regex(
        @"(?<![\w@:/\\])(?<value>(?:(?:[A-Za-z]:|\.{1,2}|[\w.@-]+)[\\/](?:[\w.@()+'-]+[\\/])*)?[\w@][\w .@()+'-]*\.(?:cs|csx|vb|fs|fsx|cpp|cxx|cc|c|h|hpp|xaml|xml|json|config|props|targets|sln|csproj|vbproj|fsproj|js|jsx|ts|tsx|css|scss|html|htm|md|txt|sql|py|java|kt|go|rs|sh|ps1|yml|yaml)(?:(?::(?<line>\d+)(?::(?<column>\d+))?)|(?:#L(?<hashline>\d+)(?:C(?<hashcolumn>\d+))?))?)(?![\w/\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<FileReferenceMatch> Find(string text)
    {
        var result = new List<FileReferenceMatch>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (Match match in ReferencePattern.Matches(text))
        {
            FileReference reference;
            if (!IsPartOfUrlOrEmail(text, match) && TryParseMatch(match, out reference))
                result.Add(new FileReferenceMatch(match.Index, match.Length, reference));
        }
        return result;
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
