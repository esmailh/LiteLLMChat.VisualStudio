using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiteLLM.Core;

/// <summary>What one attempt at fitting a request produced: the messages to send, the size that was measured on
/// them, and how much of the transcript had to give way. <see cref="Fits"/> is the answer to the only question
/// the caller has — may this be sent? — and <see cref="Describe"/> is what the user is told when it may not.</summary>
public sealed class RequestFit
{
    internal RequestFit(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int limit,
        long size, long toolChars, int passes, int shortened)
    {
        Messages = messages;
        Tools = tools;
        Limit = limit;
        Size = size;
        ToolChars = toolChars;
        Passes = passes;
        Shortened = shortened;
        Fits = size <= limit;
    }

    /// <summary>The request copy to send. Every message is still there, in order: what gave way is the body of an
    /// old tool result, never a message, because a result that answers no call is refused by the server.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }
    public IReadOnlyList<ToolDefinition>? Tools { get; }
    public bool Fits { get; }
    /// <summary>How many trimming passes measuring the request took. One is the common case; the second exists
    /// because the first is budgeted against an estimate and a request can come out a little over it.</summary>
    public int Passes { get; }
    public int Shortened { get; }
    public int Limit { get; }
    public long Size { get; }
    /// <summary>What the tool schemas alone weigh. Counted separately because it is the one part a turn can be
    /// continued without, which is what makes it worth naming when nothing fits.</summary>
    public long ToolChars { get; }
    public long MessageChars => Size - ToolChars;

    /// <summary>Says what the request weighs and what is in it, in the user's terms. A refusal that only says the
    /// budget was exceeded leaves the reader guessing whether the transcript, an attachment or the schemas are the
    /// part to give up.</summary>
    public string Describe() =>
        "Request context is " + Number(Size) + " characters against the " + Number(Limit)
        + "-character budget (" + Number(MessageChars) + " of messages and attachments, " + Number(ToolChars)
        + " of tool schemas). Reduce attachments or start a new conversation. No request was sent.";

    private static string Number(long value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Makes a request fit by the size that is actually there rather than by the size that was planned.
///
/// A turn is budgeted before its request exists: a share of the model's budget is reserved for the system prompt
/// and the tool schemas, and the transcript is folded and shortened to what is left. That estimate is honest but
/// not exact — the system prompt is serialized a second time, a server can add a tool between two rounds, and the
/// JSON around the text costs more than the text — so a request can come out a few thousand characters over the
/// line it was aimed at. The transport's safety bound then refuses the whole turn, which is the worst answer
/// available: the work is done and nothing is sent.
///
/// So the request is measured after it is built, and what it is over by is taken out of the same place the
/// estimate took it from — old tool-result bodies, never messages. Two passes, because the second is budgeted
/// against the measured size rather than the estimated one.</summary>
public static class RequestFitting
{
    /// <summary>How many times one attempt may shorten before it reports what is left. A third pass has nothing
    /// new to measure, and a request that needs one is a request whose schemas are the part that does not fit.</summary>
    public const int MaxPasses = 2;

    public static RequestFit Fit(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int limit,
        int keepRecentRounds = ChatStateLimits.KeepRecentToolRounds)
    {
        var toolChars = tools == null ? 0 : JsonConvert.SerializeObject(tools).Length;
        var best = messages;
        var size = RequestBudget.Size(messages, tools);
        var passes = 0;
        while (size > limit && passes < MaxPasses)
        {
            // What the JSON around the text costs, counted from the messages in hand so the second pass sees the
            // overhead the first one's shortening has already changed.
            var overhead = Math.Max(0L, RequestBudget.Size(best, null) - ContextBudget.TotalChars(best));
            var target = limit - toolChars - overhead;
            if (target <= 0) break;                                   // the instructions and the schemas alone are over
            var trimmed = ContextBudget.Trim(best, (int)Math.Min(int.MaxValue, target), keepRecentRounds);
            passes++;
            var trimmedSize = RequestBudget.Size(trimmed, tools);
            var progress = trimmedSize < size;
            best = trimmed;
            size = trimmedSize;
            if (!progress) break;                                     // nothing old enough, or nothing left to shorten
        }
        return new RequestFit(best, tools, limit, size, toolChars, passes, CountShortened(messages, best));
    }

    /// <summary>How many results lost their body. Counted against the messages that came in rather than summed
    /// over the passes, so a result that was shortened twice is still one result in the row the user reads.</summary>
    private static int CountShortened(IReadOnlyList<ChatMessage> original, IReadOnlyList<ChatMessage> fitted)
    {
        var count = 0;
        for (var i = 0; i < fitted.Count && i < original.Count; i++)
            if (!ReferenceEquals(original[i], fitted[i]) &&
                !string.Equals(original[i].Content, fitted[i].Content, StringComparison.Ordinal))
                count++;
        return count;
    }
}
