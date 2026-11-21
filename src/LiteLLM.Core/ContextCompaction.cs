using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LiteLLM.Core;

/// <summary>Why a compaction ran. The three are the three opencode names: the conversation reached the model's
/// ceiling, the provider rejected a request as too long, or the user asked for one.</summary>
public enum CompactionTrigger
{
    None,
    Auto,
    Overflow,
    Manual
}

/// <summary>Folds the older part of a conversation into a structured checkpoint, the way opencode compacts a
/// session: a token ceiling below the model's window decides when, a fixed template decides what the checkpoint
/// says, the newest <c>keep</c> tokens of conversation travel beside it verbatim, and an existing checkpoint is
/// updated rather than replaced.
///
/// The one measure this build has is a pair: the provider's own prompt count from the last response, plus an
/// estimate of what has been added since. There is no tokenizer here — the estimate is two characters per token,
/// the same conservative ratio <see cref="ModelRequestBudget"/> already converts a window with — so the token
/// count is what decides <em>whether</em> to compact, while <see cref="RequestFitting"/> remains the hard,
/// character-measured bound on what may actually be sent.
///
/// A checkpoint is one <c>user</c> message at the head of the request copy (<see cref="Replacement"/>). The
/// transcript, the chat and the stored session keep every message: what is replaced is the request, never the
/// conversation.</summary>
public static class ContextCompaction
{
    /// <summary>The ratio every estimate here works at: two characters per token. It is the same conservative
    /// figure <see cref="ModelRequestBudget.Limit"/> already uses, so a ceiling in tokens and a safety bound in
    /// characters describe the same request.</summary>
    public const int CharsPerToken = 2;

    /// <summary>opencode's default <c>keep</c>: the newest 15 000 tokens of the conversation stay beside the
    /// checkpoint verbatim, because the checkpoint is a model's account and the immediate back-and-forth is the
    /// part a model is most likely to need word for word.</summary>
    public const int DefaultKeepTokens = 15000;

    /// <summary>The least window kept free for the reply and the checkpoint itself.</summary>
    public const int ReserveMin = 16000;

    /// <summary>The share of the window kept free when it is larger than twice <see cref="ReserveMin"/>.</summary>
    public const int ReserveShare = 15;

    /// <summary>Ceiling on the checkpoint that comes back. Model output is the one thing in this loop that
    /// arrives without a size, so it is bounded here and again on the way to disk.</summary>
    public const int MaxSummaryChars = 12000;

    /// <summary>Reasoning room for the summarization call, in place of the session's own budget.</summary>
    public const int UtilityTokens = 4000;

    /// <summary>Ceiling on one message inside the summarization request.</summary>
    public const int MaxMessageChars = 2000;

    /// <summary>Ceiling on the whole span handed to the summarizer when the model's own limit is not known.</summary>
    public const int MaxSpanChars = 180000;

    /// <summary>A span below this is not worth a call.</summary>
    public const int MinSpanChars = 8000;

    /// <summary>Characters kept from the start and end of a tool output or a user message that has to be
    /// shortened for the summarizer.</summary>
    public const int TranscriptClip = 2000;

    /// <summary>Most checkpoints one turn's transcript keeps, so a loop cannot grow the rows without bound.</summary>
    public const int MaxPerTurn = 8;

    /// <summary>After each "too long" rejection, the next attempt aims at this share of the rejected size.</summary>
    public static readonly double[] ShrinkSteps = { .85, .7, .5, .35 };

