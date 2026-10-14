using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LiteLLM.Core;

public enum ChatTurnStatus
{
    Pending,
    Streaming,
    Complete,
    Cancelled,
    Failed
}

public sealed class ChatContextItem
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "selection";
    public string Preview { get; set; } = "";
}

public sealed class ChatTurnState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public int Attempt { get; set; } = 1;
    public string ModelKey { get; set; } = "";
    public ChatTurnStatus Status { get; set; } = ChatTurnStatus.Pending;
    public string Error { get; set; } = "";
    public List<ChatContextItem> Context { get; set; } = new List<ChatContextItem>();
    public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    /// <summary>The last automatic post-edit build for this turn, or null when none ran. Kept beside the
    /// messages rather than as one of them: the note the model is sent is a user-role message, and storing
    /// it as a message would render it back as something the user typed.</summary>
    public BuildReport? Build { get; set; }
    /// <summary>The last automatic post-edit test run for this turn, or null when none ran. Beside the build
    /// report rather than replacing it: the build says whether the change compiles and the tests say whether it
    /// still does what it did, and a turn that lost either one would lose the reason it was asked to keep
    /// going.</summary>
    public BuildReport? Tests { get; set; }
    /// <summary>Every time this turn's older messages were folded into a summary, in the order it happened.
    /// A list rather than the latest one, because a long turn can summarize more than once and a reloaded
    /// conversation has to draw back the same rows the live one drew.</summary>
    public List<ContextSummaryReport> Summaries { get; set; } = new List<ContextSummaryReport>();
}

/// <summary>What one summarization of the older part of a transcript did. Shaped like <see cref="BuildReport"/>
/// on purpose: the row and the tooltip are drawn from the same two fields either way, and both are written by
/// the extension about something it did on its own.</summary>
public sealed class ContextSummaryReport
{
    /// <summary>How many messages of the transcript's beginning the summary stands for. Persisted, because it
    /// is what tells a later round how much of the conversation is already inside the summary and how much new
    /// material would be worth asking for another one.</summary>
    public int Messages { get; set; }
    /// <summary>The one-line label the chat row shows.</summary>
    public string Summary { get; set; } = "";
    /// <summary>The summary itself, which is what the model was given in place of those messages.</summary>
    public string Detail { get; set; } = "";
}

public sealed class ChatSessionState
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New chat";
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Title) ? "New chat" : Title;
            return title.Length <= 20 ? title : title.Substring(0, 20);
        }
    }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string SelectedModelKey { get; set; } = "";
    /// <summary>Execution transport, independent of Ask/Agent/Plan. Missing values from older files mean LiteLLM.</summary>
    public string ExecutionBackend { get; set; } = "litellm";
    /// <summary>Opaque id emitted by Claude Code. Never synthesized from the extension conversation id.</summary>
    public string ClaudeSessionId { get; set; } = "";
    public List<ChatTurnState> Turns { get; set; } = new List<ChatTurnState>();

    [JsonIgnore]
    public bool HasMessages => Turns.Any(t => t.Messages.Any(m => m.Role == "user" || m.Role == "assistant"));
}

public sealed class ChatSessionDocument
{
    public int Version { get; set; } = 1;
    public Guid ActiveSessionId { get; set; }
    public List<ChatSessionState> Sessions { get; set; } = new List<ChatSessionState>();
}

public static class ChatStateLimits
{
    public const int MaxSessions = 20;
    public const int MaxTurnsPerSession = 50;
    /// <summary>Room for a long tool-calling turn: one assistant plus its results per round, for the default budget and the first continuation.</summary>
    public const int MaxMessagesPerTurn = 160;
    public const int MaxMessageChars = 60000;
    public const int MaxToolResultChars = 12000;
    public const int MaxContextPreviewChars = 4000;

    /// <summary>Characters one request's transcript may carry, the system prompt and the attachments aside.
    /// Long turns are not shortened by dropping messages — see <see cref="ContextBudget"/> for what gives way
    /// instead — so this bounds the request without ever making it invalid.</summary>
    public const int MaxPromptChars = 200000;

    /// <summary>Rounds at the end of a transcript whose tool results are never shortened, whatever the budget
    /// says: those are the ones the model is working on right now.</summary>
    public const int KeepRecentToolRounds = 2;

    /// <summary>Tool-calling rounds allowed before the user is asked whether to keep going.</summary>
    public const int DefaultToolRounds = 25;
    /// <summary>Hard ceiling on tool-calling rounds, however often the user chooses to continue.</summary>
    public const int MaxToolRoundsCeiling = 200;

