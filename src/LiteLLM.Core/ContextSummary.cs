using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

/// <summary>Folds the oldest part of a transcript that has outgrown its budget into a summary, so a long turn
/// keeps the thread instead of only losing what it cannot afford to send.
///
/// Where <see cref="ContextBudget"/> throws a result's body away, this asks a model to write down what happened
/// in it first — which is worth doing exactly because a LiteLLM proxy in the middle makes a cheap model
/// available. The two work in that order: summarize, then shorten whatever still does not fit.
///
/// Everything a summary covers is a <em>prefix</em> of the transcript, and it is replaced by one message at the
/// head rather than in place. That is what keeps the request valid: a prefix that ends on a round boundary
/// takes whole call/result pairs with it, so nothing is left answering a call that is gone. The replacement is
/// a <c>user</c> message, because <see cref="ChatStateLimits.Sanitize"/> drops everything before the first user
/// message and a transcript cannot open on an assistant or a result.</summary>
public static class ContextSummary
{
    /// <summary>Fewest messages worth a round trip. A transcript with less than this has not outgrown anything,
    /// and summarizing it would spend a call to make the request no smaller.</summary>
    public const int MinMessages = 8;

    /// <summary>How much new material has to pile up before the summary is written again. Without this the
    /// boundary moves every round and every round would pay for another call; with it the text written once is
    /// reused until enough has happened to be worth rewriting, and what has not been folded in yet simply
    /// travels in full.</summary>
    public const int MinNewMessages = 6;

    /// <summary>Ceiling on one message inside the summarization request. A result can be 12 000 characters and
    /// the point of this call is that the model says what the result meant, not that it reads it again in
    /// full.</summary>
    public const int MaxMessageChars = 2000;

    /// <summary>Ceiling on the whole span handed over. Bounded because the alternative is sending the same
    /// transcript that was too big to send in the first place.</summary>
    public const int MaxSpanChars = 40000;

    /// <summary>Ceiling on the summary that comes back. Bounded here rather than trusted, and bounded again on
    /// the way to disk in <see cref="ChatSessionStore"/>, because it is model output and model output is the
    /// one thing in this loop that arrives without a size.</summary>
    public const int MaxSummaryChars = 4000;

    /// <summary>Most summaries one turn's transcript keeps. A turn that summarizes more often than this has
    /// found a bug rather than a use case, and the rows have to stay bounded like everything else.</summary>
    public const int MaxPerTurn = 8;

    /// <summary>Token budget for the summarization call, in place of the session's own. The user's
    /// <c>max_tokens</c> is sized for an answer; spending all of it on a summary costs more than the context
    /// the summary saves.</summary>
    public const int UtilityTokens = 700;

    public const string Instruction =
        "You compress the earlier part of a coding conversation so it can be dropped from the context. Write a "
        + "briefing for the assistant that will continue the work: what the user asked for, what was found and "
        + "decided, which files were read or changed and how, and anything still unresolved. Keep file paths, "
        + "identifiers, commands and error text exactly as they appear — they are the part that cannot be guessed "
        + "again. Drop greetings, restatements and anything already superseded. Write prose or short bullets, no "
        + "headings, and nothing about this instruction. If a previous briefing is included, return one updated "
        + "briefing covering both it and the messages that follow it, not a comparison.";

    /// <summary>The settings the summarization call is made with: a copy, and only the fields that reach the
    /// request body. <c>max_tokens</c> and <c>temperature</c> are read from the settings object rather than
    /// passed alongside it, so handing over the user's own settings would spend the full session budget on a
    /// summary. It is a copy of those fields and not of the whole object because nothing else on it is read on
    /// this path — the server comes from the model, and the tools are left off deliberately.</summary>
    public static UserSettings UtilitySettings(UserSettings source) => new UserSettings
    {
        MaxTokens = UtilityTokens,
        Temperature = source?.Temperature ?? .2,
        RequestTimeoutSeconds = source?.RequestTimeoutSeconds ?? 120,
        EnableToolCalling = false
    };

