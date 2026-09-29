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
