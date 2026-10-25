using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

/// <summary>What to do with a streamed answer the server closed before it was finished.
///
/// A reverse proxy that reaches its read timeout, a restarted worker and a link that drops all end the same way:
/// the response headers arrived, part of the answer did too, and then the socket was reset. That is not a wrong
/// answer, it is an unfinished one, and the same turn can simply be asked to go on — carrying what has already
/// been written as the assistant message, because a model that cannot see the beginning of its own answer
/// repeats it.</summary>
public static class StreamContinuation
{
    /// <summary>The message that follows the partial answer. It is a user message rather than a system one
    /// because the request already carries its system prompt, and a second system prompt in the middle of a
    /// transcript is refused by some OpenAI-compatible servers.</summary>
    public const string Instruction =
        "Your previous answer was cut off before it was finished. Continue it from the exact character where it "
        + "stopped. Do not repeat anything already written, do not restate the beginning, and do not add a "
        + "preamble — output only the continuation of the answer.";

    /// <summary>An answer can be continued once it has text and no tool call has been announced. A tool call
    /// arrives in pieces, and half of one is not a call that can be run, so that case is reported rather than
    /// guessed at.</summary>
    public static bool CanContinue(int contentLength, bool toolCallsSeen) => contentLength > 0 && !toolCallsSeen;

    /// <summary>The request for the continuation: the turn as it was sent, the answer so far as the assistant's
    /// own message, and the instruction to go on. Built from the original turn every time, not from the last
    /// continuation, so a second resume still describes the answer as one piece.</summary>
    public static IReadOnlyList<ChatMessage> Messages(IReadOnlyList<ChatMessage> request, string contentSoFar)
    {
        var messages = new List<ChatMessage>(request.Count + 2);
        messages.AddRange(request);
        messages.Add(new ChatMessage { Role = "assistant", Content = contentSoFar });
        messages.Add(new ChatMessage { Role = "user", Content = Instruction });
        return messages;
    }
}

/// <summary>Removes the start of a continuation that only repeats the end of the answer already shown.
///
/// Most models continue where they were told to, but the ones that do not begin by repeating the last
/// sentence — and a sentence starts before the answer's last character. The repeat therefore has to be found
/// against every suffix of the answer, not just its final character: while the newly arrived text is still a
/// prefix of some suffix, it may yet be that repeat, so it is held; once it runs past the suffix it was
/// matching, the suffix is dropped and only what follows is shown. That has to happen as the text arrives,
/// because the deltas have already been drawn: a character shown twice cannot be taken back.
///
/// Only the tail of the answer can be a junction, so only the tail is compared. A match shorter than
/// <see cref="MinOverlap"/> is left alone — trimming a handful of coincidental characters would damage more
/// text than it saves.</summary>
public sealed class StreamContinuationFilter
{
    private const int Window = 400;
    private const int MinOverlap = 8;

    private readonly string _tail;
    private readonly StringBuilder _held = new StringBuilder();
    private bool _settled;

    public StreamContinuationFilter(string contentSoFar)
    {
        _tail = contentSoFar.Length <= Window ? contentSoFar : contentSoFar.Substring(contentSoFar.Length - Window);
    }

    /// <summary>What of <paramref name="delta"/> may be shown. Empty while the delta could still turn out to be
    /// the beginning of a repeated sentence.</summary>
    public string Accept(string delta)
    {
        if (_settled) return delta;
        _held.Append(delta);
        if (_held.Length == 0) return "";
        var (repeat, stillMatching) = Measure();
        // The hold is still inside a suffix of the answer: it may yet be a repeat, so nothing is shown.
        if (stillMatching) return "";
        _settled = true;
        return Release(repeat);
    }

    /// <summary>Everything still held, for the end of the stream. A continuation that was nothing but a repeat
    /// releases as empty — that is what this class exists to drop; one that ended inside a repeated sentence
    /// and is long enough to be one is dropped too.</summary>
    public string Flush()
    {
        if (_settled) return "";
        _settled = true;
        var (repeat, stillMatching) = Measure();
        if (repeat >= MinOverlap) return Release(repeat);
        if (stillMatching && _held.Length >= MinOverlap) return "";
        return Release(0);
    }

    /// <summary>How much of the answer the held text repeats, and whether it is still inside that repeat.
    /// A repeat is a suffix of the answer, so every suffix is compared; the longest one the hold gets past is
    /// the repeat to drop. A hold that is still a prefix of any suffix has not decided yet.</summary>
    private (int Repeat, bool StillMatching) Measure()
    {
        var repeat = 0;
        var stillMatching = false;
        for (var start = 0; start < _tail.Length; start++)
        {
            var suffix = _tail.Length - start;
            var compare = Math.Min(suffix, _held.Length);
            var i = 0;
            while (i < compare && _tail[start + i] == _held[i]) i++;
            if (i < compare) continue;
            if (_held.Length <= suffix) stillMatching = true;
            else if (suffix > repeat) repeat = suffix;
        }
        return (repeat, stillMatching);
    }

    private string Release(int repeat)
    {
        var skip = repeat >= MinOverlap ? repeat : 0;
        var text = _held.ToString(skip, _held.Length - skip);
        _held.Clear();
        return text;
    }
}
