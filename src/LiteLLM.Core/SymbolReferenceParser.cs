using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>A stretch of text that may name a type or a member, and the name to look up for it. The span is what
/// gets underlined; <see cref="Name"/> is what the index is asked about, which can be shorter than the span when a
/// call's parentheses or a type argument list follow the identifier.</summary>
public sealed class SymbolReferenceMatch
{
    public SymbolReferenceMatch(int index, int length, string name)
    {
        Index = index; Length = length; Name = name;
    }

    public int Index { get; }
    public int Length { get; }
    public string Name { get; }
}

/// <summary>Finds the places in a reply that might name a declared type or member.
///
/// It reports candidates, not answers: whether <c>ChatStateLimits.Sanitize</c> is a symbol this solution declares is
/// <see cref="SymbolIndex.Find"/>'s question. Being only a candidate detector is what keeps a reply readable — a
/// name that does not resolve is left as plain text instead of becoming a link that goes nowhere.
///
/// A dotted chain (<c>Owner.Name</c>) is a candidate anywhere, because prose does not use that shape for anything
/// else. A bare name is a candidate only inside inline code, and only when the whole code span is that one name:
/// linking every capitalised word in a sentence would turn ordinary prose into a field of links, and a solution
/// that declares <c>Program</c> would link the English word.</summary>
public static class SymbolReferenceParser
{
    private static readonly Regex DottedPattern = new Regex(
        @"(?<![\w.])(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)(?![\w])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern = new Regex(
        @"^[A-Z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="allowSingle">True for inline code, where a whole span that is one identifier is a reference;
    /// false for prose, where only a dotted chain is specific enough to mean a declaration.</param>
    public static IReadOnlyList<SymbolReferenceMatch> Find(string? text, bool allowSingle)
    {
        var matches = new List<SymbolReferenceMatch>();
        var source = text ?? "";
        if (source.Length == 0) return matches;

        foreach (Match match in DottedPattern.Matches(source))
        {
            if (LooksLikeAddress(source, match.Index, match.Length)) continue;
            matches.Add(new SymbolReferenceMatch(match.Index, match.Length, match.Groups["name"].Value));
        }
        if (allowSingle)
        {
            var single = WholeIdentifier(source);
            if (single != null) matches.Add(single);
        }
        matches.Sort((left, right) => left.Index.CompareTo(right.Index));
        return matches;
    }

    /// <summary>The whole span, once whitespace and call syntax are set aside, is a single identifier — `Foo`,
    /// `Foo()`, `Foo&lt;T&gt;`. Anything longer is a phrase or an expression and is not treated as one name.</summary>
    private static SymbolReferenceMatch? WholeIdentifier(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start >= end) return null;

        var value = text.Substring(start, end - start);
        if (value.EndsWith("()", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 2);
        var generic = value.IndexOf('<');
        if (generic > 0 && value.EndsWith(">", StringComparison.Ordinal)) value = value.Substring(0, generic);
        return IdentifierPattern.IsMatch(value) ? new SymbolReferenceMatch(start, value.Length, value) : null;
    }

    /// <summary>A dotted name inside a URL, an e-mail address or a path is punctuation, not a symbol: those tokens
    /// are already handled as file references, and linking their middle segment would open an unrelated type.</summary>
    private static bool LooksLikeAddress(string text, int index, int length)
    {
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = index + length;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var token = text.Substring(start, end - start);
        return token.IndexOf("://", StringComparison.Ordinal) >= 0
            || token.IndexOf('@') >= 0
            || token.IndexOf('/') >= 0
            || token.IndexOf('\\') >= 0;
    }
}