    /// <summary>The template opencode asks for. The section headings are the part that matters: a reply is only
    /// accepted as a checkpoint when it carries one of them.</summary>
    public const string Template =
        "You MUST use this format for your response (you may omit sections that aren't applicable).\n"
        + "## Objective\n"
        + "- [one or two brief sentences describing what the user is trying to accomplish]\n\n"
        + "## Requirements\n"
        + "- [constraints, preferences, requirements, and scope boundaries stated by the user, or \"(none)\"]\n\n"
        + "## Decisions\n"
        + "- [decisions already made and why, or \"(none)\"]\n\n"
        + "## Work State\n"
        + "Break the objective into smaller goals and report which are completed, which are being worked on, and which are blocked.\n"
        + "### Completed\n"
        + "- [goals that have been completed; otherwise \"(none)\"]\n\n"
        + "### Active\n"
        + "- [goals currently being worked on; otherwise \"(none)\"]\n\n"
        + "### Blocked\n"
        + "- [anything blocking progress, and why; otherwise \"(none)\"]\n\n"
        + "## Next Move\n"
        + "1. [ordered list of next actions, or \"(none)\"]\n\n"
        + "## Relevant Files\n"
        + "List the files and directories, other than the current working directory, that another agent would need to open to continue this work. Include at most 15, most important first. Do not list every file that was read or changed. Include paths outside the current working directory when relevant. If none, write \"(none)\".\n"
        + "- `[file or directory path]`: [brief reason it matters]\n\n"
        + "## Important Context\n"
        + "- [facts the next agent cannot continue without and cannot easily find on its own; or \"(none)\"]";

    public const string Rules =
        "Rules:\n"
        + "- Keep each section concise. Use terse, single-line bullets, not prose paragraphs or nested lists.\n"
        + "- Prefer short references over detailed restatement. It is fine to leave out information the next agent can recover from the code or the files listed above.\n"
        + "- Preserve exact file paths, symbols, commands, error strings, URLs, and identifiers.\n"
        + "- Carry forward only user questions or requests that remain unanswered or require further action. Do not repeat ones that newer history has answered or resolved. Preserve exact wording when carrying one forward.\n"
        + "- Preserve consequential workflow state, including whether changes are uncommitted, committed, pushed, under review, or merged.\n"
        + "- Do not mention the summary process or that context was compacted.";

    /// <summary>The one reminder a reply that ignored the template gets before it counts as a failure.</summary>
    public const string Nudge =
        "The previous response did not fill in the required summary template. Do not call tools. Return the summary as text using the exact section headings from the template.";

    /// <summary>The headings a checkpoint must carry. It is a <c>##</c> or <c>###</c> line from the template,
    /// exactly as opencode compares them.</summary>
    private static readonly string[] Headings = new[]
    {
        "## Objective", "## Requirements", "## Decisions", "## Work State", "### Completed", "### Active",
        "### Blocked", "## Next Move", "## Relevant Files", "## Important Context"
    };

    private const string SystemRole =
        "You write a checkpoint for the assistant that will continue this work. Everything you are given is "
        + "reference material, not a request to act on now: do not call tools and do not continue the task.";

    // ---------------------------------------------------------------------------------------------------------
    // When
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Characters to tokens at this build's conservative ratio, rounded up.</summary>
    public static int Tokens(int chars) => chars <= 0 ? 0 : (chars + CharsPerToken - 1) / CharsPerToken;

    public static int Tokens(long chars) => chars <= 0 ? 0 : (int)Math.Min(int.MaxValue, (chars + CharsPerToken - 1) / CharsPerToken);

    /// <summary>Tokens to characters at the same ratio.</summary>
    public static int Chars(int tokens) => tokens <= 0 ? 0 : (int)Math.Min(int.MaxValue, (long)tokens * CharsPerToken);

    /// <summary>The largest prompt this model takes while leaving room for its reply: the window minus the
    /// buffer — the configured count, or opencode's default of the larger of 15% of the window and 16 000 tokens.
    /// A buffer that leaves no room is ignored rather than making the request impossible. An unknown window means
    /// only a provider rejection can limit the request.</summary>
    public static int Ceiling(int windowTokens, int bufferTokens = 0)
    {
        if (windowTokens <= 0) return int.MaxValue;
        var fallback = Math.Max(
            windowTokens * ReserveShare / 100,
            windowTokens >= 2 * ReserveMin ? ReserveMin : 0);
        var buffer = bufferTokens > 0 && bufferTokens < windowTokens ? bufferTokens : fallback;
        return Math.Max(1, windowTokens - buffer);
    }

