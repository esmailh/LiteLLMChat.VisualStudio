using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LiteLLM.Core;

public sealed class ChatSessionStore
{
    private readonly string _path;
    private readonly JsonSerializerSettings _settings = new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore
    };

    public ChatSessionStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteLLM.VisualStudio", "chat-sessions.json");
    }

    public static string ScopedPath(string scope)
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteLLM.VisualStudio", "chat-sessions");
        if (string.IsNullOrWhiteSpace(scope)) return System.IO.Path.Combine(root, "no-solution.json");
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var normalized = scope.Trim().ToUpperInvariant();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            return System.IO.Path.Combine(root, BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() + ".json");
        }
    }

    public string Path => _path;

    public ChatSessionDocument Load()
    {
        if (!File.Exists(_path)) return NewDocument();
        try
        {
            var document = JsonConvert.DeserializeObject<ChatSessionDocument>(File.ReadAllText(_path), _settings) ?? NewDocument();
            Normalize(document);
            return document;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return NewDocument();
        }
    }

    public void Save(ChatSessionDocument document)
    {
        Normalize(document);
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented, _settings), Encoding.UTF8);
        if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
    }

    private static ChatSessionDocument NewDocument()
    {
        var session = NewSession();
        return new ChatSessionDocument { ActiveSessionId = session.Id, Sessions = new List<ChatSessionState> { session } };
    }

    public static ChatSessionState NewSession() => new ChatSessionState { Title = "New chat", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };

    /// <summary>Removes one conversation, and leaves the document in the state the rest of this class already insists
    /// on: the active pointer moves to whatever is left, and deleting the last conversation leaves a fresh empty chat
    /// rather than an empty list. Both of those are <see cref="Normalize"/>'s decisions, so they are asked of it rather
    /// than restated here — a second rule about which conversation is active is a second rule to drift.</summary>
    public static void Delete(ChatSessionDocument document, Guid id)
    {
        document.Sessions.RemoveAll(s => s.Id == id);
        Normalize(document);
    }

    private static void Normalize(ChatSessionDocument document)
    {
        if (document.Version <= 0) document.Version = 1;
        document.Sessions ??= new List<ChatSessionState>();
        foreach (var session in document.Sessions)
        {
            session.ExecutionBackend = string.Equals(session.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "litellm";
            session.ClaudeSessionId = session.ClaudeSessionId ?? "";
            session.Turns ??= new List<ChatTurnState>();
            // Laid out as a row of the history drop-down, so it is shaped as one line here rather than at each place that
            // shows it — including the titles already on disk, which is what repairs a conversation an older build
            // clipped with a marker that carried a line break in it.
            // A title an older build clipped with the transcript helper carries that helper's marker, which is the
            // extension's own note about what it dropped and was never part of anyone's conversation name. Taken off
            // before OneLine rather than after, because the marker opens with the line break that OneLine replaces.
            var title = ChatStateLimits.OneLine((session.Title ?? "").Replace(ChatStateLimits.TruncationMarker, ""), 120);
            session.Title = title.Length == 0 ? "New chat" : title;
            session.Turns = session.Turns
                .Where(t => t != null)
                .OrderBy(t => t.StartedUtc)
                .Skip(Math.Max(0, session.Turns.Count - ChatStateLimits.MaxTurnsPerSession))
                .ToList();
            foreach (var turn in session.Turns)
            {
                turn.Messages ??= new List<ChatMessage>();
                turn.Context ??= new List<ChatContextItem>();
                turn.Messages = ChatStateLimits.Sanitize(turn.Messages.Skip(Math.Max(0, turn.Messages.Count - ChatStateLimits.MaxMessagesPerTurn)).Select(ChatStateLimits.CopyMessage));
                turn.Context = turn.Context.Take(12).Select(c => new ChatContextItem
                {
                    Kind = ChatStateLimits.Clip(c.Kind, 80),
                    Label = ChatStateLimits.Clip(c.Label, 240),
                    Preview = ChatStateLimits.Clip(c.Preview, ChatStateLimits.MaxContextPreviewChars)
                }).ToList();
                // A build log arrives from the Error List, which is unbounded, so it is bounded here the way the
                // context is: the counts and the summary are what the row shows, and the detail only fills the
                // tooltip and the model's note.
                if (turn.Build != null)
                {
                    turn.Build.Detail = ChatStateLimits.Clip(turn.Build.Detail ?? "", BuildDiagnostics.MaxDetailChars);
                    turn.Build.Summary = ChatStateLimits.OneLine(turn.Build.Summary, 200);
                }
                // A test report arrives from a runner's console output, so it is bounded exactly like the build
                // report it is shaped as — including its failure text, which is a stack trace and can be long.
                if (turn.Tests != null)
                {
                    turn.Tests.Detail = ChatStateLimits.Clip(turn.Tests.Detail ?? "", TestDiagnostics.MaxDetailChars);
                    turn.Tests.Summary = ChatStateLimits.OneLine(turn.Tests.Summary, 200);
                }
                // A summary is model output, so it arrives without a size like a build log does, and it is
                // bounded the same way. The list is capped as well: a turn that summarized more often than this
                // stopped being a conversation and became a loop.
                turn.Summaries ??= new List<ContextSummaryReport>();
                turn.Summaries = turn.Summaries
                    .Where(s => s != null)
                    .Take(ContextSummary.MaxPerTurn)
                    .Select(s => new ContextSummaryReport
                    {
                        Messages = Math.Max(0, s.Messages),
                        Summary = ChatStateLimits.OneLine(s.Summary, 200),
                        Detail = ChatStateLimits.Clip(s.Detail ?? "", ContextSummary.MaxSummaryChars)
                    })
                    .ToList();
                if (turn.Status == ChatTurnStatus.Pending || turn.Status == ChatTurnStatus.Streaming)
                {
                    turn.Status = ChatTurnStatus.Cancelled;
                    turn.Error = "The previous Visual Studio session ended while this response was running.";
                    turn.CompletedUtc ??= DateTime.UtcNow;
                }
            }
        }
        document.Sessions = document.Sessions
            .Where(s => s != null)
            .OrderByDescending(s => s.UpdatedUtc)
            .Take(ChatStateLimits.MaxSessions)
            .ToList();
        if (document.Sessions.Count == 0)
        {
            var session = NewSession(); document.Sessions.Add(session); document.ActiveSessionId = session.Id;
        }
        if (!document.Sessions.Any(s => s.Id == document.ActiveSessionId)) document.ActiveSessionId = document.Sessions[0].Id;
    }
}
