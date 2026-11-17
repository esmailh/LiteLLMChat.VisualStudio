using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>One LiteLLM turn: the round loop that streams a reply, runs the tool calls it announces, verifies the
/// build (and, when asked, the tests), and either asks to continue or stops.
///
/// It lives here rather than in the chat window so that the loop and the thing that draws it are separable, and
/// behind <see cref="IAgentView"/> so that this file names only what it actually asks of the window. It is still
/// in the VS project — the tools it runs need DTE — but everything that could be moved to Core already has been:
/// the budget, the sanitizer, the summary, the prompts and the diagnostics are all Core-side.
///
/// The move was a move: the transcript, the order of every row and the text of every note are what the control
/// produced before. The one behaviour that changed is the disk-only tool batch, below.</summary>
internal sealed class AgentHarness
{
    private readonly IAgentView _view;
    private readonly ExtensionServices _services;
    private readonly ModelInfo _model;
    /// <summary>What this turn has spent so far, accumulated across its rounds. A harness is one turn, so the
    /// total has the turn's lifetime; a summary written along the way is counted too, because it is a call the
    /// turn caused.</summary>
    private readonly TokenUsage _usage = new TokenUsage();
    /// <summary>Every edit this turn applied, in the order it applied them. Held by reference rather than
    /// re-read from <see cref="EditTools"/> at the end, because the edit history is capped and a long turn can
    /// outlive its oldest entries.</summary>
    private readonly List<AppliedEdit> _edits = new List<AppliedEdit>();
    /// <summary>When the status line last took an MCP progress update, in ticks. Written from the session's
    /// receive loop, so it is read and exchanged rather than assigned.</summary>
    private long _lastProgressTicks;
    /// <summary>What the turn's own message selected. Resolved once, from the snapshot preparation just produced and
    /// the first user message of the active turn — the same rule as <see cref="ChatSessionState.IgnoreHistory"/>:
    /// editing the message mid-turn changes the next turn, not the running loop.</summary>
    private McpToolSelection _selection = McpToolSelection.Automatic;
    /// <summary>The MCP block for the system prompt: the selection hint, plus what to say about any server the message
    /// named that this turn cannot offer.</summary>
    private string? _mcpHint;
    /// <summary>What this turn's skill scan found, and the instructions built from it. Read by the tool dispatch
    /// and by the round that builds the request; both come from the same snapshot.</summary>
    private SkillPlan _skillPlan = SkillPlan.None;
    /// <summary>The folders this turn may read and change, read once at the start. A field rather than a local
    /// because the tool dispatch runs in its own method, and a tool call must be answered against the workspace
    /// of the turn that announced it — not against whatever conversation happens to be selected when it runs.</summary>
    private Workspace _workspace = new Workspace(null);
    /// <summary>The conversation this turn belongs to, as <see cref="BackgroundShells"/> records it. A shell outlives
    /// the turn, so the notice that one ended has to find its way back to the chat that started it and to no other.
    /// </summary>
    private string _shellOwner = "";
    /// <summary>Shells that ended while this turn was running. Kept for the rest of the turn once delivered, so a
    /// round that fails on the client does not lose the news — the transcript only grows, and re-sending a handful of
    /// lines costs nothing next to the output it describes.</summary>
    private readonly List<ShellCompletion> _shellNotices = new List<ShellCompletion>();
    /// <summary>The conversation this turn belongs to, captured when it was started. A user who switches chats
    /// mid-turn must not have this turn's checkpoint, or the anchor its estimate grows from, written into
    /// whichever conversation happens to be selected when a call returns.</summary>
    private readonly ChatSessionState _session;
    /// <summary>The checkpoint this conversation opened with when the turn began, or null when none was written.
    /// Read once here rather than per round, for the same reason the session is captured: it is the state a turn
    /// started from, and the turn's own checkpoint supersedes it as soon as one is written.</summary>
    private readonly CompactionCheckpoint? _checkpoint;
    /// <summary>Set once a turn has been told that a compaction was due but there was nothing to fold. The
    /// transcript only grows, so every later round would repeat the same sentence.</summary>
    private bool _nothingToCompactNoted;