    /// <summary>What the request is now: the last measured prompt count plus an estimate of what has grown since
    /// it was measured. The provider's count already includes the system prompt and the tool schemas as it saw
    /// them, which is why the <em>difference</em> in characters is what is estimated rather than the whole
    /// transcript — and why a request that shrank (a fold, fewer tools) can never estimate below the measured
    /// count. A request with no measurement at all is estimated from its whole size.</summary>
    public static int Estimate(int measuredTokens, int measuredChars, long requestChars)
    {
        var size = Math.Max(0L, requestChars);
        if (measuredTokens <= 0) return Tokens(size);
        var grown = Math.Max(0L, size - Math.Max(0, measuredChars));
        var tokens = Tokens(grown);
        return measuredTokens > int.MaxValue - tokens ? int.MaxValue : measuredTokens + tokens;
    }

    public static bool Due(int estimatedTokens, int ceiling) => ceiling > 0 && estimatedTokens >= ceiling;

    /// <summary>What a request that the provider just rejected as too long should aim at on its next attempt.
    /// The estimate ran low — the rejection is the proof — so the first attempt aims below the size that was
    /// refused, and each later one shrinks further. Never above the ceiling.</summary>
    public static int OverflowBudget(int estimatedTokens, int ceiling, int attempt)
    {
        var step = ShrinkSteps[Math.Min(Math.Max(0, attempt), ShrinkSteps.Length - 1)];
        var aimed = (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(1, estimatedTokens) * step));
        return ceiling > 0 && ceiling != int.MaxValue ? Math.Min(ceiling, aimed) : aimed;
    }

    /// <summary>The configured keep, clamped to a size a request can hold. Zero is allowed and keeps no tail
    /// verbatim; a hand-edited value must not make the tail the whole window.</summary>
    public static int EffectiveKeep(int configured) =>
        configured <= 0 ? (configured == 0 ? 0 : DefaultKeepTokens) : Math.Min(configured, 200000);

    /// <summary>The tail kept after the provider rejected a request as too long: a third of what the next attempt
    /// may become, so the checkpoint, the instruction and the verbatim tail together have a chance of fitting
    /// under the budget the rejection named, and never more than the configured keep. A third rather than half
    /// because the checkpoint is model output of unbounded length until it comes back, and the system prompt sits
    /// in the same request.</summary>
    public static int OverflowKeep(int budgetTokens, int configured)
    {
        var keep = EffectiveKeep(configured);
        if (budgetTokens <= 0) return keep;
        return Math.Min(keep, Math.Max(1000, budgetTokens / 3));
    }

    // ---------------------------------------------------------------------------------------------------------
    // What stays
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The index the newest <paramref name="keepTokens"/> tokens of conversation begin at: what stays
    /// verbatim beside the checkpoint. Never inside a tool round — a tail may not open on a result whose call was
    /// folded away — so a cut that lands on a result moves back to the assistant message that announced it.
    ///
    /// Returns 0 when the whole conversation fits in the tail: there is nothing to compact.</summary>
    public static int KeepFrom(IReadOnlyList<ChatMessage> messages, int keepTokens)
    {
        if (messages == null || messages.Count == 0) return 0;
        // Zero keeps nothing verbatim: every message is folded into the checkpoint, and the request that follows
        // is the checkpoint (plus the pinned request and anything that arrived after it).
        if (keepTokens <= 0) return messages.Count;
        var keepChars = (long)Chars(keepTokens);
        long total = 0;
        var from = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] == null) { from = i; continue; }
            total += ContextBudget.Cost(messages[i]) + 8;
            if (total > keepChars) { from = i + 1; break; }
            from = i;
        }
        if (from >= messages.Count) from = messages.Count - 1;
        // A tail has to start at the assistant message that announced the calls whose results it carries.
        while (from > 0 && messages[from] != null && messages[from].Role == "tool") from--;
        return from;
    }

    // ---------------------------------------------------------------------------------------------------------
    // What is written
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The request that writes the next checkpoint.
    ///
    /// <paramref name="covered"/> is how many messages of the conversation's beginning the checkpoint already in
    /// hand stands for; <paramref name="keepFrom"/> is where the verbatim tail begins. The span folded in is
    /// everything between them — the material that has arrived since the checkpoint was written, which grows
    /// across turns and not only inside one. An existing checkpoint is placed in the conversation as its own
    /// message and the prompt asks for one consolidated update, exactly as opencode rebuilds its history from the
    /// latest checkpoint.
    ///
    /// <paramref name="through"/> is the prefix length the returned request covers. It is never inside a tool
    /// round, and never more than was actually written into the request: the messages the checkpoint claims to
    /// stand for are the messages the model was shown. A span that is too big even at the flattest transcript
    /// level is folded as far as it fits; the next round folds the rest.</summary>
    public static bool TryBuildRequest(IReadOnlyList<ChatMessage> messages, int covered, int keepFrom,
        string? previous, out List<ChatMessage> request, out int through, int maxSpanChars = MaxSpanChars)
    {
        request = new List<ChatMessage>();
        through = 0;
        if (messages == null || messages.Count == 0) return false;
        if (covered < 0 || covered > messages.Count) covered = 0;   // a stored transcript that was head-sliced
        if (keepFrom > messages.Count) keepFrom = messages.Count;
        if (keepFrom <= covered) return false;                       // nothing new to fold

        var budget = Math.Max(MinSpanChars, maxSpanChars);
        List<KeyValuePair<int, string>> rendered = null!;
        foreach (var level in new[] { TranscriptLevel.Whole, TranscriptLevel.OutputsClipped, TranscriptLevel.OutputsRemoved, TranscriptLevel.UserOnly })
        {
            rendered = Render(messages, covered, keepFrom, level);
            if (RenderedChars(rendered) <= budget) break;
        }
        if (rendered.Count == 0) return false;

        // Whole messages from the oldest, never a call without the result that answers it.
        var used = 0;
        var cut = 0;
        while (cut < rendered.Count)
        {
            var cost = rendered[cut].Value.Length + 1;
            if (used + cost > budget && cut > 0) break;
            used += cost;
            cut++;
        }
        var end = cut < rendered.Count ? rendered[cut].Key : keepFrom;
        while (end > covered && end < messages.Count && messages[end] != null && messages[end].Role == "tool") end--;
        if (end <= covered) return false;

        var body = new StringBuilder();
        foreach (var entry in rendered)
        {
            if (entry.Key >= end) break;
            body.Append(entry.Value).Append('\n');
        }

        var update = !string.IsNullOrWhiteSpace(previous);
        request.Add(new ChatMessage { Role = "system", Content = SystemRole });
        if (update)
            request.Add(new ChatMessage { Role = "user", Content = Checkpoint(previous!) });
        request.Add(new ChatMessage { Role = "user", Content = "Conversation to fold into the checkpoint:\n\n" + body });
        request.Add(new ChatMessage { Role = "user", Content = BuildPrompt(update, update && !Filled(previous)) });
        through = end;
        return true;
    }

    /// <summary>The transcript with its first <paramref name="through"/> messages replaced by the checkpoint.
    /// Messages after it are handed back as they came, which is safe because <see cref="AgentPrompt.Build"/> copies
    /// the history into the request rather than handing these out.
    ///
    /// <paramref name="pinned"/> is the request the turn is answering. It is put back verbatim in front of the
    /// messages the fold kept, because a checkpoint can leave out what the user actually asked for, and one bad
    /// checkpoint then reads to the next round as a conversation with an attached file and no request at all. It is
    /// never duplicated: a request already after <paramref name="through"/> stays where it is.</summary>
    public static List<ChatMessage> Merge(IReadOnlyList<ChatMessage> messages, int through, string checkpoint,
        ChatMessage? pinned = null)
    {
        var result = new List<ChatMessage> { Replacement(checkpoint) };
        if (messages != null)
            for (var i = Math.Max(0, through); i < messages.Count; i++)
                if (messages[i] != null) result.Add(messages[i]);

        if (pinned != null)
        {
            var present = false;
            for (var i = 1; i < result.Count && !present; i++)
                present = ReferenceEquals(result[i], pinned);
            // After the checkpoint, so the request still opens on a user message and Sanitize keeps it.
            if (!present) result.Insert(1, pinned);
        }
        return result;
    }

    /// <summary>The message a turn is answering: the last thing the user typed. It is the one message a
    /// checkpoint may not stand in for. A conversation with no message from the user has nothing to pin.</summary>
    public static ChatMessage? RequestMessage(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages == null) return null;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i] != null && messages[i].Role == "user") return messages[i];
        return null;
    }

    /// <summary>What stands in for the messages that were folded in. A <c>user</c> message framed as a checkpoint
    /// rather than as a request, because the model has to read it as established context: one that reads like an
    /// instruction would be answered instead of continued from.</summary>
    public static string Checkpoint(string checkpoint) =>
        "<conversation-checkpoint>\n<summary>\n" + (checkpoint ?? "").Trim()
        + "\n</summary>\n</conversation-checkpoint>";

    public static ChatMessage Replacement(string checkpoint) => new ChatMessage
    {
        Role = "user",
        Content = Checkpoint(checkpoint)
            + "\n\nA checkpoint above stands in for the earlier conversation. The messages below continue from it."
    };

    /// <summary>Whether a reply is a checkpoint at all: one line carrying a heading from the template. Anything
    /// else gets one reminder and then counts as a failed compaction.</summary>
    public static bool Filled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var line in text!.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var heading in Headings)
                if (string.Equals(trimmed, heading, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The instruction that writes or updates a checkpoint. opencode's wording, with the "legacy" branch
    /// covering the prose briefings earlier builds of this extension wrote: an update of one of those is told to
    /// rewrite it in the current sections rather than carry the prose forward.</summary>
    public static string BuildPrompt(bool update, bool legacy = false)
    {
        var text = new StringBuilder();
        text.Append(update
            ? "Update the existing checkpoint in the conversation above into one consolidated summary."
            : "You MUST summarize the conversation above into a structured summary that will be given to another agent to resume the work.");
        text.Append("\n\n");
        if (update && legacy)
            text.Append("The existing checkpoint was written as prose rather than in the sections below. Rewrite it in this format rather than carrying the prose forward. Keep its requirements, decisions, and open questions; they came from earlier conversation with the user.\n\n");
        text.Append("Summarize only what the user and the assistant said and did. Leave out instructions and setup the assistant was given rather than told by the user: repository conventions, instruction files such as AGENTS.md, and environment details like the session ID. The next agent receives current versions of all of these separately.\n\n");
        text.Append(Template).Append("\n\n");
        text.Append(Rules).Append("\n\n");
        text.Append("Do not continue the task or call tools.\n\n");
        text.Append("Return only the structured summary in the requested format. Do not include a preamble, explanation, or other commentary.");
        return text.ToString();
    }

    /// <summary>The settings the checkpoint call is made with: a copy, and only the fields that reach the request
    /// body. It is a copy because the user's <c>max_tokens</c> is sized for an answer, and no tools are offered
    /// because there is nothing for this call to look up.</summary>
    public static UserSettings UtilitySettings(UserSettings? source) => new UserSettings
    {
        MaxTokens = UtilityTokens,
        Temperature = source?.Temperature ?? .2,
        RequestTimeoutSeconds = source?.RequestTimeoutSeconds ?? 120,
        EnableToolCalling = false
    };

    // ---------------------------------------------------------------------------------------------------------
    // What is said
    // ---------------------------------------------------------------------------------------------------------

    public static CompactionReport Report(int messages, string text, string covered = "",
        CompactionTrigger trigger = CompactionTrigger.Auto) => new CompactionReport
    {
        Messages = messages,
        Summary = Label(messages, trigger),
        Detail = text,
        Covered = covered,
        Trigger = trigger.ToString()
    };

    /// <summary>What the folded part of the conversation was, in terms the reader can check against the
    /// transcript: how many of each kind, and which tools ran inside them.</summary>
    public static string Describe(IReadOnlyList<ChatMessage>? messages, int through, CompactionTrigger trigger = CompactionTrigger.Auto)
    {
        if (messages == null || through <= 0) return "";
        var end = Math.Min(through, messages.Count);
        var fromYou = 0; var replies = 0; var results = 0;
        var tools = new List<string>();
        for (var i = 0; i < end; i++)
        {
            var message = messages[i];
            if (message == null) continue;
            if (message.Role == "user") fromYou++;
            else if (message.Role == "assistant")
            {
                replies++;
                foreach (var call in message.ToolCalls)
                    if (!tools.Contains(call.Name, StringComparer.Ordinal)) tools.Add(call.Name);
            }
            else if (message.Role == "tool") results++;
        }

        var parts = new List<string>();
        if (fromYou > 0) parts.Add(fromYou + (fromYou == 1 ? " message from you" : " messages from you"));
        if (replies > 0) parts.Add(replies + (replies == 1 ? " reply" : " replies"));
        if (results > 0) parts.Add(results + (results == 1 ? " tool result" : " tool results"));
        var lead = trigger == CompactionTrigger.Overflow
            ? "Compaction after the server rejected the request as too long folded"
            : trigger == CompactionTrigger.Manual
                ? "Compaction folded"
                : "Automatic compaction folded";
        var text = lead + " the first " + end + " message" + (end == 1 ? "" : "s") + " of this conversation into the checkpoint below: "
            + (parts.Count > 0 ? string.Join(", ", parts) : "no messages with content") + ".";
        if (tools.Count > 0) text += " Tools in them: " + string.Join(", ", tools) + ".";
        return ChatStateLimits.OneLine(text, 400);
    }

    /// <summary>The row's label: what was replaced, and by what. One line by construction, as the row is one
    /// line.</summary>
    public static string Label(int messages, CompactionTrigger trigger = CompactionTrigger.Auto)
    {
        var lead = trigger == CompactionTrigger.Manual ? "Compacted by hand"
            : trigger == CompactionTrigger.Overflow ? "Compacted · the request was too long"
            : "Compacted";
        return lead + " · " + messages + " message" + (messages == 1 ? "" : "s") + " → checkpoint";
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rendering the span for the summarizer
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>How much of an exchange of the fold is written into the summary request. The levels apply in
    /// order until the span fits: whole, tool outputs cut, tool outputs left out and inputs cut, only the user's
    /// words, omitted.</summary>
    private enum TranscriptLevel
    {
        Whole,
        OutputsClipped,
        OutputsRemoved,
        UserOnly
    }

    private static List<KeyValuePair<int, string>> Render(IReadOnlyList<ChatMessage> messages, int from, int to,
        TranscriptLevel level)
    {
        var rendered = new List<KeyValuePair<int, string>>();
        for (var i = from; i < to; i++)
        {
            var message = messages[i];
            if (message == null) continue;
            var text = Render(message, level);
            if (text.Length == 0) continue;
            rendered.Add(new KeyValuePair<int, string>(i, text));
        }
        return rendered;
    }

    /// <summary>One message as the summarizer sees it. The tool calls are on the assistant message that announced
    /// them and the results are their own messages, so both are written out: what was asked and what came back is
    /// the whole of what a later round needs to know about a tool round.</summary>
    private static string Render(ChatMessage message, TranscriptLevel level)
    {
        if (level == TranscriptLevel.UserOnly && message.Role != "user") return "";
        var name = message.Role == "tool" && message.ToolName.Length > 0 ? "tool " + message.ToolName : message.Role;
        var text = new StringBuilder("[").Append(name).Append("] ");
        foreach (var call in message.ToolCalls)
            text.Append(call.Name).Append(' ').Append(ChatStateLimits.OneLine(call.Arguments, 300)).Append(' ');
        if (message.Content.Length > 0)
        {
            if (level == TranscriptLevel.OutputsRemoved && message.Role == "tool")
                text.Append("(" + message.Content.Length.ToString(CultureInfo.InvariantCulture) + " characters of output left out)");
            else if (level >= TranscriptLevel.OutputsClipped)
                text.Append(ClipMiddle(message.Content, TranscriptClip));
            else
                text.Append(message.Content);
        }
        // Bounded as a whole as well, so one message can never be the reason the span budget is blown: the calls
        // above are bounded each but unbounded in number. This is a per-message ceiling, not the span budget —
        // a message cut here is still one the checkpoint claims to stand for, and the next level flattens it
        // further rather than dropping it.
        return ChatStateLimits.Clip(text.ToString(), MaxMessageChars * 8);
    }

    /// <summary>Keeps the start and the end of an over-long value, because a tool result's first lines say what
    /// it was and its last lines say how it ended.</summary>
    private static string ClipMiddle(string value, int keep)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= keep * 2) return value ?? "";
        return value!.Substring(0, keep) + "\n[ clipped ]\n" + value.Substring(value.Length - keep);
    }

    private static int RenderedChars(List<KeyValuePair<int, string>> rendered)
    {
        var total = 0;
        foreach (var entry in rendered) total += entry.Value.Length + 1;
        return total;
    }
}
