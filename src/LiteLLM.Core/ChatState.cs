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
