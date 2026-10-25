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
    /// <summary>What this turn's skill scan found, and the instructions built from it. Read by the tool dispatch
    /// and by the round that builds the request; both come from the same snapshot.</summary>
    private SkillPlan _skillPlan = SkillPlan.None;

    internal AgentHarness(IAgentView view, ExtensionServices services, ModelInfo model)
    {
        _view = view;
        _services = services;
        _model = model;
    }

    internal async Task RunAsync(CancellationToken token)
    {
        _services.ApplyOptions();
        // Read once per turn, not once per round: the files are the same for the whole turn, and re-reading them
        // on every request would be three file reads for text that cannot have changed underneath us. A null root
        // yields "", so a solution that is not open costs the request nothing.
        var solutionRoot = await _view.SolutionRootAsync();
        var repoInstructions = RepoInstructions.Load(solutionRoot);
        // MCP is prepared before the first round: the servers' tools have to be in the request the model reads,
        // and a server that cannot be reached has to say so before the turn starts rather than after it stalls.
        // Only tool-calling turns connect anything — with the tools off the definitions would never be sent.
        var mcp = new McpTurnReport();
        if (_services.Settings.EnableToolCalling && _services.Settings.EnableMcp)
        {
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

            // MCP on with every configured server disabled is not a failure — but the turn then silently goes
            // without the tools the user may be waiting on, so it says so before the first round rather than
            // leaving the absence to be noticed later.
            var configured = _services.Settings.McpServers ?? new List<McpServerConfiguration>();
            if (configured.Count > 0 &&
                !configured.Any(server => server != null && server.Enabled && server.AppliesTo(solutionRoot)))
                _view.AddToolNote("mcp",
                    "MCP · no server is enabled for this solution, so this turn has no MCP tools.", false, null, null,
                    null);

            // What the message itself asked for is resolved against the tools preparation just offered. Doing it
            // here rather than per round means a tools/list_changed mid-turn cannot widen a narrowed turn: the
            // user chose these tools for this message.
            _selection = McpToolSelection.Resolve(_services.Mcp.Snapshot, CurrentUserMessage(),
                _services.Settings.McpMaxToolsPerTurn);
            foreach (var row in _selection.Rows)
                _view.AddToolNote("mcp", row.Summary, row.Ok, null, row.Detail, null);
        }
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
        var transcript = _view.History;
        // Captured for the whole turn: the switch belongs to the chat the turn was started in, and flipping it
        // mid-turn must not change the transcript a loop that has already been running is built from.
        var ignoreHistory = _services.ActiveSession.IgnoreHistory;
        for (var round = 1; ; round++)
        {
            var assistant = new ChatMessage { Role = "assistant" };
            transcript.Add(assistant); last = assistant;
            finish = "";
            // Summarized before it is shortened, and only once the request has actually outgrown its budget:
            // under the budget there is nothing to save, and a summary would be a call spent to make the
            // request no smaller. What this recovers is the thread — the whole reason phase 1's shortening is
            // a loss at all — and what it cannot reach, Trim still shortens below.
            // With earlier messages ignored, the request is this turn and nothing older. ActiveTurn.Messages already
            // holds the user's message and every assistant/tool message of the rounds so far; the assistant being
            // built is added to it only after its call returns, so it is not in this list yet. The full transcript
            // is still what the window shows and what the session stores — only the request copy is cut, the same
            // way the summary is. A null ActiveTurn (the user moved to another chat mid-turn) keeps the full
            // history: a running loop with no turn to scope it to must still produce a valid request.
            var prompt = ignoreHistory && _view.ActiveTurn != null
                ? _view.ActiveTurn.Messages.ToList()
                : transcript.Take(transcript.Count - 1).ToList();
            if (ContextBudget.TotalChars(prompt) > ChatStateLimits.MaxPromptChars)
                prompt = await SummarizeAsync(prompt, _model, token).ConfigureAwait(true);
            // The budget is spent before sanitizing, because what it measures is the transcript that will
            // actually be sent. Nothing is dropped to fit: an old tool result loses its body and keeps the
            // call it answers, since a result that answers no call is refused by the server. Note the
            // shortening once a turn, not once a round - the same old results qualify from the round the
            // budget is first over, and a row every round would bury the transcript.
            var trimmed = ContextBudget.Trim(prompt, ChatStateLimits.MaxPromptChars, ChatStateLimits.KeepRecentToolRounds, out var shortened);
            // Sanitizing is what keeps the request valid: every tool result must answer a call the assistant
            // announced and every announced call must be answered, or an OpenAI-compatible server rejects
            // the whole transcript. A cancelled tool batch or a history loaded from an older build can
            // otherwise leave the two out of step.
            var history = ChatStateLimits.Sanitize(trimmed);
            if (shortened > 0 && !contextNoted)
            {
                contextNoted = true;
                _view.AddToolNote("", "Context · " + shortened + " older tool result" + (shortened == 1 ? "" : "s") + " shortened to fit the request", true, null, null, null);
            }
            // Built here, not stored: the system prompt and the attachment block are added at request time and the
            // transcript keeps only what the user typed and what the model answered.
            // With the tools switched off an attached file is only a path, and the model has no way to open it, so
            // a bounded excerpt goes with the request instead. The turn keeps the path either way, so the chat and
            // the stored session are unaffected by which way the setting is set.
            var context = _services.Settings.EnableToolCalling
                ? _view.ActiveTurn?.Context
                : AgentPrompt.InlineFiles(_view.ActiveTurn?.Context, _view.ReadAttachedText);
            // The error list is read every round rather than once a turn, because its whole value is being
            // current and a round that just edited a file is exactly when it has changed. Read, not built —
            // it is a walk over items already in the window — and the block says so, so an entry that predates
            // this turn is not read as the result of it. It joins the system prompt and never _history: that is
            // rebuilt per request, so this needs no sanitizing, costs the stored conversation nothing, and
            // goes away by itself once the list is clean.
            var errors = BuildDiagnostics.ToStandingNote(await SolutionBuild.ReadErrorListAsync().ConfigureAwait(true));
            var request = AgentPrompt.Build(history, context, _view.Mode, _services.Settings.EnableToolCalling, _services.Settings.EnableFileEditing, _services.Settings.EnableTerminalTools, repoInstructions, errors, mcp.Instructions, _selection.Hint, _skillPlan.Instructions);

            var bubble = _view.AddAssistantBubble(round == 1 ? "Thinking" : "Reading the tool results");
            _view.Streamed = 0; _view.Phase = round == 1 ? "Thinking" : "Reading the tool results";
            // A narrowed turn sends exactly the tools the message selected; otherwise the live snapshot, so a list
            // change between rounds still reaches the model on the next one.
            var mcpTools = _selection.Narrows ? _selection.Definitions : null;

            if (_services.Settings.EnableStreaming)
                await _services.StreamAsync(_model, request, async update =>
                {
                    assistant.Content += update.ContentDelta; assistant.Reasoning += update.ReasoningDelta;
                    if (update.ToolCalls.Count > 0) assistant.ToolCalls = update.ToolCalls;
                    if (update.FinishReason.Length > 0) finish = update.FinishReason;
                    _view.Streamed = assistant.Content.Length;
                    _view.Phase = assistant.Content.Length > 0 ? "Writing" : "Thinking";
                    await _view.InvokeAsync(() => _view.SetContent(bubble, assistant));
                }, token, mcpTools, _skillPlan.OffersTools).ConfigureAwait(true);
            else
            {
                var complete = await _services.CompleteAsync(_model, request, token, mcpTools, _skillPlan.OffersTools).ConfigureAwait(true);
                assistant.Content = complete.Content; assistant.Reasoning = complete.Reasoning; assistant.ToolCalls = complete.ToolCalls;
                _view.SetContent(bubble, assistant);
            }
            // Read after the call, not before: a streamed answer reports its usage on the last chunk, which has
            // not arrived when the first token does.
            _usage.Add(_services.LastUsage);
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
                var listed = await SolutionBuild.VerifyAsync(token).ConfigureAwait(true);
                if (listed != null)   // null is "could not verify", which is not the same as "no errors"
                {
                    var report = BuildDiagnostics.Summarize(listed);
                    if (_view.ActiveTurn != null) _view.ActiveTurn.Build = report;
                    _view.AddToolNote("build", report.Summary, report.Errors == 0, null, report.Detail, null);
                    // Handed back as a user message rather than a tool result: a tool result answers a call the
                    // assistant announced, and this one answers nothing, so ChatStateLimits.Sanitize would drop
                    // it before the model ever saw it. Persist it on the active turn as well: _history is rebuilt
                    // from turn messages after a reload/continuation, and losing this note loses the repair task.
                    buildVerified = report.Errors == 0;
                    unresolvedVerification = !buildVerified;
                    if (unresolvedVerification)
                    {
                        var buildNote = new ChatMessage { Role = "user", Content = BuildDiagnostics.ToModelNote(report) };
                        transcript.Add(buildNote);
                        _view.ActiveTurn?.Messages.Add(ChatStateLimits.CopyMessage(buildNote));
                        repairReminder = BuildDiagnostics.RepairReminder;
                        repairReminders = 0;
                    }
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
        if (SkillTools.Handles(call.Name))
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
                : await ContextTools.ExecuteAsync(LiteLlmPackage.Instance, call.Name, call.Arguments, token).ConfigureAwait(true);
        }
        Append(call, result, revisions);
    }

    /// <summary>Answers a call the turn may not make. It is an ordinary tool result, so the call stays paired and the
    /// model learns why instead of being told the tool does not exist.</summary>
    private ToolResult RefuseUnselected(McpToolInfo tool)
    {
        var label = tool.Server.Name + " · " + tool.Name;
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
            running.Add(ContextTools.ExecuteAsync(LiteLlmPackage.Instance, calls[index].Name, calls[index].Arguments, token));
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
            _view.AddToolNote(call.Name, result.Summary, result.Ok, null, null, null);
            return;
        }

        // The call edited something. The row's own undo names the last edit; the turn keeps every one, because
        // a patch can change six files and putting back one of them is not putting back the turn.
        foreach (var edit in EditTools.EditsSince(revisions))
            if (!_edits.Contains(edit)) _edits.Add(edit);
        _view.AddToolNote(call.Name, result.Summary, result.Ok, null, null, EditTools.Latest);
    }

    /// <summary>Replaces the oldest, already-covered part of an over-budget transcript with a briefing written by
    /// the utility model, so a long turn keeps the thread instead of only losing what it cannot afford to send.
    ///
    /// Returns the transcript unchanged whenever there is nothing worth doing: too little history, nothing new
    /// since the last summary, or a call that failed. That is the rule this whole method is built around — a
    /// summary is an improvement to the request and never something the turn depends on, so every failure here
    /// has to leave the request exactly what the budget alone would have made it.
    ///
    /// The summary is written once per span and reused: what the previous one stands for is left standing, and
    /// only the material that has piled up since is folded in, which is what keeps this from costing a call on
    /// every round.</summary>
    private async Task<List<ChatMessage>> SummarizeAsync(List<ChatMessage> messages, ModelInfo model, CancellationToken token)
    {
        var summaries = _view.ActiveTurn?.Summaries;
        var covered = summaries != null && summaries.Count > 0 ? summaries[summaries.Count - 1].Messages : 0;
        // A stored transcript is head-sliced on the way to disk, so a count from an earlier turn can name
        // messages that are no longer there. Starting over costs one call; trusting it would silently stop
        // summarizing for the rest of the conversation.
        if (covered < 0 || covered > messages.Count) covered = 0;
        var previous = covered > 0 ? summaries![summaries.Count - 1].Detail : null;

        if (!ContextSummary.TryBuildRequest(messages, covered, previous, ChatStateLimits.KeepRecentToolRounds, out var request, out var through))
            return covered > 0 ? ContextSummary.Merge(messages, covered, previous!) : messages;

        try
        {
            _view.Phase = "Summarizing the conversation";
            var utility = UtilityModel(model);
            var answer = await _services.CompleteInternalAsync(utility, request, ContextSummary.UtilitySettings(_services.Settings), token).ConfigureAwait(true);
            _usage.Add(_services.LastUsage);   // the summary is a call the turn made, so it is part of what it cost
            var text = ChatStateLimits.Clip(answer.Content.Trim(), ContextSummary.MaxSummaryChars);
            // A reasoning model can spend the whole small budget thinking and answer with nothing. Replacing the
            // messages with an empty briefing would quietly delete the conversation, so it is taken as the failure
            // it is and said out loud.
            if (text.Length == 0)
            {
                _view.AddToolNote("", "Context · the utility model (" + utility.Id + ") returned no summary, so the older messages were left in place", false, null, null, null);
                return covered > 0 ? ContextSummary.Merge(messages, covered, previous!) : messages;
            }

            var report = ContextSummary.Report(through, text);
            _view.ActiveTurn?.Summaries.Add(report);
            _view.AddToolNote("", report.Summary, true, null, report.Detail, null);
            _services.Log("Context summarized with " + utility.Id + ": " + through + " messages, " + text.Length + " characters");
            _view.SaveSession();
            return ContextSummary.Merge(messages, through, text);
        }
        catch (OperationCanceledException) { throw; }   // the user stopped; that is not a summarization failure
        catch (Exception ex)
        {
            _view.AddToolNote("", "Context · could not summarize the older messages (" + ex.Message + "), so the request was sent without a briefing", false, null, null, null);
            _services.Log("Context summary failed: " + ex);
            return covered > 0 ? ContextSummary.Merge(messages, covered, previous!) : messages;
        }
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