    /// <summary>Keeps a hand-edited settings.json from disabling the loop outright or running it unbounded.</summary>
    public static int ClampToolRounds(int value) => Math.Min(Math.Max(1, value), MaxToolRoundsCeiling);

    /// <summary>Copilot Chat's growth step when the user continues: <c>min(round(limit * 3 / 2), 200)</c>.
    /// Half rounds away from zero, as JavaScript's <c>Math.round</c> does, rather than to even.</summary>
    public static int ExtendToolRounds(int limit) => Math.Min((int)Math.Round(ClampToolRounds(limit) * 1.5, MidpointRounding.AwayFromZero), MaxToolRoundsCeiling);

    /// <summary>True once the budget can no longer grow, so the caller stops instead of asking to continue forever.</summary>
    public static bool AtToolRoundsCeiling(int limit) => limit >= MaxToolRoundsCeiling;

    /// <summary>Drops whatever an OpenAI-compatible server would reject on the next request. A tool result
    /// must answer a call the assistant announced, and an announced call must be answered — otherwise the
    /// server refuses the whole transcript (HTTP 400). History reaches this state from the head-slice in
    /// <see cref="MaxMessagesPerTurn"/>, from a tool batch that threw partway, and from sessions written by
    /// builds that stopped mid-round, and it is repaired here rather than by the model.</summary>
    public static List<ChatMessage> Sanitize(IEnumerable<ChatMessage> messages)
    {
        var source = messages.Where(m => m != null).ToList();
        var first = source.FindIndex(m => m.Role == "user");
        if (first > 0) source = source.Skip(first).ToList();   // a transcript cannot open on an assistant or a result

        var answered = new HashSet<string>(source.Where(m => m.Role == "tool").Select(m => m.ToolCallId), StringComparer.Ordinal);
        var kept = new HashSet<ChatMessage>();
        foreach (var message in source)
            if (message.Role == "assistant" && message.ToolCalls.Count > 0 && message.ToolCalls.All(c => c.Id.Length > 0 && answered.Contains(c.Id)))
                kept.Add(message);

        var announced = new HashSet<string>(kept.SelectMany(m => m.ToolCalls).Select(c => c.Id), StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>();
        foreach (var message in source)
        {
            if (message.Role == "tool")
            {
                if (!emitted.Add(message.ToolCallId) || !announced.Contains(message.ToolCallId)) continue;
            }
            else if (message.Role == "assistant")
            {
                if (message.ToolCalls.Count > 0 && !kept.Contains(message)) continue;
                if (message.ToolCalls.Count == 0 && message.Content.Length == 0 && message.Reasoning.Length == 0) continue;
            }
            result.Add(message);
        }
        return result;
    }

    public static ChatMessage CopyMessage(ChatMessage source)
    {
        return new ChatMessage
        {
            Role = source.Role,
            Content = Clip(source.Content, source.Role == "tool" ? MaxToolResultChars : MaxMessageChars),
            Reasoning = Clip(source.Reasoning, MaxMessageChars),
            ToolCallId = source.ToolCallId,
            ToolName = source.ToolName,
            ToolCalls = source.ToolCalls.Select(c => new ToolCall { Id = c.Id, Name = c.Name, Arguments = Clip(c.Arguments, MaxMessageChars) }).ToList()
        };
    }

    /// <summary>What <see cref="Clip"/> leaves behind where it cut a transcript. Named because it is the extension's own
    /// note about what it dropped, so anything shown to a person as a label has to be able to recognise it and take it
    /// off — which is what <see cref="ChatSessionStore"/> does for titles written by an older build.</summary>
    public const string TruncationMarker = "\n… [history truncated]";

    public static string Clip(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value.Substring(0, max) + TruncationMarker;

    /// <summary>Shapes a value into a one-line label — a conversation's title in the history drop-down, which is one row
    /// and so is one line by definition. Every run of whitespace becomes a single space, so a title taken from a first
    /// message written in paragraphs does not arrive with its line breaks still in it; and a value too long for the row
    /// ends in an ellipsis rather than in <see cref="Clip"/>'s marker, because that marker is a sentence about what was
    /// dropped from a transcript and it opens with a line break of its own — which in a list row is a second row, and was
    /// the reason a long conversation showed up on two lines in the drop-down.</summary>
    public static string OneLine(string? value, int max)
    {
        var text = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text.Substring(0, max).TrimEnd() + "…";
    }
}
