using System;

namespace LiteLLM.Core;

/// <summary>An edit that could not be applied, with a message written for the model to act on rather than for a log.
/// The model gets one chance to correct itself before the turn moves on, so the text has to say what to do.</summary>
public sealed class EditFailure : Exception
{
    public EditFailure(string message) : base(message) { }
}

/// <summary>Literal, single-occurrence replacement — the semantics of Copilot's <c>replace_string_in_file</c>:
/// <c>oldString</c> must identify exactly one place, and anything else is refused rather than guessed at.
///
/// Guessing is the dangerous option here. A "close enough" match applies the edit somewhere the model did not look,
/// and the user finds out when the code stops compiling.</summary>
public static class StringReplacer
{
    /// <summary>Replaces the one occurrence of <paramref name="oldString"/>. <paramref name="subject"/> names the
    /// file in any failure message, because a batch edit fails one entry at a time and the reply has to say which.</summary>
    public static string ReplaceOnce(string text, string oldString, string newString, string subject)
    {
        if (oldString.Length == 0) throw new EditFailure("oldString was empty in " + subject + ".");

        var at = Unique(text, oldString);
        if (at >= 0) return Swap(text, at, oldString.Length, newString);
        if (at == Multiple) throw new EditFailure(TooMany(text, oldString, subject));

        // None. A model that read the file through a tool which normalised line endings will send "\n" for a CRLF
        // file, and every retry fails the same way until it gives up and prints the code at the user instead.
        // Retrying once with the endings brought into line is what breaks that loop.
        var aligned = Align(text, oldString);
        if (aligned != oldString)
        {
            var alignedAt = Unique(text, aligned);
            if (alignedAt >= 0) return Swap(text, alignedAt, aligned.Length, Align(text, newString));
            if (alignedAt == Multiple) throw new EditFailure(TooMany(text, aligned, subject));
        }

        throw new EditFailure("No exact match for oldString in " + subject
            + ". Read the file again and copy the target text exactly, including its indentation, or use apply_patch.");
    }

    private const int Multiple = -2;

    /// <summary>The single index of <paramref name="value"/>, -1 when absent, or <see cref="Multiple"/> when it is
    /// not unique. One pass decides all three, so the count and the position can never disagree.</summary>
    private static int Unique(string text, string value)
    {
        var at = text.IndexOf(value, StringComparison.Ordinal);
        if (at < 0) return -1;
        return text.IndexOf(value, at + 1, StringComparison.Ordinal) < 0 ? at : Multiple;
    }

    private static string TooMany(string text, string value, string subject) =>
        Count(text, value) + " matches for oldString in " + subject
        + ". Include more of the surrounding lines so that exactly one place matches.";

    internal static int Count(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Swap(string text, int at, int length, string replacement) =>
        text.Substring(0, at) + replacement + text.Substring(at + length);

    /// <summary>Rewrites <paramref name="value"/>'s line endings to match <paramref name="reference"/>. Both steps go
    /// through "\n" first: doing it in one pass turns an existing "\r\n" into "\r\r\n".</summary>
    internal static string Align(string reference, string value)
    {
        var unified = value.Replace("\r\n", "\n").Replace('\r', '\n');
        return reference.IndexOf('\r') >= 0 ? unified.Replace("\n", "\r\n") : unified;
    }
}
