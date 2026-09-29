using System;
using System.Collections.Generic;

namespace LiteLLM.Core;

/// <summary>The single region that differs between two versions of a file, found by trimming the lines they share at
/// the top and the bottom.
///
/// It is what lets an edit reach the editor as a minimal change instead of a whole-file rewrite. Replacing the entire
/// buffer would work, but it would mark every line as modified in the editor's change tracking, collapse the caret
/// and scroll position, and make Ctrl+Z restore a file rather than a line. A patch touching two distant regions
/// reports the span between them: a valid, if not minimal, diff, and still a small edit as far as the editor cares.</summary>
public sealed class TextChange
{
    /// <summary>1-based line the change starts on.</summary>
    public int StartLine = 1;
    public List<string> Removed = new List<string>();
    public List<string> Added = new List<string>();

    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;

    public static TextChange Between(string? oldText, string? newText)
    {
        var before = LineText.Split(LineText.Normalize(oldText));
        var after = LineText.Split(LineText.Normalize(newText));

        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal)) prefix++;

        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix
               && string.Equals(before[before.Count - 1 - suffix], after[after.Count - 1 - suffix], StringComparison.Ordinal)) suffix++;

        var change = new TextChange { StartLine = prefix + 1 };
        change.Removed.AddRange(before.GetRange(prefix, before.Count - prefix - suffix));
        change.Added.AddRange(after.GetRange(prefix, after.Count - prefix - suffix));
        return change;
    }
}
