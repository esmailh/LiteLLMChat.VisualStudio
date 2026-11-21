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
    /// <summary>Every time this turn's older messages were folded into a checkpoint, in the order it happened.
    /// A list rather than the latest one, because a long turn can compact more than once and a reloaded
    /// conversation has to draw back the same rows the live one drew. The property keeps its stored name so
    /// conversations written before checkpoints still replay their briefings.</summary>
    public List<CompactionReport> Summaries { get; set; } = new List<CompactionReport>();
    /// <summary>The checkpoint this turn wrote, or null when it wrote none. The newest checkpoint in a session is
    /// the one the next turn builds its request from; it is stored with the turn that wrote it so a conversation
    /// that was switched away from mid-turn cannot have its checkpoint written into whichever chat is selected
    /// when the call returns.</summary>
    public CompactionCheckpoint? Compaction { get; set; }
    /// <summary>What the model reported spending across this turn's calls, or null when no call reported
    /// anything — a provider that omits usage while streaming, or a turn stored before this was counted.
    /// Kept on the turn so a reloaded conversation shows the same total the live one did.</summary>
    public TokenUsage? Usage { get; set; }
    public AgentPlan? AgentPlan { get; set; }
}

/// <summary>What one compaction of the older part of a transcript did. Shaped like <see cref="BuildReport"/>
/// on purpose: the row and the tooltip are drawn from the same two fields either way, and both are written by
/// the extension about something it did on its own.</summary>
public sealed class CompactionReport
{
    /// <summary>How many messages of the transcript's beginning the checkpoint stands for. Persisted so a
    /// reloaded conversation can say what was folded, and so the row and the request agree.</summary>
    public int Messages { get; set; }
    /// <summary>The one-line label the chat row shows.</summary>
    public string Summary { get; set; } = "";
    /// <summary>What the folded messages were — how many of each kind, and which tools ran in them. Kept with
    /// the report so a reloaded conversation says the same thing the live one did.</summary>
    public string Covered { get; set; } = "";
    /// <summary>The checkpoint itself, which is what the model was given in place of those messages.</summary>
    public string Detail { get; set; } = "";
    /// <summary>Which of <see cref="CompactionTrigger"/> caused it, by name. Empty on rows written by builds
    /// before this existed, which read as automatic.</summary>
    public string Trigger { get; set; } = "";
}

/// <summary>The checkpoint a session's request opens with: the structured record of everything before
/// <see cref="Covered"/>, plus where that boundary is. One per session — the latest one — and updated in place
/// rather than replaced from scratch, because a checkpoint is a consolidated record and not a fresh summary.</summary>
public sealed class CompactionCheckpoint
{
    /// <summary>How many messages of the conversation's beginning the checkpoint stands for.</summary>
    public int Covered { get; set; }
    /// <summary>The checkpoint text, in the sectioned form <see cref="ContextCompaction.Template"/> asks for.</summary>
    public string Text { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
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
    /// <summary>When true, a turn sends only the messages it produced itself: earlier turns of this chat stay on
    /// screen and in the saved conversation but are not part of the request. It trades continuity for tokens, so
    /// it defaults to false — a file written before the switch existed reads as "send everything", the behavior
    /// it had when it was written.</summary>
    public bool IgnoreHistory { get; set; }
    /// <summary>Execution transport, independent of Ask/Agent/Plan. Missing values from older files mean LiteLLM.</summary>
    public string ExecutionBackend { get; set; } = "litellm";
    /// <summary>Opaque id emitted by Claude Code. Never synthesized from the extension conversation id.</summary>
    public string ClaudeSessionId { get; set; } = "";
    /// <summary>Skills the user selected for this conversation. Their bodies are injected into every turn, so the
    /// choice is a property of the chat rather than of one message. Stored by name so a skill can be moved or its
    /// folder renamed without rewriting the conversation; a name that no longer resolves is reported, not dropped.
    /// Selection is not a permission grant.</summary>
    public List<string> SelectedSkills { get; set; } = new List<string>();
    /// <summary>Project folders the user added beside the solution, so the tools of this conversation may read and
    /// change them too. Stored as full paths with the conversation rather than with the settings: which projects a
    /// chat is about is part of the chat, and another conversation must not inherit them. Adding one widens what the
    /// model may touch, so the list is bounded (<see cref="Workspace.MaxExtraRoots"/>) and never includes the
    /// solution itself.</summary>
    public List<string> ExtraRoots { get; set; } = new List<string>();
    /// <summary>True when this conversation was told, in a message, to be offered the MCP servers' tools on every
    /// turn (<see cref="McpChatInstruction"/>). It belongs to the chat rather than to the settings — it is a
    /// statement about this conversation, and a new chat must not inherit it — and it is read once at the start of a
    /// turn, like <see cref="IgnoreHistory"/>. A later instruction that asks for the opposite clears it. Only the
    /// LiteLLM backend reads it: the Claude CLI has its own harness and its own tools.</summary>
    public bool McpEveryTurn { get; set; }
    /// <summary>The prompt count the server reported for the last response of this conversation, and the
    /// character size of the request that produced it. Together they are the anchor the compaction estimate
    /// grows from: the measured count plus the difference since, never a guess at the whole prompt. Zero means
    /// no measurement (a server that omits usage, or a conversation written before this existed), and the
    /// estimate then falls back to the request's own size.</summary>
    public int MeasuredPromptTokens { get; set; }
    public int MeasuredPromptChars { get; set; }
    /// <summary>The model the measurement belongs to. A count taken while another model was answering says
    /// nothing about this one's prompt, so it is dropped when the model changes.</summary>
    public string MeasuredPromptModel { get; set; } = "";
    public List<ChatTurnState> Turns { get; set; } = new List<ChatTurnState>();
    public List<PlanStep> Plan { get; set; } = new List<PlanStep>();

    [JsonIgnore]
    public bool HasMessages => Turns.Any(t => t.Messages.Any(m => m.Role == "user" || m.Role == "assistant"));

    /// <summary>The checkpoint this conversation's request opens with, or null when none was written. Read
    /// from the newest turn that wrote one, because a checkpoint belongs to the turn and a turn belongs to the
    /// conversation it was started in.</summary>
    [JsonIgnore]
    public CompactionCheckpoint? LastCheckpoint
    {
        get
        {
            for (var i = Turns.Count - 1; i >= 0; i--)
                if (Turns[i]?.Compaction != null) return Turns[i].Compaction;
            return null;
        }
    }
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
    public const int MaxPromptChars = 300000;

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