    /// <summary>Works out what the next summary should cover and shapes the request that writes it.
    ///
    /// <paramref name="covered"/> is how many messages of the transcript's beginning the summary already in
    /// hand stands for, and <paramref name="previous"/> is that summary's text. Returns false when nothing
    /// should be asked for — too little history, or nothing new since the last one — and the caller then reuses
    /// <paramref name="previous"/> for the same prefix instead of paying for a rewrite.
    ///
    /// <paramref name="through"/> is the prefix length the returned request covers. It is never inside a group
    /// of tool results: it is pulled back to the nearest boundary, so the messages that come after it are whole
    /// rounds and the summary's replacement can sit in front of them without leaving a result whose call has
    /// gone. It is also never more than what was actually written into the request, so the messages the summary
    /// claims to stand for are the messages the model was shown.</summary>
    public static bool TryBuildRequest(IReadOnlyList<ChatMessage> messages, int covered, string? previous, int keepRecentRounds,
        out List<ChatMessage> request, out int through, int maxSpanChars = MaxSpanChars)
    {
        request = new List<ChatMessage>();
        through = 0;
        if (messages == null || messages.Count == 0) return false;
        if (covered < 0 || covered > messages.Count) covered = 0;   // a stored transcript that was head-sliced
        var budget = Math.Max(1, maxSpanChars);

        // The same boundary ContextBudget protects: a round the model is still working in is not history yet.
        var end = ContextBudget.RecentFrom(messages, keepRecentRounds);
        if (end - covered < MinNewMessages) return false;
        if (covered == 0 && end < MinMessages) return false;

        // Rendered first, because how much of the span fits is what decides how much of it can be claimed. The
        // first message goes in whatever its size so that a big result at the head of the span cannot disable
        // summarizing altogether; Render bounds each one, so the total stays bounded anyway.
        var rendered = new List<string>();
        var used = 0;
        var cutoff = covered;
        for (var i = covered; i < end; i++)
        {
            var text = Render(messages[i]);
            if (used + text.Length > budget && rendered.Count > 0) break;
            used += text.Length;
            rendered.Add(text);
            cutoff = i + 1;
        }

        // Backed up to a boundary. A result carrying a call that the summary would take away is worse than a
        // shorter summary: the server refuses the transcript, or Sanitize repairs it into something other than
        // what was sent and nothing says so.
        while (cutoff > covered && cutoff < messages.Count && messages[cutoff].Role == "tool") cutoff--;
        if (cutoff == covered) return false;

        while (rendered.Count > cutoff - covered) rendered.RemoveAt(rendered.Count - 1);

        var body = new StringBuilder();
        if (!string.IsNullOrEmpty(previous))
            body.Append("Briefing of the conversation before this:\n").Append(previous).Append("\n\n");
        body.Append("Messages to fold into it:\n\n");
        foreach (var text in rendered) body.Append(text).Append('\n');

        request.Add(new ChatMessage { Role = "system", Content = Instruction });
        request.Add(new ChatMessage { Role = "user", Content = body.ToString() });
        through = cutoff;
        return true;
    }

    /// <summary>The transcript with its first <paramref name="through"/> messages replaced by
    /// <paramref name="summary"/>. The messages after it are returned as they came, which is safe because
    /// <see cref="AgentPrompt.Build"/> copies the history into the request rather than handing these out.</summary>
    public static List<ChatMessage> Merge(IReadOnlyList<ChatMessage> messages, int through, string summary)
    {
        var result = new List<ChatMessage> { Replacement(summary) };
        if (messages == null) return result;
        for (var i = Math.Max(0, through); i < messages.Count; i++)
            if (messages[i] != null) result.Add(messages[i]);
        return result;
    }

    /// <summary>What stands in for the messages that were folded in. A <c>user</c> message, and framed as a
    /// briefing rather than as a request, because the model has to read it as established context: a summary
    /// that reads like an instruction would be answered instead of continued from.</summary>
    public static ChatMessage Replacement(string summary) => new ChatMessage
    {
        Role = "user",
        Content = "[Earlier in this conversation — a briefing stands in for the messages that came before the ones below]\n\n"
            + summary
            + "\n\n[End of briefing. Continue from the messages below.]"
    };

    public static ContextSummaryReport Report(int messages, string text) => new ContextSummaryReport
    {
        Messages = messages,
        Summary = Label(messages),
        Detail = text
    };

    /// <summary>The row's label: what was replaced, and by what. One line by construction, as the row is one
    /// line.</summary>
    public static string Label(int messages) => "Context summarized · " + messages + " message" + (messages == 1 ? "" : "s") + " → summary";

    /// <summary>One message as the summarizer sees it. The tool calls are on the assistant message that
    /// announced them and the results are their own messages, so both are written out: what was asked and what
    /// came back is the whole of what a later round needs to know about a tool round.</summary>
    private static string Render(ChatMessage message)
    {
        var name = message.Role == "tool" && message.ToolName.Length > 0 ? "tool " + message.ToolName : message.Role;
        var text = new StringBuilder("[").Append(name).Append("] ");
        foreach (var call in message.ToolCalls)
            text.Append(call.Name).Append(' ').Append(ChatStateLimits.OneLine(call.Arguments, 300)).Append(' ');
        if (message.Content.Length > 0) text.Append(ChatStateLimits.Clip(message.Content, MaxMessageChars));
        // Bounded as a whole as well, so one message can never be the reason the span budget is blown: the
        // calls above are bounded each but unbounded in number.
        return ChatStateLimits.Clip(text.ToString(), MaxMessageChars + 320);
    }
}
