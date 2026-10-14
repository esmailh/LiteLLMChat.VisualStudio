using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiteLLM.Core;

/// <summary>Keeps one request inside a size a model can still attend to. A transcript is not shortened by
/// dropping messages, because every announced tool call has to keep the result that answers it or the server
/// refuses the whole request — that is the rule <see cref="ChatStateLimits.Sanitize"/> enforces by dropping
/// the pair. So an old result keeps its place and loses only its body: the part that is expensive, and the
/// part the model has already read and acted on.</summary>
public static class ContextBudget
{
    /// <summary>The transcript to send. Results belonging to rounds older than
    /// <paramref name="keepRecentRounds"/> may come back as a one-line note instead of their content.
    ///
    /// Nothing here mutates the caller's messages: that list is what gets saved and shown, and it keeps every
    /// result in full. Messages that were not shortened are returned as they came, which is safe because
    /// <see cref="AgentPrompt.Build"/> copies the history into the request rather than handing these out.</summary>
    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int maxChars,
        int keepRecentRounds = ChatStateLimits.KeepRecentToolRounds) => Trim(messages, maxChars, keepRecentRounds, out _);

    /// <summary>As above, and reports how many results were shortened, so the loop can say so rather than
    /// quietly dropping what it could not afford to send.</summary>
    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int maxChars, int keepRecentRounds,
        out int shortened)
    {
        shortened = 0;
        var result = new List<ChatMessage>();
        if (messages == null) return result;

        var budget = Math.Max(1, maxChars);
        var protectedFrom = RecentFrom(messages, keepRecentRounds);
        var arguments = ArgumentsById(messages);

        // Walked backwards from the newest message, because that is the end of the conversation the request is
        // about: the budget is spent there first, and what it cannot pay for is the beginning.
        var total = 0;
        var shortenedAt = new Dictionary<int, string>();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message == null) continue;
            total += Cost(message);
            if (total <= budget || i >= protectedFrom || message.Role != "tool" || message.Content.Length == 0) continue;
            var note = Note(message, arguments);
            shortenedAt[i] = note;
            total -= message.Content.Length;
            total += note.Length;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message == null) continue;
            if (!shortenedAt.TryGetValue(i, out var note)) { result.Add(message); continue; }
            var copy = ChatStateLimits.CopyMessage(message);
            copy.Content = note;
            result.Add(copy);
            shortened++;
        }
        return result;
    }

    /// <summary>The index the newest <paramref name="keepRecentRounds"/> tool-calling rounds begin at. From
    /// there on is what the model is working on right now. A transcript with fewer rounds than that is all
    /// recent, so none of it is shortened. Public because <see cref="ContextSummary"/> has to agree with this
    /// about which rounds are recent: one that summarized a round this refuses to touch would take away the
    /// results the model is in the middle of using.</summary>
    public static int RecentFrom(IReadOnlyList<ChatMessage> messages, int keepRecentRounds)
    {
        if (messages == null) return 0;
        if (keepRecentRounds <= 0) return messages.Count;   // nothing is recent enough to protect
        var rounds = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message == null || message.Role != "assistant" || message.ToolCalls.Count == 0) continue;
            if (++rounds >= keepRecentRounds) return i;
        }
        return 0;
    }

    /// <summary>Roughly what the whole transcript costs, by the same measure <see cref="Trim"/> budgets
    /// with. Public so the loop can ask whether a request has outgrown its budget before deciding whether
    /// anything needs to give way.</summary>
    public static int TotalChars(IReadOnlyList<ChatMessage> messages)
    {
        var total = 0;
        if (messages == null) return total;
        foreach (var message in messages)
            if (message != null) total += Cost(message);
        return total;
    }

    /// <summary>What each call was asked for, keyed by the id its result answers. The arguments live on the
    /// assistant message that announced the call, not on the result, and a note that cannot say which file it
    /// replaced is worth far less to the model than one that can.</summary>
    private static Dictionary<string, string> ArgumentsById(IReadOnlyList<ChatMessage> messages)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message == null || message.Role != "assistant") continue;
            foreach (var call in message.ToolCalls)
                if (call.Id.Length > 0 && !arguments.ContainsKey(call.Id)) arguments[call.Id] = call.Arguments;
        }
        return arguments;
    }

    /// <summary>Roughly what a message costs the request. Reasoning is counted because a tool-calling turn
    /// echoes it back, and the arguments because they travel with the call.</summary>
    private static int Cost(ChatMessage message)
    {
        var cost = message.Content.Length + message.Reasoning.Length;
        foreach (var call in message.ToolCalls) cost += call.Arguments.Length;
        return cost;
    }

    /// <summary>What stands in for a result that could not be afforded: which call it answered, how much was
    /// dropped, and that it can be had again. The arguments are folded onto one line and bounded, because a
    /// patch runs to thousands of characters and this note is meant to be read at a glance.</summary>
    private static string Note(ChatMessage message, Dictionary<string, string> arguments)
    {
        var name = message.ToolName.Length > 0 ? message.ToolName : "tool";
        arguments.TryGetValue(message.ToolCallId, out var asked);
        var call = string.IsNullOrEmpty(asked) ? name : name + " " + ChatStateLimits.OneLine(asked, 120);
        return call + " — " + message.Content.Length.ToString(CultureInfo.InvariantCulture)
            + " characters dropped to save context; call the tool again if you need the result.";
    }
}
