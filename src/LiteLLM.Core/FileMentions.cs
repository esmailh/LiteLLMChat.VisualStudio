using System;

namespace LiteLLM.Core;

/// <summary>Parses the @file token under the caret. Kept UI-independent so composer behavior is deterministic and testable.</summary>
public static class FileMentions
{
    public static bool TryGetFilter(string text, int caret, out int start, out int length, out string filter)
    {
        start = length = 0;
        filter = "";
        if (string.IsNullOrEmpty(text) || caret < 1 || caret > text.Length) return false;

        var at = caret - 1;
        while (at >= 0 && !char.IsWhiteSpace(text[at])) at--;
        at++;
        if (at >= caret || text[at] != '@') return false;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1])) return false;

        start = at;
        length = caret - at;
        filter = text.Substring(at + 1, caret - at - 1);
        return filter.IndexOfAny(new[] { '\r', '\n' }) < 0;
    }

    public static string RemoveToken(string text, int start, int length)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (start < 0 || length < 0 || start + length > text.Length) throw new ArgumentOutOfRangeException();
        return text.Remove(start, length);
    }
}
