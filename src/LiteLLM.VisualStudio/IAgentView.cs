using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>What the agent loop asks of the chat window.
///
/// The loop used to live inside <see cref="ChatControl"/> and call its private methods directly, which meant the
/// one thing that decides how a turn behaves could only be read through the control that draws it. This is the
/// whole of the surface it actually uses, written down once: reading and appending the transcript, drawing an
/// assistant bubble and a tool row, asking the user to continue, and the handful of pieces of window state the
/// status line reads. Nothing here is for the harness's convenience — a member that no longer has a caller goes.
///
/// It stays in this project because the tools it runs need DTE and the editor, which put them beyond the test
/// assembly's reach; the pure parts of the turn already live in Core.</summary>
internal interface IAgentView
{
    /// <summary>The transcript the request is built from, and the one the stored session follows.</summary>
    List<ChatMessage> History { get; }

    /// <summary>The turn being written. Null once the window has moved on — a user who switches conversation
    /// mid-turn keeps the loop running against no turn rather than writing into the new one.</summary>
    ChatTurnState? ActiveTurn { get; }

    /// <summary>The last raw round trip, kept for the error row and the copy commands.</summary>
    HttpExchange? LastExchange { get; set; }

    /// <summary>The mode the user picked in the header.</summary>
    string Mode { get; }

    /// <summary>The word the live status line is currently showing.</summary>
    string Phase { get; set; }

    /// <summary>Characters streamed so far, shown beside the phase.</summary>
    int Streamed { get; set; }

    /// <summary>The one-line status under the composer.</summary>
    string Status { get; set; }

    Bubble AddAssistantBubble(string status);
    void SetContent(Bubble bubble, ChatMessage message);
    void FinishStatus(Bubble bubble);
    void AddToolNote(string name, string summary, bool ok, HttpExchange? exchange, string? detail, AppliedEdit? edit);

    /// <summary>One row for the edits the turn applied, offering to put all of them back. Live-only: the row
    /// holds the edits themselves, and those are this session's memory rather than the conversation's.</summary>
    void AddUndoAllNote(IReadOnlyList<AppliedEdit> edits);

    Task<bool> AskToContinueAsync(int limit, int raised, CancellationToken token);

    /// <summary>Marshals a render to the UI thread. The streaming callback runs wherever the HTTP client calls
    /// it, and the bubble it updates is a WPF element.</summary>
    Task InvokeAsync(Action action);

    Task<string?> SolutionRootAsync();
    string? ReadAttachedText(string path);
    void SaveSession();
}