    internal AgentHarness(IAgentView view, ExtensionServices services, ModelInfo model, ChatSessionState session)
    {
        _view = view;
        _services = services;
        _model = model;
        _session = session;
        _checkpoint = session.LastCheckpoint;
    }

    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            await RunTurnAsync(token).ConfigureAwait(true);
        }
        finally
        {
            // A shell belongs to the conversation, not to the turn: a server brought up to test an endpoint is
            // still up when the answer is written, and a later message must be able to send it another request.
            // What the turn owes the user is the fact that it is there — a program they watched start must not
            // become an unexplained port holder — so the row names each one and the pid that ends it.
            var running = _services.Shells.Running(_shellOwner.Length == 0 ? null : _shellOwner);
            if (running.Count > 0)
                _view.AddToolNote("shell",
                    "Shells · " + running.Count + " still running: " +
                    string.Join(", ", running.Select(shell => shell.Id + " (pid " + shell.ProcessId + ")")),
                    true, null,
                    "A background shell outlives the turn that started it, so it is still there for the next message. " +
                    "Stop one with \"taskkill /PID <pid> /T /F\", or leave it running and say so. Its output is in its " +
                    "log file, which read_file reads from the end.", null);
        }
    }

    private async Task RunTurnAsync(CancellationToken token)
    {
        _services.ApplyOptions();
        // Read once per turn, not once per round: the files are the same for the whole turn, and re-reading them
        // on every request would be three file reads for text that cannot have changed underneath us. A null root
        // yields "", so a solution that is not open costs the request nothing.
        var solutionRoot = await _view.SolutionRootAsync();
        // The folders this turn may touch: the solution plus any project the user added to this conversation.
        // Read once, for the same reason the root is: which chat this turn belongs to cannot change underneath it
        // without the turn having already been started somewhere else.
        _workspace = _services.WorkspaceFor(solutionRoot);
        _shellOwner = _services.ActiveSession.Id.ToString();
        var workspaceNotes = _workspace.Describe();
        var repoInstructions = RepoInstructions.Load(solutionRoot);
        // MCP is prepared before the first round: the servers' tools have to be in the request the model reads,
        // and a server that cannot be reached has to say so before the turn starts rather than after it stalls.
        // Only tool-calling turns connect anything — with the tools off the definitions would never be sent.
        var mcp = new McpTurnReport();
        // A server the user configured but this turn cannot offer is answered once, by name. Doing it before the
        // selection is also what lets the selection leave those tokens alone, so one word does not draw two rows.
        // It never connects anything: the answer is a sentence, not a retry.
        var inactiveMentions = McpDisabledMentions.Find(_services.Settings, solutionRoot, CurrentUserMessage());
        var explained = inactiveMentions.SelectMany(mention => mention.Tokens).ToList();
        if (_services.Settings.EnableToolCalling && _services.Settings.EnableMcp)
        {
            // An instruction that outlives the message it arrived in. The user should not have to say "use MCP"
            // again in every turn of a chat they have already said it about, and the sentence is the only place
            // that can be said without a control of its own. It is written before anything connects, so it is
            // remembered even by a turn whose servers then fail: what the chat was told is not a property of a
            // connection. The row is drawn once, when the answer changes - a chat state is not a per-turn event.
            var instruction = McpChatInstruction.Read(CurrentUserMessage());
            if (instruction != McpChatRequest.None)
            {
                var everyTurn = instruction == McpChatRequest.EveryTurn;
                if (_services.ActiveSession.McpEveryTurn != everyTurn)
                {
                    _services.ActiveSession.McpEveryTurn = everyTurn;
                    _view.SaveSession();
                    _view.AddToolNote("mcp", everyTurn
                            ? "MCP · this chat is now offered the servers' tools on every turn"
                            : "MCP · this chat is back to being offered MCP tools only by a message that names one",
                        true, null, everyTurn
                            ? "Asked for in a message rather than set here: it belongs to this conversation and is saved " +
                              "with it. Say something like \"don't use MCP in this chat anymore\" to stop."
                            : null, null);
                }
            }
            // Read once for the turn, like the token budget: a message in the middle of the chat cannot change the
            // offer of a turn that has already started.
            var offerMcpEveryTurn = _services.ActiveSession.McpEveryTurn;
            try
            {
                _view.Phase = "Connecting MCP servers";
                mcp = await _services.Mcp.PrepareTurnAsync(_services.Settings, solutionRoot, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Preparation is not supposed to fail — each server is caught on its own — but a defect in a
                // transport factory must still cost a row, not the whole turn.
                mcp.Rows.Add(new McpTurnRow { Summary = "MCP · preparation failed: " + McpLog.Redact(ex.Message), Ok = false });
                _services.Log("MCP preparation failed: " + ex);
            }
            foreach (var row in mcp.Rows) _view.AddToolNote("mcp", row.Summary, row.Ok, null, row.Detail, null);

            // What the message itself asked for is resolved against the tools preparation just offered. Doing it
            // here rather than per round means a tools/list_changed mid-turn cannot widen a narrowed turn: the
            // user chose these tools for this message. With the "only when the message asks" setting on, a
            // message that says nothing about MCP is offered none of them — that is what keeps an ordinary turn
            // from carrying every server's schemas and waiting on their lists. A chat that was told to use MCP
            // every turn takes the path the setting would have given it.
            _selection = McpToolSelection.Resolve(_services.Mcp.Snapshot, CurrentUserMessage(),
                _services.Settings.McpMaxToolsPerTurn, explained,
                offerWhenUnmentioned: !_services.Settings.McpToolsOnlyWhenMentioned || offerMcpEveryTurn);
            foreach (var row in _selection.Rows)
                _view.AddToolNote("mcp", row.Summary, row.Ok, null, row.Detail, null);
        }
        foreach (var mention in inactiveMentions)
            _view.AddToolNote("mcp", mention.Summary, false, null, mention.Detail, null);
        // The model is told what the window is told: a server named in the message is unavailable, and retrying it
        // would only spend a round on the same answer. With MCP off entirely this is the only MCP hint there is.
        _mcpHint = McpDisabledMentions.Combine(_selection.Hint, inactiveMentions);
        // Skills are prepared once per turn, for the same reason the repository instructions are: the folder can
        // change between turns, not between rounds of one turn. The registry's signature makes this a no-op when
        // nothing changed. A failure here costs a row, never the turn: a broken skills directory must not stop a
        // chat that never asked to use one.
        if (_services.Settings.EnableSkills)
        {
            try
            {
                _view.Phase = "Reading skills";
                var snapshot = await _services.PrepareSkillsAsync(solutionRoot, token).ConfigureAwait(true);
                _skillPlan = SkillPrompt.Build(snapshot, _services.ActiveSession.SelectedSkills,
                    _services.Settings.SkillAutomatic, _services.Settings.EnableToolCalling);
                foreach (var row in _skillPlan.Rows)
                    _view.AddToolNote("skill", row.Summary, row.Ok, null, row.Detail, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _view.AddToolNote("skill", "Skills · discovery failed: " + ex.Message, false, null, null, null);
                _services.Log("Skill discovery failed: " + ex);
            }
        }
        // Copilot Chat treats the round budget as a setting and asks before spending more rather than dying
        // at a fixed count. Clamp after ApplyOptions so a mid-session change still takes effect this turn.
        var limit = ChatStateLimits.ClampToolRounds(_services.Settings.MaxToolRounds);
        var stopped = false; ChatMessage? last = null; var finish = "";
        // What automatic verification last reported, and what the model is told when it answers with prose
        // instead of fixing it. One flag for both checks: from the model's side "the last verification
        // failed" is one state, and the reminder is only the wording that goes with whichever check it was.
        var unresolvedVerification = false;
        var repairReminder = "";
        var repairReminders = 0;
        var contextNoted = false;
        var mcpDropped = false;
        var transcript = _view.History;
        // Captured for the whole turn: the switch belongs to the chat the turn was started in, and flipping it
        // mid-turn must not change the transcript a loop that has already been running is built from.
        // The conversation's own numbers, captured once: the anchor the compaction estimate grows from, and the
        // turn a checkpoint is written back to. A user who switches chats mid-turn must not have either written
        // into whichever chat happens to be selected when a call returns.
        var session = _session;
        var ignoreHistory = session.IgnoreHistory;
        for (var round = 1; ; round++)
        {
            var assistant = new ChatMessage { Role = "assistant" };
            transcript.Add(assistant); last = assistant;
            finish = "";
            // With earlier messages ignored, the request is this turn and nothing older. ActiveTurn.Messages
            // already holds the user's message and every assistant/tool message of the rounds so far; the
            // assistant being built is added to it only after its call returns, so it is not in this list yet.
            // The full transcript is still what the window shows and what the session stores — only the request
            // copy is cut, the same way the checkpoint is. A null ActiveTurn (the user moved to another chat
            // mid-turn) keeps the full history: a running loop with no turn to scope it to must still produce a
            // valid request.
            var raw = ignoreHistory && _view.ActiveTurn != null
                ? _view.ActiveTurn.Messages.ToList()
                : transcript.Take(transcript.Count - 1).ToList();
            // The request opens with the conversation's checkpoint when it has one: the messages the checkpoint
            // stands for are not sent again. It is rebuilt from the transcript every round rather than carried
            // between rounds, because the transcript is what the window and the session follow and a request copy
            // must never become a second source of truth for it. `raw` stays the whole conversation, which is what
            // a fold's boundary is counted in — a checkpoint written two rounds ago and one written two turns ago
            // are then the same kind of number.
            var checkpoint = _view.ActiveTurn?.Compaction ?? _checkpoint;
            var prompt = !ignoreHistory && checkpoint != null && checkpoint.Covered > 0
                         && checkpoint.Covered <= raw.Count && checkpoint.Text.Length > 0
                ? ContextCompaction.Merge(raw, checkpoint.Covered, checkpoint.Text, ContextCompaction.RequestMessage(raw))
                : raw;
            var context = _services.Settings.EnableToolCalling
                ? _view.ActiveTurn?.Context
                : AgentPrompt.InlineFiles(_view.ActiveTurn?.Context, _view.ReadAttachedText);
            var errors = BuildDiagnostics.ToStandingNote(await SolutionBuild.ReadErrorListAsync().ConfigureAwait(true));
            // Shells that ended since the previous round, delivered the way the Error List is: in the request, not
            // in the transcript. A log read gives the output but never the fact that there will be no more of it, so
            // without this a model watching a server waits on a shell that died one round ago.
            foreach (var completion in _services.Shells.Drain(_shellOwner)) _shellNotices.Add(completion);
            var shellNotes = ShellNotice.Block(_shellNotices);
            // Null offers the live snapshot; a message that narrowed the turn, or said nothing about MCP at all,
            // pins exactly what it may call instead.
            var mcpTools = _selection.PinnedDefinitions;
            var tools = _services.ToolsFor(mcpTools, _skillPlan.OffersTools);
            var requestLimit = ModelRequestBudget.Limit(_model, _services.Settings);
            var fixedRequest = AgentPrompt.Build(new List<ChatMessage>(), context, _view.Mode, _services.Settings.EnableToolCalling, _services.Settings.EnableFileEditing, _services.Settings.EnableTerminalTools, repoInstructions, errors, mcp.Instructions, _mcpHint, _skillPlan.Instructions, workspaceNotes, shellNotes);
            var historyLimit = ModelRequestBudget.HistoryAllowance(requestLimit, fixedRequest, tools);
            // A conversation is compacted before it is shortened, and only when the request has actually
            // outgrown something: under the ceiling there is nothing to save, and a checkpoint would be a call
            // spent to make the request no smaller. Two figures decide it, the way opencode decides:
            //   - the ceiling, the model's window minus a buffer (the larger of 15% and 16 000 tokens by
            //     default), and
            //   - what the request is now: the prompt count the server reported for the last response, plus an
            //     estimate of everything that has changed since. The provider's count is a measurement and the
            //     difference is the estimate; there is no tokenizer here, so an unmeasured request is counted at
            //     two characters per token — the same ratio the character budget already converts a window with.
            // The character allowance stays as a second trigger rather than the decision: it is this build's own
            // hard bound, and reaching it without folding would refuse a turn that a fold could save.
            var measured = string.Equals(session.MeasuredPromptModel, _model.Key, StringComparison.Ordinal)
                ? session.MeasuredPromptTokens
                : 0;
            var requestChars = (long)RequestBudget.Size(prompt, tools) + RequestBudget.Size(fixedRequest, null);
            var estimatedTokens = ContextCompaction.Estimate(measured, session.MeasuredPromptChars, requestChars);
            var window = _model.ContextWindowTokens > 0 ? _model.ContextWindowTokens : _services.Settings.FallbackContextTokens;
            var ceiling = ContextCompaction.Ceiling(window, _services.Settings.CompactionBufferTokens);
            var overChars = historyLimit <= 0 || RequestBudget.Size(prompt, null) > historyLimit;
            if (round == 1)
                _view.AddToolNote("", "Context · ceiling " + (ceiling == int.MaxValue ? "none" : ceiling.ToString("N0"))
                        + " tokens (window " + window + ", buffer " + (ceiling == int.MaxValue ? 0 : window - ceiling)
                        + "); now about " + estimatedTokens + " tokens ("
                        + (measured > 0 ? measured + " measured + estimate since" : "estimated from size")
                        + "); history allowance " + Math.Max(0, historyLimit) + " characters",
                    true, null,
                    "Compaction is decided in tokens: the model's window less a buffer, against the last response's reported prompt count plus an estimate of what changed since. The character allowance is a separate hard safety bound on the built request. Neither is an exact token count.",
                    null);
            // The allowance is an estimate, so it decides only whether folding the old part is worth a call. Whether
            // the request may be sent is decided below, against the request that was actually built. A provider's
            // "too long" rejection has one more answer, below: fold and send the same round again. A chat that
            // ignores its earlier turns already sends only this turn, so there is nothing a checkpoint could stand
            // in for — its transcript is what the character bound and the provider's own ceiling still apply to.
            if (!ignoreHistory && _services.Settings.AutoCompaction &&
                (ContextCompaction.Due(estimatedTokens, ceiling) || overChars))
                prompt = await CompactAsync(raw, _model, token, CompactionTrigger.Auto, 0).ConfigureAwait(true);

            var bubble = _view.AddAssistantBubble(round == 1 ? "Thinking" : "Reading the tool results");
            // The size of the request that was finally sent, against which the provider's own prompt count is the
            // measurement. Recorded across the loop rather than read afterwards: the fit that produced it is local
            // to the attempt that succeeded.
            var sentChars = 0;
            // The provider's own ceiling is the one limit this request cannot argue with, and the estimate above has
            // just been proved low by it. So the round is built, sent, and on that one rejection the fold the
            // estimate declined is done after all — aimed below the size that was refused — and the same round is
            // sent again. The turn does not end one request short of the answer the user asked for.
            for (var attempt = 1; ; attempt++)
            {
                // Sanitizing is what keeps the request valid: every tool result must answer a call the assistant
                // announced and every announced call must be answered, or an OpenAI-compatible server rejects
                // the whole transcript. A cancelled tool batch or a history loaded from an older build can
                // otherwise leave the two out of step.
                var history = ChatStateLimits.Sanitize(prompt);
                // Built here, not stored: the system prompt and the attachment block are added at request time and
                // the transcript keeps only what the user typed and what the model answered.
                // With the tools switched off an attached file is only a path, and the model has no way to open it,
                // so a bounded excerpt goes with the request instead. The turn keeps the path either way, so the
                // chat and the stored session are unaffected by which way the setting is set.
                // The error list is read every round rather than once a turn, because its whole value is being
                // current and a round that just edited a file is exactly when it has changed. Read, not built —
                // it is a walk over items already in the window — and the block says so, so an entry that predates
                // this turn is not read as the result of it. It joins the system prompt and never _history: that is
                // rebuilt per request, so this needs no sanitizing, costs the stored conversation nothing, and
                // goes away by itself once the list is clean.
                var request = AgentPrompt.Build(history, context, _view.Mode, _services.Settings.EnableToolCalling, _services.Settings.EnableFileEditing, _services.Settings.EnableTerminalTools, repoInstructions, errors, mcp.Instructions, _mcpHint, _skillPlan.Instructions, workspaceNotes, shellNotes);

                // Measured, not estimated. What the transport refuses on is this serialized request, and the
                // allowance above is a few thousand characters short of it by construction: the instruction block is
                // costed before it is written, the JSON around the text costs more than the text, and a server can
                // add a tool between two rounds. Nothing is dropped to fit — an old result loses only its body and
                // keeps the call it answers — because a result that answers no call is refused by the whole server.
                var attemptTools = tools;
                var sentMcpTools = mcpTools;
                var fit = RequestFitting.Fit(request, attemptTools, requestLimit);
                if (!fit.Fits)
                {
                    // Still over by measurement, so the round continues without the MCP schemas instead of the turn
                    // ending on them. They are the one part of a request that can be left out and still leave a turn
                    // that works: the built-in tools stay, and the rounds already spent behind it stay with them. The
                    // prompt is rebuilt without the MCP block, because a server offered no MCP tool must not be told
                    // about one. A turn that gets here has lost nothing it had: it never had a sendable request.
                    var plainTools = _services.ToolsFor(Array.Empty<ToolDefinition>(), _skillPlan.OffersTools);
                    var plainRequest = AgentPrompt.Build(history, context, _view.Mode, _services.Settings.EnableToolCalling, _services.Settings.EnableFileEditing, _services.Settings.EnableTerminalTools, repoInstructions, errors, null, null, _skillPlan.Instructions, workspaceNotes, shellNotes);
                    var plain = RequestFitting.Fit(plainRequest, plainTools, requestLimit);
                    // Only worth taking if the schemas are lighter than they were: with no MCP tool in the request
                    // this is the same request again, and saying it was sent without them would be false.
                    if (plain.ToolChars >= fit.ToolChars) throw new InvalidOperationException(fit.Describe());
                    // Shortening reached its floor and the schemas were already left out: say what the request is
                    // made of rather than only that it is too big, so the reader knows which part to give up.
                    if (!plain.Fits) throw new InvalidOperationException(plain.Describe());
                    fit = plain;
                    attemptTools = plainTools;
                    sentMcpTools = Array.Empty<ToolDefinition>();
                    // Once a turn: the transcript only grows, so from here every round takes the same path, and the
                    // row would otherwise be repeated for the rest of the turn.
                    if (!mcpDropped)
                    {
                        mcpDropped = true;
                        _view.AddToolNote("mcp", "MCP · this turn continues without MCP tool schemas: the request was over the model's budget with them, so only the built-in tools can be called", true, null, "The transcript is shortened as far as it can be first — old tool results lose their bodies and keep the calls they answer — so this is the schemas being the part that does not fit, not the conversation.", null);
                    }
                }
                // Note the shortening once a turn, not once a round: the same old results qualify from the round the
                // budget is first over, and a row every round would bury the transcript.
                if (fit.Shortened > 0 && !contextNoted)
                {
                    contextNoted = true;
                    _view.AddToolNote("", "Context · " + fit.Shortened + " older tool result" + (fit.Shortened == 1 ? "" : "s") + " shortened to fit the request", true, null, null, null);
                }

                _view.Streamed = 0; _view.Phase = round == 1 ? "Thinking" : "Reading the tool results";
                // What the anchor below is the measured half of: the request's conversation and schemas, without the
                // instruction block, which the provider's count already covers and which is built the same way every
                // round. The same measure the next round's estimate uses, so what is subtracted is what grew.
                sentChars = (int)Math.Min(int.MaxValue, Math.Max(0L,
                    RequestBudget.Size(fit.Messages, sentMcpTools) - RequestBudget.Size(fixedRequest, null)));
                // A narrowed turn sends exactly the tools the message selected, a turn whose message never mentioned
                // MCP sends none of them, and otherwise the live snapshot is used, so a list change between rounds
                // still reaches the model on the next one.
                try
                {
                    if (_services.Settings.EnableStreaming)
                        try
                        {
                            await _services.StreamAsync(_model, fit.Messages, async update =>
                            {
                                // A continuation is a seam in the answer, so it gets a row of its own: the user has to be
                                // able to tell a proxy timeout from the model changing its mind about what it was writing.
                                if (update.Resumed)
                                    await _view.InvokeAsync(() => _view.AddToolNote("stream", "Stream · the connection dropped mid-answer; the rest of the answer was asked for and appended", true, null, null, null));
                                assistant.Content += update.ContentDelta; assistant.Reasoning += update.ReasoningDelta;
                                if (update.ToolCalls.Count > 0) assistant.ToolCalls = update.ToolCalls;
                                if (update.FinishReason.Length > 0) finish = update.FinishReason;
                                await _view.InvokeAsync(() =>
                                {
                                    _view.Streamed = assistant.Content.Length;
                                    _view.Phase = assistant.Content.Length > 0 ? "Writing" : "Thinking";
                                    _view.SetContent(bubble, assistant);
                                });
                            }, token, sentMcpTools, _skillPlan.OffersTools).ConfigureAwait(true);
                        }
                        catch
                        {
                            // A stream the client could not finish still has the text it did deliver, and the bubble is
                            // already showing it. Recording it is what keeps the partial answer after the session is
                            // reloaded; an announced tool call is left out, because a call with no result would put the
                            // stored transcript out of step and Sanitize would have to cut it on the next request.
                            if (assistant.Content.Length > 0 && assistant.ToolCalls.Count == 0)
                            {
                                _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(assistant));
                                _view.SaveSession();
                            }
                            throw;
                        }
                    else
                    {
                        var complete = await _services.CompleteAsync(_model, fit.Messages, token, sentMcpTools, _skillPlan.OffersTools).ConfigureAwait(true);
                        assistant.Content = complete.Content; assistant.Reasoning = complete.Reasoning; assistant.ToolCalls = complete.ToolCalls;
                        _view.SetContent(bubble, assistant);
                    }
                }
                catch (LiteLlmException ex) when (attempt <= ContextCompaction.ShrinkSteps.Length
                                                  && !ignoreHistory && _services.Settings.AutoCompaction
                                                  && ContextOverflow.IsOverflow(ex))
                {
                    // The bubble is reused rather than replaced: a rejected request produced no answer, and a second
                    // bubble under the first would read as a second attempt at answering. Anything a stream managed to
                    // deliver before the rejection is cleared with it.
                    assistant.Content = ""; assistant.Reasoning = ""; assistant.ToolCalls = new List<ToolCall>();
                    _view.SetContent(bubble, assistant);
                    var budget = ContextCompaction.OverflowBudget(estimatedTokens, ceiling, attempt - 1);
                    prompt = await CompactAsync(raw, _model, token, CompactionTrigger.Overflow, attempt, budget).ConfigureAwait(true);
                    continue;
                }
                break;
            }
            // Read after the call, not before: a streamed answer reports its usage on the last chunk, which has
            // not arrived when the first token does.
            _usage.Add(_services.LastUsage);
            // The anchor the next round's estimate grows from: the provider's own prompt count for this call, and the
            // size of the request that produced it. A call that reported nothing leaves the previous anchor in place —
            // a missing usage line is not a measurement of zero, and the conversation is then estimated from size.
            var promptUsage = _services.LastUsage;
            if (promptUsage != null && promptUsage.PromptTokens > 0)
            {
                session.MeasuredPromptTokens = promptUsage.PromptTokens;
                session.MeasuredPromptChars = sentChars;
                session.MeasuredPromptModel = _model.Key;
            }
            _view.LastExchange = _services.LastExchange; bubble.Exchange = _view.LastExchange;   // attach the raw JSON to the bubble that came from it
            // One assistant message per round, appended in order. Overwriting the turn's first assistant
            // instead would record every round's calls against a single message, leaving the stored turn
            // with results that answer nothing and calls that are never answered.
            _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(assistant));
            _view.FinishStatus(bubble);

            if (assistant.ToolCalls.Count == 0)
            {
                // A prose answer does not resolve a failed verification build. Give the model one explicit
                // correction round instead of accepting a false-success answer, but keep the retry bounded so
                // a model that refuses to edit cannot spin forever.
                if (unresolvedVerification && repairReminders++ == 0)
                {
                    var reminder = new ChatMessage { Role = "user", Content = repairReminder };
                    transcript.Add(reminder);
                    _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(reminder));
                    _view.SaveSession();
                    if (round < limit) continue;
                }
                _view.SaveSession();
                break;
            }

            var revisionsAtRoundStart = EditTools.Revisions;
            // Consecutive disk-only calls run together: each is a file read whose answer cannot depend on the one
            // before it, so awaiting them one at a time buys nothing. A run of them is batched, and everything
            // else stays serial — an edit or a command asks the user through a card, and those must keep both
            // their order and their readability. The results are appended in the order the calls were announced,
            // never the order they finished, or the answers stop lining up with the calls they answer.
            var calls = assistant.ToolCalls;
            for (var index = 0; index < calls.Count; )
            {
                if (!ContextTools.IsDiskOnly(calls[index].Name))
                {
                    await RunToolAsync(calls[index], token).ConfigureAwait(true);
                    index++;
                    continue;
                }
                var end = index;
                while (end < calls.Count && ContextTools.IsDiskOnly(calls[end].Name)) end++;
                await RunTogetherAsync(calls, index, end, token).ConfigureAwait(true);
                index = end;
            }

            // Whether this round's build step ran and came back clean, which is the only state in which the
            // assemblies on disk are the ones this round just wrote. Per round rather than per turn: a later
            // round that only reads files must not run the tests again on the strength of an earlier build.
            var buildVerified = false;

            // The build check is what stops an edit that does not compile from being reported as a success: it
            // runs once the round's edits are in, on the whole solution, and only when a file actually changed —
            // a round that only read files has nothing to verify. It runs again on every later round that edits
            // something, until the Error List comes back empty: a fixed number of attempts used to end the turn
            // with the errors still listed, which is the failure this feature exists to prevent. The round
            // budget below is what bounds it, and that asks the user before it grows.
            if (_services.Settings.VerifyBuildAfterEdits && _view.Mode == "agent" && _services.Settings.EnableFileEditing
                && EditTools.Revisions > revisionsAtRoundStart)
            {
                _view.Phase = "Building the solution";
                  var verification = await SolutionBuild.VerifyAsync(token).ConfigureAwait(true);
                  var verifiedReport = verification.Report();
                  if (verifiedReport != null)
                  {
                      var report = verifiedReport;
                    if (_view.ActiveTurn != null) _view.ActiveTurn.Build = report;
                    _view.AddToolNote("build", report.Summary, report.Errors == 0, null, report.Detail, null);
                    // Handed back as a user message rather than a tool result: a tool result answers a call the
                    // assistant announced, and this one answers nothing, so ChatStateLimits.Sanitize would drop
                    // it before the model ever saw it. Persist it on the active turn as well: _history is rebuilt
                    // from turn messages after a reload/continuation, and losing this note loses the repair task.
                      buildVerified = verification.CanRunTests;
                    unresolvedVerification = !buildVerified;
                    if (unresolvedVerification)
                    {
                        var buildNote = new ChatMessage { Role = "user", Content = BuildDiagnostics.ToModelNote(report) };
                        transcript.Add(buildNote);
                        _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(buildNote));
                        repairReminder = BuildDiagnostics.RepairReminder;
                        repairReminders = 0;
                  }
                  else
                      _view.AddToolNote("build", "Build · not verified: " + verification.Problem, false, null, null, null);
                }
            }

            // The tests run on top of a build that passed, never instead of it: what is on disk is then the
            // code in the editor, so a failure is a real one. Skipped when the build failed or could not be
            // verified, because a test run against the previous round's binaries reports on code that has
            // been replaced, and a green result from stale assemblies is worse than no result at all.
            if (buildVerified && _services.Settings.VerifyTestsAfterEdits)
            {
                _view.Phase = "Running the tests";
                var (tests, problem) = await SolutionTests.RunAsync(token).ConfigureAwait(true);
                if (tests != null)
                {
                    if (_view.ActiveTurn != null) _view.ActiveTurn.Tests = tests;
                    _view.AddToolNote("tests", tests.Summary, tests.Errors == 0, null, tests.Detail, null);
                    if (tests.Errors > 0)
                    {
                        var testNote = new ChatMessage { Role = "user", Content = TestDiagnostics.ToModelNote(tests) };
                        transcript.Add(testNote);
                        _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(testNote));
                        unresolvedVerification = true;
                        repairReminder = TestDiagnostics.RepairReminder;
                        repairReminders = 0;
                    }
                }
                else if (problem.Length > 0)
                {
                    // Said out loud rather than passed over: a check that did not happen and a check that
                    // found nothing draw the same silent transcript otherwise, and only one of them means
                    // the code is fine.
                    _view.AddToolNote("tests", TestDiagnostics.ToUnverifiedLine(problem), false, null, null, null);
                }
            }
            _view.SaveSession();   // once per round, not per tool: each save re-serializes the whole document

            // The budget is only consulted once the results are in, so what goes back to the model is always
            // a complete exchange — the same reason Copilot Chat checks at the top of its loop.
            if (round < limit) continue;
            if (ChatStateLimits.AtToolRoundsCeiling(limit))
            {
                stopped = true;
                _view.AddToolNote("", "Stopped at the " + ChatStateLimits.MaxToolRoundsCeiling + "-round ceiling. The results so far are kept — send another message to continue from them.", false, null, null, null);
                break;
            }
            var raised = ChatStateLimits.ExtendToolRounds(limit);
            if (!await _view.AskToContinueAsync(limit, raised, token).ConfigureAwait(true)) { stopped = true; break; }
            limit = raised;
        }
        // Skipped when the user chose to stop: `last` is then the tool request they declined to continue, so
        // its emptiness says nothing about the model running out of budget, and the stop note is already up.
        if (!stopped && last != null && last.Content.Length == 0 && last.ToolCalls.Count == 0)
        {
            if (last.Reasoning.Length > 0)
                _view.AddToolNote("", "The model spent its whole " + _services.Settings.MaxTokens + "-token budget on reasoning and never wrote an answer. Raise \"Maximum tokens\" in Tools ▸ Options ▸ LiteLLM.", false, null, null, null);
            else if (finish == "length") _view.AddToolNote("", "The reply hit the " + _services.Settings.MaxTokens + "-token limit before it produced any text.", false, null, null, null);
            else throw new LiteLlmException("The model returned an empty response.", "No content or tool calls.");
        }
        else if (!stopped && finish == "length") _view.AddToolNote("", "The reply was cut off at the " + _services.Settings.MaxTokens + "-token limit.", false, null, null, null);
        var turn = _view.ActiveTurn;
        if (turn != null) { turn.Status = ChatTurnStatus.Complete; turn.CompletedUtc = DateTime.UtcNow; }
        // The turn's own undo comes before the cost row: the two are footnotes, and the one the user may act on
        // should not sit under the one they only read. Both are drawn once, at the end — a row per round would
        // bury the transcript in arithmetic.
        if (_edits.Count > 0) _view.AddUndoAllNote(_edits);
        if (_usage.Any)
        {
            // The ledger is told even when the window has no active turn (the user switched chats mid-turn): the
            // calls were made and paid for, and the ledger answers what the model cost, not what one conversation
            // still remembers.
            _services.RecordUsage(_model.Key, _usage);
            if (turn != null) turn.Usage = _usage;
            _view.AddToolNote("", _usage.Line, true, null, null, null);
        }
        _view.SaveSession(); _view.Status = string.Empty;
    }

    /// <summary>One call, awaited in series. Used for everything that is not a disk read: the edit tools, the
    /// commands and the Git wrappers all switch to the UI thread or ask the user, and a batch of those is not a
    /// batch at all.</summary>
    private async Task RunToolAsync(ToolCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = _services.Settings;
        var refusal = ToolExecutionPolicy.Refusal(call.Name, settings.EnableToolCalling, settings.EnableFileEditing, settings.EnableTerminalTools);
        if (refusal != null)
        {
            Append(call, new ToolResult { Ok = false, Summary = call.Name + " · disabled", Content = refusal }, EditTools.Revisions);
            return;
        }
        _view.Phase = "Running " + call.Name;
        // The revision count says whether this call produced an edit, which is what puts the diff and the
        // undo on this row rather than on the previous one.
        var revisions = EditTools.Revisions;
        // The snapshot is the only dispatch authority for an MCP name. An unknown name falls through to
        // ContextTools exactly as it did before MCP existed, so its own "unknown tool" answer is unchanged.
        // A name that exists but was not selected is refused here, before the policy and before the transport: the
        // transcript must still answer the announced call, and the user narrowed the turn on purpose.
        // Skill tools are resolved first. They were offered only when this turn prepared a skill plan, and a call
        // for them when it did not is refused here rather than falling through to ContextTools' "unknown tool".
        ToolResult result;
        if (call.Name == AgentPlanTool.Name)
        {
            if (_view.ActiveTurn == null)
                result = new ToolResult { Ok = false, Summary = "Plan · no active turn", Content = "No active turn owns this plan." };
            else if (!AgentPlan.TryRead(call.Arguments, out var plan, out var error))
                result = new ToolResult { Ok = false, Summary = "Plan · invalid arguments", Content = error };
            else
            {
                _view.ActiveTurn.AgentPlan = plan;
                _view.SaveSession();
                _view.RefreshAgentPlan();
                result = new ToolResult { Ok = true, Summary = "Plan · updated", Content = "Plan displayed and saved. Status is model-reported only; tool approvals and verification remain separate." };
            }
        }
        else if (SkillTools.Handles(call.Name))
        {
            var skillResult = _skillPlan.OffersTools
                ? SkillTools.Execute(_skillPlan.Snapshot, call.Name, call.Arguments, token)
                : new SkillToolResult
                {
                    Content = "No skill is available to this turn, so the call was not run. The skills extension is " +
                              "off, tool calling is off, or no skill is discovered.",
                    Summary = "Skill · " + call.Name + " is not available this turn",
                    Ok = false
                };
            result = new ToolResult { Content = skillResult.Content, Summary = skillResult.Summary, Ok = skillResult.Ok };
        }
        else
        {
            result = _services.Mcp.TryResolveTool(call.Name, out var mcpTool)
                ? _selection.Allows(call.Name)
                    ? await RunMcpToolAsync(call, mcpTool, token).ConfigureAwait(true)
                    : RefuseUnselected(mcpTool)
                : await ContextTools.ExecuteAsync(LiteLlmPackage.Instance, call.Name, call.Arguments, token, _workspace, _shellOwner).ConfigureAwait(true);
        }
        Append(call, result, revisions);
    }

    /// <summary>Answers a call the turn may not make. It is an ordinary tool result, so the call stays paired and the
    /// model learns why instead of being told the tool does not exist.</summary>
    private ToolResult RefuseUnselected(McpToolInfo tool)
    {
        var label = tool.Server.Name + " · " + tool.Name;
        if (_selection.Suppressed)
            return new ToolResult
            {
                Content = "The MCP tool '" + label + "' was not offered for this turn, so it was not run: the " +
                          "message did not mention MCP, and none of the servers' tools are offered to a turn that " +
                          "does not. Ask the user to name the tool with # if they want it. Do not claim it ran.",
                Summary = label + " · not offered this turn",
                Ok = false
            };
        var selected = _selection.Selected.Take(8)
            .Select(chosen => chosen.Server.Name + " · " + chosen.Name)
            .ToList();
        var list = selected.Count == 0
            ? "none"
            : string.Join(", ", selected) + (_selection.Selected.Count > selected.Count ? ", …" : "");
        return new ToolResult
        {
            Content = "The MCP tool '" + label + "' was not selected for this turn, so it was not run. Selected this " +
                      "turn: " + list + ". Do not call an MCP tool outside that list; ask the user to select it with " +
                      "# if they want it.",
            Summary = label + " · not selected this turn",
            Ok = false
        };
    }

    /// <summary>The message this turn is about: the first user message of the active turn. Re-read rather than
    /// remembered because the turn is what retry and continuation restore; null means no turn to scope it to.</summary>
    private string? CurrentUserMessage() =>
        _view.ActiveTurn?.Messages.FirstOrDefault(message => message.Role == "user")?.Content;

    /// <summary>One MCP call: validate the arguments, apply the approval rule, ask if the rule says so, then run it.
    ///
    /// A denial is a result, not an exception — the call id still needs an answer — and so is every failure. The
    /// approval handler is a static delegate so the harness never waits on a window it does not own; when no window
    /// installed one, the safe reading is "not approved", never "run it".</summary>
    private async Task<ToolResult> RunMcpToolAsync(ToolCall call, McpToolInfo tool, CancellationToken token)
    {
        var label = tool.Server.Name + " · " + tool.Name;
        if (!ToolArguments.TryParse(call.Arguments, out var arguments, out var argumentsError))
            return new ToolResult { Content = argumentsError, Summary = "invalid arguments", Ok = false };

        var allowAll = string.Equals(_services.Settings.PermissionMode, "allowAll", StringComparison.OrdinalIgnoreCase);
        var decision = McpPolicy.Decide(tool, allowAll, _services.Settings.McpAutoApproveReadOnly);
        if (decision == McpDecision.Deny)
            return new ToolResult
            {
                Content = "The MCP tool '" + label + "' is disabled, so it was not run.",
                Summary = label + " · disabled",
                Ok = false
            };

        if (decision == McpDecision.Ask)
        {
            var confirm = McpApproval.Confirm;
            var approved = confirm != null &&
                           await confirm(new McpApprovalRequest { Tool = tool, Arguments = arguments })
                               .ConfigureAwait(true);
            if (!approved)
                return new ToolResult
                {
                    Content = "The user declined to run the MCP tool '" + label + "'. Do not call it again unless the user asks.",
                    Summary = label + " · declined",
                    Ok = false
                };
        }

        try
        {
            var formatted = await _services.Mcp
                .CallToolAsync(tool, arguments, update => ReportMcpProgress(call.Name, update), token)
                .ConfigureAwait(true);
            return new ToolResult { Content = formatted.Content, Summary = formatted.Summary, Ok = formatted.Ok };
        }
        catch (OperationCanceledException) { throw; }
        catch (McpException ex)
        {
            // A 401 is an instruction, not a generic failure: without it the model retries a call that cannot
            // succeed until the user signs in.
            var text = ex.AuthRequired
                ? "MCP server '" + tool.Server.Name + "' requires you to sign in. Open the MCP tab in the Dashboard, sign in, and try again."
                : "MCP tool '" + label + "' failed: " + ex.Message;
            return new ToolResult { Content = text, Summary = label + " · failed", Ok = false };
        }
        catch (Exception ex)
        {
            return new ToolResult { Content = "MCP tool '" + label + "' failed: " + ex.Message, Summary = label + " · failed", Ok = false };
        }
    }

    /// <summary>A server may report progress far faster than a status line can be read, so the phase is written at
    /// most four times a second. The final row is drawn by <see cref="Append"/> whether or not any update arrived.</summary>
    private void ReportMcpProgress(string name, McpProgressUpdate update)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastProgressTicks);
        if (now - last < TimeSpan.FromMilliseconds(250).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last) return;

        var text = "Running " + name;
        if (update.Total.HasValue && update.Total.Value > 0)
            text += " · " + (int)Math.Round(100 * (update.Progress ?? 0) / update.Total.Value) + "%";
        else if (update.Progress.HasValue)
            text += " · " + update.Progress.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(update.Message)) text += " · " + update.Message;
        try { _ = _view.InvokeAsync(() => _view.Phase = text); } catch (Exception) { }
    }

    /// <summary>The announced calls from <paramref name="start"/> to <paramref name="end"/>, run at the same time
    /// and appended in the order they were announced. Nothing in this batch can edit a file, so the revision
    /// baseline is read once and cannot be crossed by any of them.</summary>
    private async Task RunTogetherAsync(IReadOnlyList<ToolCall> calls, int start, int end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var revisions = EditTools.Revisions;
        var running = new List<Task<ToolResult>>(end - start);
        for (var index = start; index < end; index++)
        {
            _view.Phase = "Running " + calls[index].Name;
            running.Add(ContextTools.ExecuteAsync(LiteLlmPackage.Instance, calls[index].Name, calls[index].Arguments, token, _workspace, _shellOwner));
        }
        var results = await Task.WhenAll(running).ConfigureAwait(true);
        for (var index = 0; index < results.Length; index++) Append(calls[start + index], results[index], revisions);
    }

    /// <summary>The tool message the request needs and the one-line row the user sees. Both are appended here so
    /// the serial and the batched paths cannot drift apart in what they record.</summary>
    private void Append(ToolCall call, ToolResult result, int revisions)
    {
        var toolMessage = new ChatMessage { Role = "tool", ToolCallId = call.Id, ToolName = call.Name, Content = result.Content };
        _view.History.Add(toolMessage);
        _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(toolMessage));
        if (EditTools.Revisions <= revisions)
        {
            _view.AddToolNote(call.Name, result.Summary, result.Ok, null, result.Detail, null);
            return;
        }

        // The call edited something. The row's own undo names the last edit; the turn keeps every one, because
        // a patch can change six files and putting back one of them is not putting back the turn.
        foreach (var edit in EditTools.EditsSince(revisions))
            if (!_edits.Contains(edit)) _edits.Add(edit);
        _view.AddToolNote(call.Name, result.Summary, result.Ok, null, null, EditTools.Latest);
    }

    /// <summary>A compaction the user asked for with <c>/compact</c>. It is the automatic path with a different
    /// trigger and no caller waiting on the request it returns: the checkpoint it writes is what the next turn
    /// opens with, and nothing is sent, so an idle chat stays idle. The turn the command opened stores the row, so
    /// a reloaded window draws it back.</summary>
    internal async Task CompactNowAsync(CancellationToken token)
    {
        var messages = _view.History.ToList();
        if (messages.Count == 0)
        {
            _view.AddToolNote("", "Nothing to compact: this conversation has no messages yet.", false, null, null, null);
            return;
        }
        await CompactAsync(messages, _model, token, CompactionTrigger.Manual, 1).ConfigureAwait(true);
        _view.Phase = "";
    }

    /// <summary>Folds the material between the checkpoint already in hand and the verbatim tail into one structured
    /// checkpoint, written by the utility model.
    ///
    /// <paramref name="messages"/> is the whole conversation, so the boundary this writes — the checkpoint's
    /// <see cref="CompactionCheckpoint.Covered"/> — is an index into the transcript and not into the folded request:
    /// the same number names the same messages on the next round and on the next turn. The tail kept word for word
    /// is read from its end, so what it keeps is what the model is still working on.
    ///
    /// Every failure returns the transcript folded no further than it was: a checkpoint is an improvement to the
    /// request and never something a turn depends on. <paramref name="attempt"/> is the overflow attempt that asked
    /// for this and <paramref name="budgetTokens"/> its target size; when the provider rejected the request outright
    /// the tail is cut to fit inside that target rather than at the configured keep.</summary>
    private async Task<List<ChatMessage>> CompactAsync(List<ChatMessage> messages, ModelInfo model, CancellationToken token,
        CompactionTrigger trigger, int attempt, int budgetTokens = 0)
    {
        var checkpoint = _view.ActiveTurn?.Compaction ?? _checkpoint;
        var covered = checkpoint != null && checkpoint.Text.Length > 0 ? checkpoint.Covered : 0;
        // A stored transcript is head-sliced on the way to disk, so a boundary from an earlier turn can name
        // messages that are no longer there. Starting over costs one call; trusting it would skip the messages the
        // checkpoint never stood for.
        if (covered < 0 || covered > messages.Count) covered = 0;
        var previous = covered > 0 ? checkpoint!.Text : null;
        var pinned = ContextCompaction.RequestMessage(messages);
        var keep = budgetTokens > 0
            ? ContextCompaction.OverflowKeep(budgetTokens, _services.Settings.CompactionKeepTokens)
            : ContextCompaction.EffectiveKeep(_services.Settings.CompactionKeepTokens);
        var keepFrom = ContextCompaction.KeepFrom(messages, keep);

        var utility = UtilityModel(model);
        var utilitySettings = ContextCompaction.UtilitySettings(_services.Settings);
        utilitySettings.FallbackContextTokens = _services.Settings.FallbackContextTokens;
        utilitySettings.ModelContextOverrides = _services.Settings.ModelContextOverrides;
        // The span handed over is bounded by what the utility model can read, minus what the instruction and the
        // checkpoint in hand already cost, so the request that writes the checkpoint is one this build will send.
        // A fold whose span is too big folds less this time and the rest on the next round.
        var update = previous != null;
        var instruction = ContextCompaction.BuildPrompt(update, update && !ContextCompaction.Filled(previous));
        var room = (long)ModelRequestBudget.Limit(utility, utilitySettings) - instruction.Length - (previous?.Length ?? 0) - 2000;
        var span = (int)Math.Max(ContextCompaction.MinSpanChars, Math.Min(ContextCompaction.MaxSpanChars, room));

        if (!ContextCompaction.TryBuildRequest(messages, covered, keepFrom, previous, out var request, out var through, span))
            return NothingToFold(messages, covered, previous, pinned, trigger);

        try
        {
            _view.Phase = trigger == CompactionTrigger.Manual ? "Compacting the conversation" : "Compacting the older messages";
            _view.Streamed = 0;
            var answer = await _services.CompleteInternalAsync(utility, request, utilitySettings, token).ConfigureAwait(true);
            _usage.Add(_services.LastUsage);   // the checkpoint is a call the turn made, so it is part of what it cost
            var text = answer.Content.Trim();
            // A model that answers with prose, or with nothing at all, has not written a checkpoint. It gets one
            // reminder — the same single retry opencode makes — because the instruction is long and a model can miss
            // it once. A second miss is a model that cannot follow the template, and the older checkpoint stands.
            if (!ContextCompaction.Filled(text))
            {
                request.Add(new ChatMessage { Role = "user", Content = ContextCompaction.Nudge });
                answer = await _services.CompleteInternalAsync(utility, request, utilitySettings, token).ConfigureAwait(true);
                _usage.Add(_services.LastUsage);
                text = answer.Content.Trim();
            }
            if (!ContextCompaction.Filled(text))
            {
                _view.AddToolNote("", "Context · the utility model (" + utility.Id + ") did not return a checkpoint in the required sections, so the older messages were left in place", false, null,
                    "A checkpoint has to carry the template's headings. One written as prose would read to the next model as an instruction rather than as established context, so it is taken as a failed compaction — which costs the request the fold, never the turn.", null);
                _services.Log("Compaction rejected: no template heading in a reply of " + text.Length + " characters");
                return covered > 0 ? ContextCompaction.Merge(messages, covered, previous!, pinned) : messages;
            }

            text = ChatStateLimits.Clip(text, ContextCompaction.MaxSummaryChars);
            var report = ContextCompaction.Report(through, text, ContextCompaction.Describe(messages, through, trigger), trigger);
            var turn = _view.ActiveTurn;
            if (turn != null)
            {
                turn.Compaction = new CompactionCheckpoint { Covered = through, Text = text, UpdatedUtc = DateTime.UtcNow };
                // Bounded like everything else a turn keeps: a turn that compacts more often than this is a loop.
                if (turn.Summaries.Count < ContextCompaction.MaxPerTurn) turn.Summaries.Add(report);
            }
            _view.AddCompactionNote(report);
            // The anchor described the request as it was before this fold, so it measures nothing now. The next
            // round estimates from the request's own size until a call reports a new count of its own.
            _session.MeasuredPromptTokens = 0;
            _session.MeasuredPromptChars = 0;
            _session.MeasuredPromptModel = "";
            _services.Log("Conversation compacted with " + utility.Id + ": " + through + " messages, " + text.Length
                + " characters, " + trigger + (attempt > 1 ? " attempt " + attempt : ""));
            _view.SaveSession();
            return ContextCompaction.Merge(messages, through, text, pinned);
        }
        catch (OperationCanceledException) { throw; }   // the user stopped; that is not a compaction failure
        catch (Exception ex)
        {
            _view.AddToolNote("", "Context · could not write a checkpoint (" + ex.Message + "), so the request was sent without one", false, null, null, null);
            _services.Log("Compaction failed: " + ex);
            return covered > 0 ? ContextCompaction.Merge(messages, covered, previous!, pinned) : messages;
        }
    }

    /// <summary>The row for a compaction that was due but had nothing new to fold: the newest messages are already
    /// everything between the checkpoint and now. Once a turn for the automatic path, because every later round
    /// would repeat it; once per command for the manual one, because the user asked for an answer.</summary>
    private List<ChatMessage> NothingToFold(List<ChatMessage> messages, int covered, string? previous, ChatMessage? pinned,
        CompactionTrigger trigger)
    {
        if (trigger != CompactionTrigger.Auto || !_nothingToCompactNoted)
        {
            _nothingToCompactNoted = true;
            _view.AddToolNote("", trigger == CompactionTrigger.Manual
                    ? "Nothing to compact: the newest messages already fit inside the keep the setting reserves, so no call was spent"
                    : "Context · compaction was due but there was nothing before the verbatim tail to fold into a checkpoint",
                false, null,
                "The tail kept word for word is \"Keep beside the checkpoint (tokens)\" in Options · Context: lower it to fold more of the conversation, or send more messages so something sits before the tail. A request that is over the ceiling with nothing foldable is shortened by the character bound instead, and the provider's own ceiling still applies.", null);
        }
        return covered > 0 ? ContextCompaction.Merge(messages, covered, previous!, pinned) : messages;
    }

    /// <summary>The model the extension thinks with: the configured utility model, or the one the chat is already
    /// talking to. A configured model that no longer resolves falls back rather than failing — the setting names
    /// a model that may since have been removed from the server, and a summary is never worth failing a turn
    /// over. The id alone is accepted as well as the full key, because the key carries a server GUID and nobody
    /// should have to type one.</summary>
    private ModelInfo UtilityModel(ModelInfo fallback)
    {
        var key = (_services.Settings.UtilityModelKey ?? "").Trim();
        if (key.Length == 0) return fallback;
        return _services.Catalog.Match(key)
            ?? _services.Models.FirstOrDefault(m => string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }
}
