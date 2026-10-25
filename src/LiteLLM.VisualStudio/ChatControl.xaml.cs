using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;
public partial class ChatControl : UserControl, IAgentView
{
    internal static ChatControl? Current { get; private set; }

    private readonly List<ChatMessage> _history = new List<ChatMessage>();
    private CancellationTokenSource? _generation;
    private DispatcherTimer? _ticker;
    private Bubble? _live;
    private HttpExchange? _lastExchange;
    private DateTime _started;
    private int _streamed;
    private string _phase = "Thinking";
    private string _mode = "agent";
    private bool _follow = true;
    private bool _narrow;
    private ChatTurnState? _activeTurn;
    private int _turnStart;
    /// <summary>What MCP looks like for the open solution, computed when the window loads and when the settings or
    /// the solution change — not per turn. The chip is the persistent answer to a state, where a transcript row per
    /// turn repeated the same sentence for every message.</summary>
    private McpAvailability _mcpAvailability = McpAvailability.For(null, null);
    /// <summary>What the user has attached to the next message. Draft state, like the composer's text: it belongs to
    /// the message being written, not yet to the conversation.</summary>
    private readonly List<ChatContextItem> _attachments = new List<ChatContextItem>();
    /// <summary>Every prompt drawn in this conversation, with the element it was drawn as, so the header's list can
    /// jump to one. Kept as it is drawn rather than re-derived from the session: a retry replaces the last turn, and
    /// the element that is on screen is the one the click has to reach.</summary>
    private readonly List<PromptEntry> _userPrompts = new List<PromptEntry>();
    /// <summary>Set while a picker is open or the composer is being written to, so the '#' gesture does not fire on our own edits.</summary>
    private bool _picking;
    /// <summary>The file open in the editor, held apart from <see cref="_attachments"/> so that rebuilding it as the
    /// user moves between files can never disturb what was picked by hand, and so the × on one chip means "turn this
    /// off" rather than "forget the file until the next click". Null while the setting is off or no file is open.</summary>
    private ChatContextItem? _activeFileContext;
    private DTE? _dte;
    private bool _eventSubscriptionsActive;
    private int _loadVersion;
    private WindowEvents? _windowEvents;
    private int _slashTokenStart;
    private int _slashTokenLength;
    private bool _fileMentionMode;
    /// <summary>True while the popup is showing MCP tools for a <c>#</c> token rather than solution files for an
    /// <c>@</c> token. The two share the popup and its keyboard handling; what a selection does is different.</summary>
    private bool _toolMentionMode;
    /// <summary>True while the popup is showing MCP prompts on the LiteLLM backend rather than the Claude CLI's
    /// own slash commands. The two lists share the popup but not the thing a selection does.</summary>
    private bool _promptMode;
    /// <summary>True while the popup is showing skill names for <c>/skill</c> rather than MCP prompts or the
    /// Claude CLI's commands. All three share the popup and its keyboard handling.</summary>
    private bool _skillMentionMode;
    private int _suggestionRefreshVersion;
    private string? _mentionRoot;
    private IReadOnlyList<FileMentionEntry> _mentionFiles = Array.Empty<FileMentionEntry>();
    /// <summary>The solution's declarations, so a class or method named in a reply can be clicked like a file
    /// reference. Built once per solution root, off the UI thread; null until the first build finishes, and null
    /// again while a different solution's index is being built.</summary>
    private SymbolIndex? _symbolIndex;
    private string? _symbolIndexRoot;
    private bool _symbolIndexBuilding;

    private sealed class FileMentionEntry
    {
        internal string FullPath = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "File";
        /// <summary>Set when this row is an MCP resource. The same picker lists both, because a reader looking for
        /// context does not think of "the solution's files" and "the server's documents" as two gestures.</summary>
        public McpResourceInfo? Resource { get; set; }
        /// <summary>Set when this row is an MCP tool offered by the <c>#</c> picker. Choosing it inserts a token;
        /// it does not run anything.</summary>
        public McpToolInfo? Tool { get; set; }
        /// <summary>The final row of the <c>#</c> picker: keep today's file picker reachable for people who type
        /// <c>#</c> out of habit.</summary>
        public bool FilesPicker { get; set; }
    }

    private sealed class PromptSuggestion
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public McpPromptInfo Prompt { get; set; } = new McpPromptInfo();
        /// <summary>Set when the row is a skill. Under <c>/skill</c> choosing it completes the name word; in the
        /// plain <c>/</c> list choosing it selects (or unselects) the skill for this conversation at once.</summary>
        public SkillInfo? Skill { get; set; }
    }

    internal ChatControl() { InitializeComponent(); VsTheme.Apply(this); Current = this; Loaded += OnLoaded; Unloaded += OnUnloaded; SizeChanged += OnSizeChanged; }
    /// <summary>Below this the whole header row of controls cannot sit beside the brand any more.</summary>
    private const double NarrowWidth = 820;
    private double _toolsWidth;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);
    private void ApplyLayout(double width)
    {
        var narrow = width < NarrowWidth;
        // What the wrapping row gets once the 16px side padding, the logo and the brand's margins are taken off. Only
        // used as a cap, and deliberately an under-estimate: a cap that is a little too small wraps a control that
        // would have fitted, while one that is too large lets a long model name run off the edge instead.
        var available = Math.Max(160, width - 96);
        if (_narrow == narrow && Math.Abs(_toolsWidth - available) < 1) return;
        _narrow = narrow; _toolsWidth = available;

        // Narrow: the controls move under the brand, where the row is the full width of the pane and they wrap. Wide:
        // back beside it, each at the width it was designed for.
        // Narrow keeps the row inside the one star column rather than spanning the Auto column beside it: a child that
        // spans into an Auto column is measured against the whole pane instead of the column, so the row came out wider
        // than the window it was in.
        Grid.SetRow(HeaderTools, 1); Grid.SetColumn(HeaderTools, 1); Grid.SetColumnSpan(HeaderTools, 2);
        HeaderTools.Margin = new Thickness(0, 10, 0, 0);
        SessionBox.MaxWidth = narrow ? available : 180;
        ModelBox.MaxWidth = narrow ? available : 200;

        // The composer's own button strip gets the same treatment: beside the status line when there is room, and on a
        // line of its own under it when there is not, where it has the whole pane to wrap into rather than being cut
        // off at the right edge. It stays in the star column for the same reason the header row does.
        Grid.SetRow(ComposerTools, narrow ? 1 : 0);
        Grid.SetColumn(ComposerTools, 0);
        Grid.SetColumnSpan(ComposerTools, 1);
        ComposerTools.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);

        ComposerHost.Margin = narrow ? new Thickness(10, 8, 10, 12) : new Thickness(18, 10, 18, 16);
        ConversationScroll.Padding = narrow ? new Thickness(10, 10, 10, 10) : new Thickness(18, 12, 18, 12);
    }
    private ExtensionServices Services => LiteLlmPackage.Instance.Services;
    /// <summary>Set once, because a tool window can be closed and reopened and the answer does not change between
    /// them. It is the only way to know from inside Visual Studio whether the shell published its own control styles
    /// and answered its own colour tokens — the alternative is assuming, and this extension is not allowed to assume
    /// when the answer decides what the user sees.</summary>
    private static bool _themeReported;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var loadVersion = ++_loadVersion;
        Current = this;
        if (!_themeReported) { _themeReported = true; Services.Log(VsTheme.Describe()); }
        if (!_eventSubscriptionsActive)
        {
            Services.ModelsChanged += ModelsChanged; Services.SessionsChanged += SessionsChanged;
            Services.SettingsChanged += SettingsChanged;
            Services.Mcp.ToolsChanged += McpToolsChanged;
            Services.Mcp.ServerClosed += McpServerClosed;
            _eventSubscriptionsActive = true;
        }
        // The edit tools ask the chat, rather than a modal dialog, because the question belongs in the conversation
        // where the change it is asking about is already on screen.
        EditTools.Confirm = AskToApplyEditAsync;
        TerminalTools.Confirm = AskToRunCommandAsync;
        McpApproval.Confirm = AskToApproveMcpToolAsync;
        await Services.RefreshSessionScopeAsync();
        if (!IsLoaded || loadVersion != _loadVersion) return;
        ReloadSessions(); RenderSession(); ReloadModels();
        // One read of the MCP state for this window. Everything after it is a refresh on a change, not a check per
        // turn: the servers cannot become enabled or disabled by a message.
        _ = RefreshMcpChipAsync();
        // The chips and the Skills menu read the same snapshot. Scanning here means the first click on Skills
        // already has the list, and a selected name left behind by an earlier session gets its real state back.
        _ = PrepareSkillsAsync(loadVersion);
        // The enabled servers are stated in settings.json, so opening the window is where they come back: a restart
        // must not mean visiting the Dashboard to reconnect each one by hand.
        _ = AutoConnectMcpAsync(loadVersion);
        // The replies already on screen gain their class and method links when this finishes; until then they are
        // plain text, so nothing about the window's readiness depends on it.
        _ = EnsureSymbolIndexAsync();
        ReloadPermissionMode();
        RefreshAutoContextButton();
        await WatchEditorAsync(loadVersion);
        if (!IsLoaded || loadVersion != _loadVersion) return;
        if (Services.Models.Count == 0) await SafeRefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ++_loadVersion;
        _generation?.Cancel();
        if (_eventSubscriptionsActive)
        {
            Services.ModelsChanged -= ModelsChanged; Services.SessionsChanged -= SessionsChanged;
            Services.SettingsChanged -= SettingsChanged;
            Services.Mcp.ToolsChanged -= McpToolsChanged;
            Services.Mcp.ServerClosed -= McpServerClosed;
            _eventSubscriptionsActive = false;
        }
        try { if (_windowEvents != null) _windowEvents.WindowActivated -= OnWindowActivated; } catch (Exception) { }
        _windowEvents = null; _dte = null;
        if (ReferenceEquals(EditTools.Confirm?.Target, this)) EditTools.Confirm = null;
        if (ReferenceEquals(TerminalTools.Confirm?.Target, this)) TerminalTools.Confirm = null;
        if (ReferenceEquals(McpApproval.Confirm?.Target, this)) McpApproval.Confirm = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }
    /// <summary>Brings back the MCP servers the user enabled. It runs for the LiteLLM backend only: the Claude CLI
    /// starts its own MCP processes from the per-run configuration, so connecting here would launch a second set for
    /// the same servers. Nothing is launched while the feature is off or every server is disabled. A server that
    /// cannot be reached is drawn as its own row and does not fail the load.</summary>
    private async Task AutoConnectMcpAsync(int loadVersion)
    {
        try
        {
            if (!Services.Settings.EnableMcp) return;
            if (!string.Equals(Services.ActiveSession.ExecutionBackend, "litellm", StringComparison.OrdinalIgnoreCase)) return;
            if (!(Services.Settings.McpServers ?? new List<McpServerConfiguration>()).Any(server => server != null && server.Enabled)) return;

            // A window that looks ready while a server is being launched invites a message the turn cannot send
            // with the tools it expects. Only an empty status line is taken, and only the same text is cleared, so a
            // turn started meanwhile keeps its own status.
            const string connecting = "Connecting MCP servers…";
            var announced = StateText.Text.Length == 0;
            if (announced) StateText.Text = connecting;

            var root = await SolutionRootAsync();
            if (!IsLoaded || loadVersion != _loadVersion) return;
            var report = await Services.Mcp.ConnectEnabledAsync(Services.Settings, root, CancellationToken.None);
            if (!IsLoaded || loadVersion != _loadVersion) return;
            foreach (var row in report.Rows) AddToolNote("mcp", row.Summary, row.Ok, null, row.Detail);
            if (announced && StateText.Text == connecting) StateText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            Services.Log("MCP auto-connect failed: " + ex);
        }
    }
    private void ModelsChanged(object sender, EventArgs e) => Dispatcher.Invoke(ReloadModels);
    private void SessionsChanged(object sender, EventArgs e) => Dispatcher.Invoke(ReloadSessions);
    /// <summary>The event fires on whatever thread changed the snapshot, so the refresh is marshalled. If the tool
    /// list under an open popup changed, the next refresh shows the new one; a row that vanished cannot be chosen.</summary>
    private void McpToolsChanged(object? sender, EventArgs e)
    {
        // The event arrives on the connection's receive loop. Reading IsLoaded there would be a cross-thread call
        // on a DispatcherObject and throw, and the client swallows handler failures — the composer would simply
        // stop refreshing. The dispatcher is the only thing touched before the switch.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            RefreshSlashCommands();
            _ = RefreshMcpChipAsync();
        }));
    }
    /// <summary>A server that ended on its own is said as soon as it happens rather than left for the next tool call:
    /// the composer would otherwise keep offering tools that no longer exist. A server this window disconnected on
    /// purpose closes from a map that no longer owns it and never reaches here.</summary>
    private void McpServerClosed(McpServerConfiguration server, string reason)
    {
        // Same rule as ToolsChanged: IsLoaded belongs to the UI thread, so the check moves inside the dispatch.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            AddToolNote("mcp", "MCP · " + server.Name + " disconnected: " + McpLog.Redact(reason), false, null,
                "The next turn tries to connect it again; ↻ in the MCP dashboard does it now.", null);
            RefreshSlashCommands();
            _ = RefreshMcpChipAsync();
        }));
    }
    /// <summary>A settings save is the one signal every control that edits MCP goes through, so the chip follows it at
    /// once rather than waiting for the next message. The event may arrive on the thread that saved; the dispatcher is
    /// touched before anything that belongs to the window.</summary>
    private void SettingsChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            _ = RefreshMcpChipAsync();
        }));
    }
    private void McpChip_Click(object sender, MouseButtonEventArgs e)
    {
        try { _ = LiteLlmPackage.Instance.ShowDashboardMcpAsync(); }
        catch (Exception ex) { Services.Log("The MCP dashboard could not be opened: " + ex); }
    }
    /// <summary>Recomputes the chip for the solution that is open now. Called when the window loads, when the settings
    /// are saved, when the solution changes and when a server's tool list does — never from a turn.</summary>
    private async Task RefreshMcpChipAsync()
    {
        try
        {
            var root = await SolutionRootAsync();
            if (!IsLoaded) return;
            _mcpAvailability = McpAvailability.For(Services.Settings, root);
            DrawMcpChip();
        }
        catch (Exception ex) { Services.Log("MCP status could not be read: " + ex); }
    }
    /// <summary>The chip is configuration, so one enabled server is "on" even if it is still connecting: the tooltip
    /// is where a failed connection belongs, and flipping the chip to "off" would blame the user's setting for a
    /// network problem.</summary>
    private void DrawMcpChip()
    {
        var availability = _mcpAvailability;
        var tools = Services.Mcp.Tools;
        var enabled = availability.IsEnabled;
        McpChipText.Text = enabled
            ? (tools.Count > 0
                ? "MCP · " + tools.Count + (tools.Count == 1 ? " tool" : " tools")
                : "MCP on · " + availability.EnabledCount + (availability.EnabledCount == 1 ? " server" : " servers"))
            : "MCP off";
        // Accent for on, support text for off: the two colours the rest of the composer already uses. Resolved rather
        // than assigned, so a theme change repaints the dot with everything else.
        McpDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            enabled ? EnvironmentColors.AccentMediumBrushKey : (object)CommonDocumentColors.SupportTextBrushKey);
        McpChip.SetResourceReference(Border.BorderBrushProperty,
            enabled ? EnvironmentColors.AccentMediumBrushKey : (object)CommonDocumentColors.ListItemBorderBrushKey);

        var lines = new List<string>();
        if (!enabled)
        {
            lines.Add(availability.Detail);
        }
        else
        {
            lines.Add(availability.Headline);
            lines.Add(tools.Count == 1 ? "1 tool is offered right now." : tools.Count + " tools are offered right now.");
            foreach (var server in availability.EnabledServers)
            {
                var status = Services.Mcp.StatusOf(server.Id);
                var state = status == null
                    ? "not connected yet"
                    : status.Connected
                        ? "connected · " + status.Tools + (status.Tools == 1 ? " tool" : " tools")
                        : "not connected" + (string.IsNullOrWhiteSpace(status.Error)
                            ? ""
                            : ": " + McpLog.Redact(status.Error ?? ""));
                lines.Add("· " + server.Name + " — " + state);
            }
            lines.Add("Click to open Dashboard ▸ MCP.");
        }
        McpChip.ToolTip = string.Join("\n", lines);
    }
    /// <summary>Whether MCP is on for this turn and this solution has a server the user enabled that is not
    /// connected — the state in which a typed '#' silently falls back to the file picker. A connected server that
    /// simply offers no tools is not that state, and must not be described as disconnected.</summary>
    private bool McpEnabledButNotConnected(string? root) =>
        Services.Settings.EnableMcp &&
        Services.Settings.EnableToolCalling &&
        string.Equals(Services.ActiveSession.ExecutionBackend, "litellm", StringComparison.OrdinalIgnoreCase) &&
        Services.Mcp.Tools.Count == 0 &&
        (Services.Settings.McpServers ?? new List<McpServerConfiguration>())
            .Any(server => server != null && server.Enabled && server.AppliesTo(root) &&
                           !(Services.Mcp.StatusOf(server.Id)?.Connected ?? false));
    private void ReloadSessions() { SessionBox.ItemsSource = null; SessionBox.ItemsSource = Services.Sessions.Sessions; SessionBox.SelectedValuePath = "Id"; SessionBox.SelectedValue = Services.Sessions.ActiveSessionId; RefreshDeleteButton(false); }
    private void ReloadModels() { var selected = Services.ActiveSession.SelectedModelKey.Length > 0 ? Services.ActiveSession.SelectedModelKey : Services.Settings.SelectedModelKey; ModelBox.ItemsSource = null; ModelBox.ItemsSource = Services.Models; ModelBox.SelectedItem = Services.Models.FirstOrDefault(m => m.Key == selected) ?? Services.Models.FirstOrDefault(); if (ModelBox.SelectedItem is ModelInfo model) ModelBox.ToolTip = model.Id; StateText.Text = Services.Models.Count == 0 ? "Configure a server in the Dashboard" : string.Empty; }
    private void RenderSession()
    {
        ReloadBackend();
        Messages.Children.Clear(); Messages.Children.Add(EmptyState); _history.Clear();
        _userPrompts.Clear();
        foreach (var turn in Services.ActiveSession.Turns)
        {
            foreach (var message in turn.Messages)
            {
                if (message.Role == "user") AddMessage("You", message.Content, false);
                else if (message.Role == "assistant") AddMessage("Assistant", Describe(message), true);
                else if (message.Role == "tool") AddToolNote(message.ToolName, "Tool result · " + message.ToolName, true);
                _history.Add(ChatStateLimits.CopyMessage(message));
            }
            // The build row is not one of the messages — the note behind it is a user-role message the model was
            // sent, and replaying that would put words in the user's mouth. It is stored on the turn instead and
            // drawn back from there.
            if (turn.Build != null) AddToolNote("build", turn.Build.Summary, turn.Build.Errors == 0, null, turn.Build.Detail);
            // After the build row, because that is the order they were drawn in a live turn.
            if (turn.Tests != null) AddToolNote("tests", turn.Tests.Summary, turn.Tests.Errors == 0, null, turn.Tests.Detail);
            // Same reasoning as the build row: the briefing is the model's own text standing in for messages that
            // are still here, so it is replayed from the turn rather than re-derived, and the rows come back in
            // the order they were drawn in a live turn.
            foreach (var summary in turn.Summaries) AddToolNote("", summary.Summary, true, null, summary.Detail);
            // Last, as it was drawn last. A turn stored by a build that did not count usage has none.
            if (turn.Usage != null) AddToolNote("", turn.Usage.Line, true, null, null, null);
        }
        EmptyState.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The chip strip follows the conversation, and this switch lives on the conversation, so switching chats
        // must redraw it as well as the messages.
        RefreshContextBar();
    }
    private void SaveSession() => SaveSession(Services.ActiveSession, (ModelBox.SelectedItem as ModelInfo)?.Key);
    private void SaveSession(ChatSessionState session, string? selectedModelKey = null) { session.UpdatedUtc = DateTime.UtcNow; session.SelectedModelKey = selectedModelKey ?? session.SelectedModelKey; if (session.HasMessages && session.Title == "New chat") { var first = session.Turns.SelectMany(t => t.Messages).FirstOrDefault(m => m.Role == "user"); if (first != null) session.Title = ChatStateLimits.OneLine(first.Content, 42); } Services.SaveSessions(); }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendClaudeAsync(string input)
    {
        Services.ApplyOptions();
        var settings = Services.Settings;
        var policy = new ClaudeToolPolicyOptions
        {
            EnableFileEditing = settings.EnableFileEditing,
            ConfirmEachEdit = settings.ConfirmEachEdit,
            EnableTerminalTools = settings.EnableTerminalTools,
            ConfirmEachCommand = settings.ConfirmEachCommand
        };
        try { policy.Validate(settings.ClaudeAllowedTools); }
        catch (Exception ex)
        {
            StateText.Text = ex.Message;
            Services.Log(ex.ToString());
            Composer.Focus();
            return;
        }
        ComposerHint.Visibility = Visibility.Collapsed;
        Composer.Clear(); EmptyState.Visibility = Visibility.Collapsed;
        _history.Add(new ChatMessage { Role = "user", Content = input }); ScrollToSent(AddMessage("You", input, false));
        _turnStart = _history.Count - 1;
        var pendingContext = PendingContext(); ClearAttachments();
        var session = Services.ActiveSession;
        var turn = new ChatTurnState { ModelKey = "claude-code", Status = ChatTurnStatus.Streaming, StartedUtc = DateTime.UtcNow, Context = pendingContext.ToList() };
        _activeTurn = turn;
        turn.Messages.Add(new ChatMessage { Role = "user", Content = input });
        session.Turns.Add(turn); SaveSession(session, "claude-code");
        SetBusy(true); var generation = new CancellationTokenSource(); _generation = generation; var token = generation.Token; StartTicker();
        var assistant = new ChatMessage { Role = "assistant" }; _history.Add(assistant);
        var view = AddAssistantBubble("Thinking");
        McpClaudeLease? mcpLease = null;
        try
        {
            var runner = new ClaudeCodeRunner();
            var context = input.StartsWith("/", StringComparison.Ordinal) ? "" : AgentPrompt.AttachmentBlock(pendingContext, true);
            var prompt = context.Length == 0 ? input : context + "\n\n" + input;
            var workingDirectory = await SolutionRootAsync();
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new InvalidOperationException("Open a solution or project with a valid local root before using Claude Code.");
            workingDirectory = Path.GetFullPath(workingDirectory);
            var wrapperPath = Path.Combine(Path.GetDirectoryName(typeof(ChatControl).Assembly.Location) ?? "", "LiteLLM.GitWrapper.exe");
            if (!File.Exists(wrapperPath)) throw new FileNotFoundException("The packaged LiteLLM Git wrapper was not found.", wrapperPath);
            // The CLI runs its own harness, so the extension can only hand it servers and an allow-list. A failure
            // here is a row and an MCP-less Claude turn, never a failed one: the rest of the turn does not depend on it.
            if (settings.EnableMcp)
            {
                try
                {
                    mcpLease = await Services.Mcp.PrepareClaudeRunAsync(settings, workingDirectory, token);
                    if (mcpLease != null)
                    {
                        var count = mcpLease.Servers == 1 ? "1 server" : mcpLease.Servers + " servers";
                        var allowed = mcpLease.AllowedTools.Count == 0
                            ? "; no tool is allow-listed, so Claude will report every MCP tool as denied"
                            : " · " + (mcpLease.AllowedTools.Count == 1
                                ? "1 tool allow-listed"
                                : mcpLease.AllowedTools.Count + " tools allow-listed");
                        AddToolNote("mcp", "MCP · " + count + " exposed to Claude" + allowed, mcpLease.AllowedTools.Count > 0);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddToolNote("mcp", "MCP · the Claude configuration could not be prepared: " + ex.Message, false);
                    Services.Log("MCP configuration for the Claude backend failed: " + ex);
                }
            }
            prompt = "For Git operations, use only the approved LiteLLM.GitWrapper executable. It performs confirmations and authentication; do not invoke git, gh, or glab directly.\n\n" + prompt;
            if (settings.EnableSkills)
            {
                try
                {
                    // The CLI owns automatic skill discovery. Only a manual selection is handed over: a skill the
                    // CLI already knows is named, and one from another location is injected once.
                    var snapshot = await Services.PrepareSkillsAsync(workingDirectory, token).ConfigureAwait(true);
                    var rows = new List<SkillTurnRow>();
                    var skillBlock = SkillPrompt.BuildClaudeBlock(snapshot, session.SelectedSkills, rows);
                    foreach (var row in rows) AddToolNote("skill", row.Summary, row.Ok, null, row.Detail);
                    if (!string.IsNullOrEmpty(skillBlock)) prompt = skillBlock + "\n\n" + prompt;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddToolNote("skill", "Skills · discovery failed: " + ex.Message, false);
                    Services.Log("Skill discovery for the Claude backend failed: " + ex);
                }
            }
            var result = await runner.RunAsync(settings.ClaudeExecutable, prompt, session.ClaudeSessionId,
                settings.ClaudeAllowedTools, settings.ClaudeDisallowedTools, workingDirectory, text =>
                {
                    assistant.Content = text;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(_activeTurn, turn) && ReferenceEquals(Services.ActiveSession, session))
                        { _streamed = text.Length; _phase = text.Length > 0 ? "Writing" : "Thinking"; SetContent(view, assistant); }
                    }));
                }, id =>
                {
                    if (string.IsNullOrWhiteSpace(id)) return;
                    // Persist synchronously on the UI thread before cancellation can discard the result.
                    Dispatcher.Invoke(new Action(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(session.ClaudeSessionId)) return;
                        session.ClaudeSessionId = id;
                        SaveSession(session, "claude-code");
                    }));
                }, policy, token, wrapperPath, mcpLease?.Path, mcpLease?.AllowedTools, mcpLease?.Strict ?? false).ConfigureAwait(true);
            assistant.Content = result.Text;
            if (ReferenceEquals(_activeTurn, turn) && ReferenceEquals(Services.ActiveSession, session)) { SetContent(view, assistant); FinishStatus(view); }
            if (string.IsNullOrWhiteSpace(session.ClaudeSessionId) && !string.IsNullOrWhiteSpace(result.SessionId)) session.ClaudeSessionId = result.SessionId;
            turn.Status = ChatTurnStatus.Complete; turn.CompletedUtc = DateTime.UtcNow;
            if (assistant.Content.Length > 0) turn.Messages.Add(ChatStateLimits.CopyMessage(assistant));
            SaveSession(session, "claude-code");
            if (ReferenceEquals(Services.ActiveSession, session)) StateText.Text = string.Empty;
        }
        catch (OperationCanceledException) { if (assistant.Content.Length > 0) turn.Messages.Add(ChatStateLimits.CopyMessage(assistant)); turn.Status = ChatTurnStatus.Cancelled; turn.CompletedUtc = DateTime.UtcNow; SaveSession(session, "claude-code"); if (ReferenceEquals(Services.ActiveSession, session)) StateText.Text = "Stopped"; }
        catch (Exception ex) { if (assistant.Content.Length > 0) turn.Messages.Add(ChatStateLimits.CopyMessage(assistant)); turn.Status = ChatTurnStatus.Failed; turn.Error = ex.Message; turn.CompletedUtc = DateTime.UtcNow; SaveSession(session, "claude-code"); if (ReferenceEquals(Services.ActiveSession, session)) { AddToolNote("error", ex.Message, false, null, ex.ToString()); StateText.Text = ex.Message; } Services.Log(ex.ToString()); }
        finally { mcpLease?.Dispose(); if (ReferenceEquals(_generation, generation)) { _ticker?.Stop(); _ticker = null; if (_live != null) FinishStatus(_live); _live = null; _generation = null; SetBusy(false); } generation.Dispose(); if (ReferenceEquals(_activeTurn, turn)) _activeTurn = null; Composer.Focus(); }
    }

    /// <summary>Handles <c>/skill</c> before anything is sent. A handled command is never forwarded as ordinary
    /// text: its name selects a skill on this conversation and only the user's remaining words become the message.
    /// A message that does not begin with the exact token is returned unchanged, which is what keeps MCP prompts,
    /// Claude's own commands and ordinary prose on their previous paths.</summary>
    private async Task<string?> ApplySkillCommandAsync(string input)
    {
        var command = SkillSlashCommand.Parse(input);
        if (command.Kind == SkillCommandKind.None) return input;

        if (!Services.Settings.EnableSkills)
        {
            AddToolNote("skill", "Skills · skills are turned off in the Dashboard's Skills tab.", false);
            StateText.Text = "Skills are turned off. Enable them in the Dashboard's Skills tab.";
            return null;
        }

        if (command.Kind == SkillCommandKind.MissingName)
        {
            AddToolNote("skill", "Skill · /skill needs a skill name, such as /skill code-review Create the endpoint.", false);
            StateText.Text = "Use /skill <name>, or pick a skill with the Skills button.";
            return null;
        }

        SkillRegistrySnapshot snapshot;
        try { snapshot = await Services.PrepareSkillsAsync(await SolutionRootAsync()).ConfigureAwait(true); }
        catch (Exception ex)
        {
            AddToolNote("skill", "Skills · discovery failed: " + ex.Message, false);
            StateText.Text = "Skills could not be read: " + ex.Message;
            Services.Log("Skill discovery failed: " + ex);
            return null;
        }

        var matches = SkillSlashCommand.Resolve(snapshot.Effective, command.Name, out var ambiguous);
        if (ambiguous)
        {
            var names = string.Join(", ", matches.Take(8).Select(skill => skill.Name)) + (matches.Count > 8 ? ", …" : "");
            AddToolNote("skill", "Skill · '" + command.Name + "' matches more than one skill: " + names, false);
            StateText.Text = "That name is ambiguous. Type more of it.";
            return null;
        }
        if (matches.Count == 0)
        {
            var suggestions = SkillSlashCommand.Suggest(snapshot.Effective, command.Name);
            var detail = snapshot.Effective.Count == 0
                ? "No skill is discovered. Create or import one in the Dashboard's Skills tab."
                : "Available: " + string.Join(", ", snapshot.Effective.Take(30).Select(skill => skill.Name));
            AddToolNote("skill",
                "Skill · '" + command.Name + "' was not found" +
                (suggestions.Count > 0 ? ". Did you mean " + string.Join(", ", suggestions) + "?" : "."), false, null, detail);
            StateText.Text = "No skill named '" + command.Name + "'.";
            return null;
        }

        var selected = Services.ActiveSession.SelectedSkills;
        if (!selected.Any(name => string.Equals(name, matches[0].Name, StringComparison.OrdinalIgnoreCase)))
        {
            // The same bound the Skills menu applies, so /skill cannot build a selection the turn will not send.
            if (selected.Count >= ChatSessionStore.MaxSelectedSkills)
            {
                AddToolNote("skill", "Skill · a conversation can use at most " + ChatSessionStore.MaxSelectedSkills +
                                     " skills; remove one with its chip first.", false);
                StateText.Text = "At most " + ChatSessionStore.MaxSelectedSkills + " skills can be selected in one conversation.";
                return null;
            }
            selected.Add(matches[0].Name);
            SaveSession();
            RefreshContextBar();
        }
        AddToolNote("skill", "Skill · " + matches[0].Name + " selected for this conversation", true, null,
            SkillLocations.DisplayPath(matches[0].Directory));
        // An invocation with no instructions still has to send something, and the command text itself must not be
        // what the model sees: naming the skill is the instruction.
        return command.Remaining.Length > 0 ? command.Remaining : "Use the skill '" + matches[0].Name + "' for this request.";
    }

    private async Task SendAsync()
    {
        var input = Composer.Text;
        if (string.IsNullOrWhiteSpace(input) || _generation != null) return;
        // Started before the first reply so the links exist by the time it is drawn; the turn itself never waits for
        // the index, and a solution opened after the window loaded is picked up here.
        _ = EnsureSymbolIndexAsync();
        input = await ApplySkillCommandAsync(input);
        if (input == null) return;
        var editsBefore = EditTools.Revisions;
        if (string.Equals(Services.ActiveSession.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase))
        {
            await SendClaudeAsync(input);
            // The CLI edits its own files; a declaration it added or removed makes the index stale for the next reply.
            if (EditTools.Revisions > editsBefore) _symbolIndex = null;
            return;
        }
        if (!(ModelBox.SelectedItem is ModelInfo model)) return;
        input = input.Trim();
        ComposerHint.Visibility = Visibility.Collapsed;
        Composer.Clear(); EmptyState.Visibility = Visibility.Collapsed;
        _history.Add(new ChatMessage { Role = "user", Content = input }); ScrollToSent(AddMessage("You", input, false));
        _turnStart = _history.Count - 1;
        // The chips are the context for this message. They move onto the turn now, so the stored conversation records
        // what each answer was given, and the composer goes back to being empty.
        var pendingContext = PendingContext();
        ClearAttachments();
        _activeTurn = new ChatTurnState { ModelKey = model.Key, Status = ChatTurnStatus.Streaming, StartedUtc = DateTime.UtcNow, Context = pendingContext.ToList() };
        _activeTurn.Messages.Add(new ChatMessage { Role = "user", Content = input });
        Services.ActiveSession.Turns.RemoveAll(t => t.Messages.Count == 0 && t.Context.Count > 0);
        Services.ActiveSession.Turns.Add(_activeTurn); SaveSession();

        SetBusy(true); _generation = new CancellationTokenSource(); var token = _generation.Token; StartTicker();
        try
        {
            // The round loop lives in AgentHarness now: with the tools and the verification steps inside it, a
            // turn is a piece of work rather than a view update, and this control is only what it reads and
            // draws through. What stays here is what belongs to the window — the composer, the token, the
            // outcome — and the harness draws through the IAgentView this control implements.
            await new AgentHarness(this, Services, model).RunAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { if (_activeTurn != null) { _activeTurn.Status = ChatTurnStatus.Cancelled; _activeTurn.CompletedUtc = DateTime.UtcNow; } SaveSession(); StateText.Text = "Stopped"; }
        catch (Exception ex)
        {
            var failure = ex as LiteLlmException;
            var detail = failure?.TechnicalDetails ?? ex.ToString();
            AddToolNote("error", ex.Message, false, failure?.Exchange ?? _lastExchange, detail);   // visible, and right-click copies the raw JSON
            if (_activeTurn != null) { _activeTurn.Status = ChatTurnStatus.Failed; _activeTurn.Error = ex.Message; _activeTurn.CompletedUtc = DateTime.UtcNow; }
            SaveSession(); StateText.Text = ex.Message;
            Services.Log(detail);
        }
        finally { _ticker?.Stop(); _ticker = null; if (_live != null) FinishStatus(_live); _live = null; _generation.Dispose(); _generation = null; SetBusy(false); _activeTurn = null; Composer.Focus(); if (EditTools.Revisions > editsBefore) _symbolIndex = null; }
    }

    /// <summary>What <see cref="AgentHarness"/> may ask of this window. Explicit rather than making the methods
    /// public: the loop is the only caller, and the control's own API stays what the rest of the extension uses.
    /// Each member is the same private method or field the loop used to reach directly, which is what makes the
    /// move a move.</summary>
    List<ChatMessage> IAgentView.History => _history;
    ChatTurnState? IAgentView.ActiveTurn => _activeTurn;
    HttpExchange? IAgentView.LastExchange { get => _lastExchange; set => _lastExchange = value; }
    string IAgentView.Mode => _mode;
    string IAgentView.Phase { get => _phase; set => _phase = value; }
    int IAgentView.Streamed { get => _streamed; set => _streamed = value; }
    string IAgentView.Status { get => StateText.Text; set => StateText.Text = value; }
    Bubble IAgentView.AddAssistantBubble(string status) => AddAssistantBubble(status);
    void IAgentView.SetContent(Bubble bubble, ChatMessage message) => SetContent(bubble, message);
    void IAgentView.FinishStatus(Bubble bubble) => FinishStatus(bubble);
    void IAgentView.AddToolNote(string name, string summary, bool ok, HttpExchange? exchange, string? detail, AppliedEdit? edit) => AddToolNote(name, summary, ok, exchange, detail, edit);
    void IAgentView.AddUndoAllNote(IReadOnlyList<AppliedEdit> edits) => AddToolNote("", edits.Count == 1
        ? "Edited 1 file this turn — right-click to undo it"
        : "Edited " + edits.Count + " files this turn — right-click to undo them all", true, null, null, null, edits);
    Task<bool> IAgentView.AskToContinueAsync(int limit, int raised, CancellationToken token) => AskToContinueAsync(limit, raised, token);
    Task IAgentView.InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;
    Task<string?> IAgentView.SolutionRootAsync() => SolutionRootAsync();
    string? IAgentView.ReadAttachedText(string path) => ReadAttachedText(path);
    void IAgentView.SaveSession() => SaveSession();

    /// <summary>Copilot Chat's "Continue to iterate?" checkpoint, rendered in the transcript rather than a modal
    /// dialog so the conversation stays readable while the turn waits. Resolves true to continue with the raised
    /// budget, false to stop — and the composer's Stop has to resolve it too, because the loop is parked on this
    /// task rather than on the token.</summary>
    private async Task<bool> AskToContinueAsync(int limit, int raised, CancellationToken token)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "⚠  LiteLLM has been working on this problem for a while. It can continue to iterate, or you can send a new message to refine your prompt.", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = limit + " tool rounds used. Continuing raises the budget to " + raised + ", up to " + ChatStateLimits.MaxToolRoundsCeiling + ".", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var stop = new Button { Content = "Stop" };
        var more = new Button { Content = "Continue", Style = (Style)FindResource("PrimaryButton") };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { stop, more } });
        var card = new Border { Style = (Style)FindResource("Card"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900, Child = body, Margin = new Thickness(0, 0, 0, 12) };
        // SetResourceReference, never an assignment: a brush handed over as a local value keeps the object it was given,
        // so after a theme change this card would still be painting the previous theme's accent while everything around
        // it repainted. This is the code equivalent of {DynamicResource}.
        card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);

        var source = new TaskCompletionSource<bool>();
        var answered = false;
        void Answer(bool keepGoing) { if (answered) return; answered = true; stop.IsEnabled = false; more.IsEnabled = false; source.TrySetResult(keepGoing); }
        stop.Click += (s, e) => Answer(false);
        more.Click += (s, e) => Answer(true);
        Messages.Children.Add(card);
        ScrollIfFollowing();

        bool keepGoing;
        try
        {
            // Only the task is completed from the token callback: it can fire on any thread, so it must not touch the UI.
            using (token.Register(() => source.TrySetResult(false))) keepGoing = await source.Task.ConfigureAwait(true);
        }
        finally { answered = true; Messages.Children.Remove(card); }

        token.ThrowIfCancellationRequested();   // stopping during the pause ends the turn as Stopped, like any other stop
        AddToolNote("", keepGoing
            ? "Continuing — the tool round budget is now " + raised + "."
            : "Stopped after " + limit + " rounds of tool calls. The results so far are kept — send another message to continue from them.", keepGoing);
        return keepGoing;
    }

    /// <summary>A one-line record of a tool call. The result itself stays in the model's context — echoing a whole file into the conversation is what burns the user's tokens. An edit is the one exception, and even then the diff is
    /// offered on the row's menu rather than printed into the transcript.</summary>
    private void AddToolNote(string name, string summary, bool ok, HttpExchange? exchange = null, string? detail = null, AppliedEdit? edit = null, IReadOnlyList<AppliedEdit>? undoAll = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = ok ? "🔧" : "⚠", Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = summary, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 12), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900, Child = row };
        // Resolved rather than assigned, so a theme change repaints this row with everything else. A failure keeps the
        // derived error colour, which carries its own contrast guard against the page and the cards.
        border.SetResourceReference(Border.BackgroundProperty, CommonDocumentColors.ListItemBackgroundBrushKey);
        if (ok) border.SetResourceReference(Border.BorderBrushProperty, CommonDocumentColors.ListItemBorderBrushKey);
        else border.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        var full = detail ?? "";
        var entries = new List<object?>
        {
            Item("Copy error report", () => Copy(Report(exchange, summary, full), "Report"), !ok && exchange != null),
            Item("Copy summary", () => Copy(summary, "Summary")),
            Item("Copy full detail", () => Copy(full, "Detail"), full.Length > 0),
            null,
            Item("Copy diff", () => Copy(edit!.Diff, "Diff"), edit != null),
            Item("Undo this edit", () => UndoEdit(edit!), edit != null)
        };
        // Only on the turn's own row, and only when there is more than the row's single undo to offer. It sits
        // with the other undo rather than on the turn's assistant bubble, because the menu is where this chat
        // keeps every action that can change a file back.
        if (undoAll is { Count: > 0 })
            entries.Add(Item("Undo all " + undoAll.Count + (undoAll.Count == 1 ? " edit" : " edits") + " from this turn", () => _ = UndoAllAsync(undoAll)));
        entries.Add(Item("Copy request (JSON)", () => Copy(exchange?.RequestBody, "Request"), exchange != null));
        entries.Add(Item("Copy response (JSON)", () => Copy(exchange?.ResponseBody, "Response"), exchange != null));
        border.ContextMenu = Menu(entries.ToArray());
        border.ToolTip = edit != null ? edit.Diff : full.Length > 0 ? full : (name.Length > 0 ? name : summary);
        Messages.Children.Add(border);
        ScrollIfFollowing();
    }

    private async void UndoEdit(AppliedEdit edit)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StateText.Text = EditTools.Undo((DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE)), edit);
        }
        catch (Exception ex) { StateText.Text = "Could not undo: " + ex.Message; }
    }

    /// <summary>Undoes a whole turn's edits in one go. The order is <see cref="EditTools.UndoAll"/>'s, not this
    /// row's: newest first, because each edit recorded the file as it stood just before it ran. Returns a task
    /// rather than being an <c>async void</c> — the menu action is not an event handler, and the surrounding code
    /// already carries the analyzer warnings this would have added to.</summary>
    private async Task UndoAllAsync(IReadOnlyList<AppliedEdit> edits)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StateText.Text = EditTools.UndoAll((DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE)), edits);
        }
        catch (Exception ex) { StateText.Text = "Could not undo: " + ex.Message; }
    }

    /// <summary>The question the edit tools ask when "Confirm before each edit" is on. It is a card in the transcript
    /// rather than a modal, so the diff it is asking about stays readable, and Skip has to be as easy to reach as
    /// Allow — a confirmation that only offers a way forward is not a confirmation.</summary>
    private async Task<bool> AskToApplyEditAsync(EditProposal proposal)
    {
        if (AllowAllPermissions) return true;
        if (!Services.Settings.ConfirmEachEdit) return true;

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "✎  Edit " + proposal.Label + "?  " + proposal.Summary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBox
        {
            Text = proposal.Diff,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, Courier New"),
            MaxHeight = 260,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        });
        var skip = new Button { Content = "Skip" };
        var allow = new Button { Content = "Allow", Style = (Style)FindResource("PrimaryButton") };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { skip, allow } });
        var card = new Border { Style = (Style)FindResource("Card"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900, Child = body, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);   // resolved, so it follows a theme change

        var source = new TaskCompletionSource<bool>();
        var answered = false;
        void Answer(bool apply) { if (answered) return; answered = true; skip.IsEnabled = false; allow.IsEnabled = false; source.TrySetResult(apply); }
        skip.Click += (s, e) => Answer(false);
        allow.Click += (s, e) => Answer(true);
        Messages.Children.Add(card);
        ScrollIfFollowing();

        bool apply;
        try
        {
            // Stop has to answer the card as well as the loop: the turn is parked here, not on the token.
            using ((_generation?.Token ?? CancellationToken.None).Register(() => source.TrySetResult(false)))
                apply = await source.Task.ConfigureAwait(true);
        }
        finally { answered = true; Messages.Children.Remove(card); }
        return apply;
    }

    /// <summary>The question an MCP tool call asks. The card names both the server and the tool the user configured,
    /// shows the annotations the server published, and prints the model's arguments with anything that looks like a
    /// credential masked. Deny is offered as plainly as Allow, and "Always allow" writes the server's own tool name
    /// to the allow-list that <see cref="McpPolicy"/> reads.
    ///
    /// The confirmation is the only place a hint becomes a decision: an unannotated tool asks, and so does a
    /// read-only one unless the global exception and the server's own switch are both on.</summary>
    private async Task<bool> AskToApproveMcpToolAsync(McpApprovalRequest request)
    {
        if (AllowAllPermissions) return true;
        var tool = request.Tool;
        var server = tool.Server;

        var badges = new List<string>();
        if (tool.ReadOnlyHint) badges.Add("read-only");
        if (tool.DestructiveHint) badges.Add("destructive");
        if (tool.OpenWorldHint) badges.Add("open-world");
        if (badges.Count == 0) badges.Add("may change something");

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "⚙  MCP · " + server.Name + " · " + tool.DisplayName,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = tool.Name + "  ·  " + string.Join(" · ", badges),
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            var description = tool.Description!;
            body.Children.Add(new TextBlock
            {
                Text = description.Length <= 400 ? description : description.Substring(0, 400) + "…",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        var arguments = RedactArguments(request.Arguments).ToString(Formatting.Indented);
        if (arguments.Length > 4000) arguments = arguments.Substring(0, 4000) + "\n…";
        body.Children.Add(new TextBox
        {
            Text = arguments,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, Courier New"),
            MaxHeight = 220,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        });

        var deny = new Button { Content = "Deny" };
        var always = new Button { Content = "Always allow this tool" };
        var allow = new Button { Content = "Allow once", Style = (Style)FindResource("PrimaryButton") };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { deny, always, allow }
        });
        var card = new Border { Style = (Style)FindResource("Card"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900, Child = body, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);

        var source = new TaskCompletionSource<bool>();
        var answered = false;
        void Answer(bool approved, bool remember)
        {
            if (answered) return;
            answered = true;
            deny.IsEnabled = false; always.IsEnabled = false; allow.IsEnabled = false;
            if (approved && remember && !server.AllowedTools.Any(name => string.Equals(name, tool.Name, StringComparison.Ordinal)))
            {
                server.AllowedTools.Add(tool.Name);
                Services.Save();
            }
            source.TrySetResult(approved);
        }
        deny.Click += (s, e) => Answer(false, false);
        always.Click += (s, e) => Answer(true, true);
        allow.Click += (s, e) => Answer(true, false);
        Messages.Children.Add(card);
        ScrollIfFollowing();

        bool approvedResult;
        try
        {
            using ((_generation?.Token ?? CancellationToken.None).Register(() => source.TrySetResult(false)))
                approvedResult = await source.Task.ConfigureAwait(true);
        }
        finally { answered = true; Messages.Children.Remove(card); }
        return approvedResult;
    }

    /// <summary>Masks values whose property name looks like a credential before the arguments are shown. The model
    /// produced this text, and a token pasted into the conversation must not become a second place it is printed.
    /// </summary>
    private static JToken RedactArguments(JToken token)
    {
        if (token is JObject obj)
        {
            var copy = new JObject();
            foreach (var property in obj.Properties())
                copy[property.Name] = McpSecrets.LooksSecret(property.Name)
                    ? new JValue("<redacted>")
                    : RedactArguments(property.Value);
            return copy;
        }
        if (token is JArray array) return new JArray(array.Select(RedactArguments));
        return token.DeepClone();
    }

    private async Task<bool> AskToRunCommandAsync(CommandProposal proposal)
    {
        if (AllowAllPermissions) return true;
        if (!Services.Settings.ConfirmEachCommand) return true;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Run " + proposal.Shell + " command?", FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBox { Text = proposal.Command, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas, Courier New"), MaxHeight = 260, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap });
        var skip = new Button { Content = "Skip" };
        var allow = new Button { Content = "Run", Style = (Style)FindResource("PrimaryButton") };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { skip, allow } });
        var card = new Border { Style = (Style)FindResource("Card"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900, Child = body, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);
        var source = new TaskCompletionSource<bool>();
        skip.Click += (s, e) => source.TrySetResult(false);
        allow.Click += (s, e) => source.TrySetResult(true);
        Messages.Children.Add(card); ScrollIfFollowing();
        try { using ((_generation?.Token ?? CancellationToken.None).Register(() => source.TrySetResult(false))) return await source.Task.ConfigureAwait(true); }
        finally { Messages.Children.Remove(card); }
    }

    private Border AddMessage(string author, string content, bool assistant)
    {
        var bubble = CreateBubble(author, assistant);
        SetContent(bubble, new ChatMessage { Role = "assistant", Content = content });
        // The prompt list is the same list of turns the user sees, so it is recorded where the bubble is drawn:
        // replaying a conversation and sending a message both come through here.
        if (!assistant) _userPrompts.Add(new PromptEntry(content, bubble.Border));
        return bubble.Border;
    }

    private Bubble AddAssistantBubble(string status)
    {
        var bubble = CreateBubble("Assistant", true);
        var text = new TextBlock { Text = status, Style = (Style)FindResource("Caption") };
        // Track and indicator come from the ProgressBar style, which names the shell's own ProgressBarColors pair.
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 6, 0, 0) };
        var live = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        live.Children.Add(text); live.Children.Add(bar);
        bubble.Body.Children.Add(live);
        bubble.Live = live; bubble.Status = text; bubble.Bar = bar;
        _live = bubble;
        ScrollIfFollowing();
        return bubble;
    }

    /// <summary>Whether to show the model's reasoning. Guarded rather than read straight off the package so a message can be rendered without a live package (design-time, tests).</summary>
    private static bool ShowReasoning
    {
        get { try { return LiteLlmPackage.Instance.Services.Settings.ShowReasoning; } catch (Exception) { return false; } }
    }

    private Bubble CreateBubble(string author, bool assistant)
    {
        var body = new StackPanel();
        var border = new Border { MaxWidth = 900, MinWidth = 90, Margin = new Thickness(0, 0, 0, assistant ? 18 : 10) };
        StackPanel? actions = null;
        if (assistant)
        {
            // The answer is read at length, so it sits on the page beside a small round avatar rather than inside a box,
            // and its copy / retry row is revealed on hover like the one Visual Studio Code shows under a response.
            border.Style = (Style)FindResource("MessageAssistant");
            border.HorizontalAlignment = HorizontalAlignment.Stretch;
            var icon = new Border { Style = (Style)FindResource("Avatar") };
            icon.Child = new TextBlock
            {
                Text = "✦",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(CommonControlsColors.ButtonDefaultTextBrushKey)
            };
            var content = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            content.Children.Add(new TextBlock { Text = author, FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = .85, Margin = new Thickness(0, 3, 0, 6) });
            content.Children.Add(body);
            actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 4, 0, 0) };
            content.Children.Add(actions);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(icon, 0); Grid.SetColumn(content, 1);
            grid.Children.Add(icon); grid.Children.Add(content);
            border.Child = grid;
        }
        else
        {
            // Keep the prompt in its soft right-aligned card, with the same hover actions available on an answer.
            border.HorizontalAlignment = HorizontalAlignment.Right;
            var card = new Border { Style = (Style)FindResource("MessageUser"), Child = body };
            actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, -6, 0) };
            var content = new StackPanel();
            content.Children.Add(card); content.Children.Add(actions);
            border.Child = content;
        }
        var bubble = new Bubble { Border = border, Body = body };
        Action retryAction = assistant ? (Action)RetryLatest : () => RetryRequest(bubble.Message?.Content);
        border.ContextMenu = Menu(
            Item("Copy message", () => Copy(Describe(bubble.Message), "Message")),
            Item(assistant ? "Retry response" : "Retry request", retryAction),
            null,
            Item("Copy request (JSON)", () => Copy(bubble.Exchange?.RequestBody, "Request"), bubble.Exchange != null),
            Item("Copy response (JSON)", () => Copy(bubble.Exchange?.ResponseBody, "Response"), bubble.Exchange != null));
        if (actions != null)
        {
            // Wired after the bubble exists because both buttons read the live transcript entry rather than a copy of it.
            var copy = Ghost("⧉ Copy");
            copy.Click += (s, e) => Copy(Describe(bubble.Message), "Message");
            var retry = Ghost("↻ Retry");
            retry.Click += (s, e) => retryAction();
            actions.Children.Add(copy); actions.Children.Add(retry);
        }
        Messages.Children.Add(border);
        return bubble;
    }

    /// <summary>A quiet label-sized button for the row under a message; the GhostAction style keeps it invisible until
    /// the pointer is over the message.</summary>
    private Button Ghost(string label) => new Button { Content = label, Style = (Style)FindResource("GhostAction") };

    // ------------------------------------------------------------------ copying things out

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (s, e) => action();
        return item;
    }
    private static ContextMenu Menu(params object?[] entries)
    {
        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            if (entry == null) menu.Items.Add(new Separator());
            else menu.Items.Add(entry);
        }
        return menu;
    }

    private void Copy(string? text, string what)
    {
        if (text == null || text.Length == 0) { StateText.Text = "Nothing to copy"; return; }
        try { Clipboard.SetDataObject(text, true); StateText.Text = what + " copied (" + text.Length.ToString("N0") + " chars)"; }
        catch (Exception ex) { StateText.Text = "Clipboard is busy: " + ex.Message; }
    }

    /// <summary>Everything about a failed call in one paste: the URL, what we sent, and what the server said back.
    /// A 400 cannot be diagnosed from the request alone — the reason lives in the response.</summary>
    private static string Report(HttpExchange? exchange, string summary, string detail)
    {
        if (exchange == null) return detail.Length > 0 ? summary + "\n\n" + detail : summary;
        var text = new System.Text.StringBuilder();
        if (exchange.Url.Length > 0) text.AppendLine("POST " + exchange.Url);
        text.AppendLine("--- REQUEST ---").AppendLine(exchange.RequestBody);
        text.AppendLine("--- RESPONSE (HTTP " + exchange.Status.ToString() + (exchange.Streamed ? ", streamed" : "") + ") ---").AppendLine(exchange.ResponseBody);
        if (detail.Length > 0 && detail.IndexOf(exchange.ResponseBody, StringComparison.Ordinal) < 0) text.AppendLine("--- DETAIL ---").AppendLine(detail);
        return text.ToString();
    }

    /// <summary>The raw exchange behind the most recent request — what "why did it 400?" is answered from.</summary>
    private HttpExchange? LastExchange { get { try { return LiteLlmPackage.Instance.Services.LastExchange; } catch (Exception) { return null; } } }

    private string LastReply() => _history.LastOrDefault(m => m.Role == "assistant" && m.Content.Length > 0)?.Content ?? "";

    private static string Describe(ChatMessage? message)
    {
        if (message == null) return "";
        // Tool calls are protocol metadata, not part of the assistant's answer.  Showing the
        // serialized JSON in the chat made a normal response look like a broken transcript.
        return message.Content;
    }

    private string Transcript()
    {
        var text = new System.Text.StringBuilder();
        foreach (var message in _history)
        {
            text.AppendLine("### " + message.Role);
            if (message.Content.Length > 0) text.AppendLine(message.Content);
            foreach (var call in message.ToolCalls) text.AppendLine("→ " + call.Name + "(" + call.Arguments + ")");
            text.AppendLine();
        }
        return text.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var failed = _lastExchange != null && _lastExchange.Status >= 400;
        var menu = Menu(
            Item("Copy last reply", () => Copy(LastReply(), "Reply"), LastReply().Length > 0),
            null,
            Item("Copy error report", () => Copy(Report(_lastExchange, "The last request failed.", ""), "Report"), failed),
            Item("Copy request (JSON)", () => Copy(_lastExchange?.RequestBody, "Request"), _lastExchange != null),
            Item("Copy response (JSON)", () => Copy(_lastExchange?.ResponseBody, "Response"), _lastExchange != null),
            Item("Copy whole conversation", () => Copy(Transcript(), "Conversation"), _history.Count > 0));
        menu.PlacementTarget = HamburgerButton; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
    }

    private FrameworkElement Directed(string text)
    {
        var openFile = new Action<FileReference>(reference => OpenLater(() => OpenReferenceAsync(reference)));
        var index = _symbolIndex;
        var rendered = index == null
            ? MarkdownRenderer.Render(text, openFile)
            : MarkdownRenderer.Render(text, openFile, index.Find, location => OpenLater(() => OpenSymbolAsync(location)));
        rendered.FlowDirection = TextDirection.For(text);
        return rendered;
    }

    /// <summary>Opens through Visual Studio's editor without making the drawing of a reply wait for it. Both open
    /// operations report their own failures into the status line, so there is nothing left for the caller to do.</summary>
    private static void OpenLater(Func<Task> open) =>
        ThreadHelper.JoinableTaskFactory.RunAsync(async () => await open());

    /// <summary>Replaces the bubble's content, keeping the author line and any live status panel at the foot.</summary>
    private void SetContent(Bubble bubble, ChatMessage message)
    {
        bubble.Message = message;
        // Everything in the body is drawn from the message except the live panel, which belongs at the foot while
        // a turn runs. Redrawing the body as a whole is what stops a previous streamed chunk from surviving: the
        // old cleanup kept one rendered child, and that child was the first chunk of every streamed reply.
        var body = bubble.Body;
        var live = bubble.Live;
        body.Children.Clear();
        if (message.Reasoning.Length > 0 && ShowReasoning)
            body.Children.Add(new Expander { Header = "Thinking · Show reasoning", Content = Directed(message.Reasoning), Margin = new Thickness(0, 2, 0, 6) });
        if (message.Content.Length > 0) body.Children.Add(Directed(message.Content));
        if (live != null) body.Children.Add(live);
        ScrollIfFollowing();
    }

    private void FinishStatus(Bubble bubble)
    {
        if (bubble.Live == null) return;
        bubble.Body.Children.Remove(bubble.Live);
        bubble.Live = null; bubble.Status = null; bubble.Bar = null;
    }

    private void StartTicker()
    {
        _started = DateTime.UtcNow; _streamed = 0; _phase = "Thinking";
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (s, e) => RefreshStatus();
        _ticker.Start();
    }

    /// <summary>Keeps a visible clock on the message being generated, so a slow first token never looks like a hang.</summary>
    private void RefreshStatus()
    {
        if (_live?.Status == null) return;
        var elapsed = (DateTime.UtcNow - _started).TotalSeconds;
        _live.Status.Text = _phase + "… " + elapsed.ToString("0.0") + "s" + (_streamed > 0 ? " · " + _streamed.ToString("N0") + " chars" : "");
    }

    private void SetBusy(bool value)
    {
        StopButton.IsEnabled = value; BackendBox.IsEnabled = !value; ModeBox.IsEnabled = !value; SessionBox.IsEnabled = !value;
        ModelBox.IsEnabled = !value && !string.Equals(Services.ActiveSession.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase);
        // One slot, two meanings, the way the chat view in Visual Studio Code swaps its send button for a stop button
        // rather than leaving two of them side by side and asking the reader which one applies.
        StopButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        SendButton.IsEnabled = !value && Composer.Text.Trim().Length > 0;
        RefreshDeleteButton(value);
        if (value) StateText.Text = "Generating…";
    }
    /// <summary>Starts a new conversation. Everything belonging to the message being written goes with it: a draft and
    /// its attachments carried into a fresh session would look like they belonged to it.</summary>
    internal void Clear()
    {
        // _generation is cancelled but not cleared: the running turn still owns it, and its finally block is what
        // disposes it. Nulling it here would leave that block throwing on a null.
        _generation?.Cancel();
        _history.Clear(); _activeTurn = null; _lastExchange = null; _live = null; _follow = true;
        _userPrompts.Clear();
        Services.NewSession();   // fires SessionsChanged, which is what reloads the session list
        Messages.Children.Clear(); Messages.Children.Add(EmptyState); EmptyState.Visibility = Visibility.Visible;
        Composer.Clear(); ClearAttachments();
        StateText.Text = string.Empty;
        ReloadModels();   // last, because it has the more important thing to say when no server is configured
        Composer.Focus();
    }
    /// <summary>Live only when there is something to delete. An empty conversation is already what deleting it would
    /// leave behind, so a Delete button that stayed live there would be one more silent no-op in a pane that keeps
    /// having to apologise for them. Takes the busy flag as an argument rather than reading <c>_generation</c>:
    /// <c>SetBusy(true)</c> runs one statement before <c>_generation</c> is assigned, so reading it here would call a
    /// turn that is just starting a turn that is not running.</summary>
    private void RefreshDeleteButton(bool busy) => DeleteChatButton.IsEnabled = !busy && Services.ActiveSession.HasMessages;

    /// <summary>Deletes the conversation on screen — the one the history drop-down is showing, which is what lets this
    /// be one header action instead of a × on every row. It asks first, the way removing a server does in the
    /// dashboard: New chat starts something and this destroys something, and only one of those can be undone.</summary>
    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        var session = Services.ActiveSession;
        if (!session.HasMessages) return;   // defensive: the button's own enabled state is the real gate
        if (MessageBox.Show("Delete \"" + session.Title + "\"? This cannot be undone.", "LiteLLM", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Services.DeleteSession(session.Id);   // fires SessionsChanged, which rebuilds the list and moves the pointer
        // Whatever belonged to the conversation just deleted belongs to nothing now. The draft and its attachments are
        // deliberately kept: they are the message being written, and deleting a conversation is not a request to lose it.
        _activeTurn = null; _lastExchange = null; _live = null; _follow = true;
        RenderSession();
        ReloadModels();   // last, for the same reason Clear runs it last: with no server it has the more important thing to say
        if (Services.Models.Count > 0) StateText.Text = "Conversation deleted";
        Composer.Focus();
    }

    private void RefreshComposerState()
    {
        if (ComposerHint != null) ComposerHint.Visibility = Composer.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (SendButton != null && _generation == null) SendButton.IsEnabled = Composer.Text.Trim().Length > 0;
    }
    private void SessionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionBox.SelectedValue is Guid id && id != Services.Sessions.ActiveSessionId && _generation == null) { Services.ActivateSession(id); RenderSession(); ReloadModels(); }
    }
    // ------------------------------------------------------------------ context attachments

    /// <summary>Takes the selection from the editor. It becomes a chip rather than a code block in the composer, which
    /// is what Visual Studio Code does: the text travels with the request without being pasted in front of the user,
    /// and the prompt stays the prompt.</summary>
    internal void InsertContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        Attach(new ChatContextItem
        {
            Label = "Selection · " + lines + (lines == 1 ? " line" : " lines"),
            Kind = "selection",
            Preview = ChatStateLimits.Clip(text, ChatStateLimits.MaxContextPreviewChars)
        });
        StateText.Text = "Selection attached";
        Composer.Focus();
    }

    private void Attach(ChatContextItem item)
    {
        if (_attachments.Any(a => a.Label == item.Label && a.Preview == item.Preview)) return;
        _attachments.Add(item);
        RefreshContextBar();
    }

    private void ClearAttachments() { _attachments.Clear(); RefreshContextBar(); }

    private void RefreshContextBar()
    {
        ContextBar.Children.Clear();
        if (_activeFileContext != null) ContextBar.Children.Add(Chip(_activeFileContext, true));
        foreach (var item in _attachments) ContextBar.Children.Add(Chip(item, false));
        // The selection lives on the conversation, so switching chats has to redraw these as well as the messages.
        // They are drawn even when the feature is off: the chip's × is the way a name left behind by an earlier
        // build is removed, and a selection nothing shows on screen would be one the user cannot undo.
        var selectedSkills = Services.ActiveSession.SelectedSkills ?? new List<string>();
        foreach (var name in selectedSkills) ContextBar.Children.Add(SkillChip(name));
        // This switch changes what the model is sent without changing a single row on screen, so it needs a chip
        // of its own: a model that silently never saw the earlier turns would otherwise read as a bug.
        var historyOff = Services.ActiveSession.IgnoreHistory
            && !string.Equals(Services.ActiveSession.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase);
        if (historyOff) ContextBar.Children.Add(HistoryChip());
        ContextBar.Visibility = _activeFileContext != null || _attachments.Count > 0 || selectedSkills.Count > 0 || historyOff
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshComposerState();
    }

    /// <summary>One skill the user selected for this conversation. The × drops the name from the conversation;
    /// a name that no longer resolves keeps its chip and is outlined in the error colour, because a selection the
    /// turn silently ignores would otherwise look like the skill simply did nothing.</summary>
    private Border SkillChip(string name)
    {
        var skill = Services.Skills.Snapshot.Find(name)
                    ?? Services.Skills.Snapshot.All.FirstOrDefault(item =>
                        string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "✦",
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = name,
            Style = (Style)FindResource("Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 240
        });
        var remove = new Button
        {
            Content = "×",
            Style = (Style)FindResource("SubtleButton"),
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Stop using this skill in this conversation"
        };
        remove.Click += (s, e) =>
        {
            Services.ActiveSession.SelectedSkills.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            SaveSession();
            RefreshContextBar();
        };
        row.Children.Add(remove);

        var problem = "";
        if (skill == null)
            problem = "This skill is not in the current scan. Refresh the Skills tab in the Dashboard to look again.";
        else if (!skill.IsValid)
            problem = skill.Error ?? "This skill's SKILL.md is invalid.";
        else if (!skill.Enabled)
            problem = "This skill is turned off in the Dashboard.";
        else if (skill.Shadowed)
            problem = "Another definition of this name is used instead: " + SkillLocations.DisplayPath(skill.ShadowedBy);

        var tooltip = (string.IsNullOrWhiteSpace(skill?.Description) ? "Selected skill" : skill!.Description) +
                      (skill != null ? "\n" + SkillLocations.DisplayPath(skill.Directory) : "") +
                      (problem.Length > 0 ? "\n\n" + problem : "");
        var border = new Border
        {
            Style = (Style)FindResource("Tag"),
            Margin = new Thickness(0, 0, 5, 3),
            ToolTip = tooltip,
            Child = row
        };
        // Resolved rather than assigned, so a theme change repaints the outline with everything else.
        border.SetResourceReference(Border.BorderBrushProperty, problem.Length > 0 ? "DangerBrush" : (object)EnvironmentColors.AccentMediumBrushKey);
        return border;
    }

    /// <summary>The chip for a chat that is not sending its earlier turns. Its × turns history back on, so the only
    /// way out of the state is not the menu that entered it.</summary>
    private Border HistoryChip()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // The shell's own History glyph — a clock with a counter-clockwise arrow — rather than a card index or a
        // clock face emoji: the chip is about earlier turns no longer being sent, and this is the mark Visual Studio
        // uses for exactly that idea.
        row.Children.Add(new TextBlock
        {
            Text = "\uE81C",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = "History off", Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center });
        var remove = new Button { Content = "×", Style = (Style)FindResource("SubtleButton"), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Send earlier messages again" };
        remove.Click += (s, e) => SetIgnoreHistory(false);
        row.Children.Add(remove);
        var border = new Border { Style = (Style)FindResource("Tag"), Margin = new Thickness(0, 0, 5, 3), ToolTip = "Earlier messages in this chat are kept on screen and in the saved conversation, but are not sent with the next message.", Child = row };
        border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);
        return border;
    }

    /// <summary>One attachment. The label opens the file it names; the × takes it off the message. The full path lives
    /// in the tooltip because the chip is the only place the path is shown at all.</summary>
    private Border Chip(ChatContextItem item, bool auto)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = item.Kind == "file" ? "📄" : "✂", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = item.Label, Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
        if (item.Kind == "file") { label.Cursor = Cursors.Hand; label.MouseLeftButtonUp += async (s, e) => await OpenAsync(item.Preview); }
        row.Children.Add(label);

        var remove = new Button { Content = "×", Style = (Style)FindResource("SubtleButton"), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = auto ? "Stop attaching the open file" : "Remove" };
        // The automatic chip has no entry in _attachments to take out, so its × is wired to the setting instead — which
        // is the same thing the Auto button toggles, and leaves the user somewhere they can turn it back on.
        remove.Click += (s, e) => { if (auto) SetAutoContext(false); else { _attachments.Remove(item); RefreshContextBar(); } };
        row.Children.Add(remove);

        var border = new Border { Style = (Style)FindResource("Tag"), Margin = new Thickness(0, 0, 5, 3), ToolTip = item.Preview, Child = row };
        // The automatic chip is outlined in the accent as well, so it never reads as one more thing the user put there
        // themselves. The outline is resolved, not assigned, so a theme change repaints it with everything else; the
        // label stays the caption colour, because an accent-coloured label is at the mercy of the theme's accent.
        if (auto) border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.AccentMediumBrushKey);
        return border;
    }

    // ------------------------------------------------------------------ the file open in the editor

    /// <summary>Hooks the editor's window events so the current file follows the user from tab to tab, the way Visual
    /// Studio Code keeps the active editor in the chat's context.</summary>
    private async Task WatchEditorAsync(int loadVersion)
    {
        if (_windowEvents != null) return;   // Loaded can fire more than once; a second handler would double every refresh
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            if (!IsLoaded || loadVersion != _loadVersion || dte == null) return;
            var windowEvents = dte.Events.WindowEvents;
            if (!IsLoaded || loadVersion != _loadVersion) return;
            _dte = dte;
            _windowEvents = windowEvents;
            _windowEvents.WindowActivated += OnWindowActivated;
            await RefreshActiveFileAsync();
        }
        catch (Exception) { /* no DTE — design time, or a shell with no solution: the composer simply carries no chip */ }
    }

    private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
    {
        _ = RefreshActiveFileAsync();
        _ = RefreshSessionScopeAndRenderAsync();
        // This is also how a solution opened after the window was loaded gets its index built.
        _ = EnsureSymbolIndexAsync();
    }

    private async Task RefreshSessionScopeAndRenderAsync()
    {
        var previous = Services.Sessions;
        await Services.RefreshSessionScopeAsync();
        // A solution change is also an MCP-scope change: a server limited to one solution starts or stops applying.
        _ = RefreshMcpChipAsync();
        if (ReferenceEquals(previous, Services.Sessions)) return;
        RenderSession();
        ReloadModels();
        // A different solution means different project roots, so the snapshot is rescanned rather than reused.
        _ = PrepareSkillsAsync(_loadVersion);
    }

    /// <summary>Scans the configured skill directories and redraws once the answer is in. Failure is logged and
    /// left to the Dashboard: an unreadable root must not fail a window that is otherwise ready, and the same
    /// problem is drawn there in full.</summary>
    private async Task PrepareSkillsAsync(int loadVersion)
    {
        try
        {
            var snapshot = await Services.PrepareSkillsAsync(await SolutionRootAsync());
            if (!IsLoaded || loadVersion != _loadVersion) return;
            foreach (var problem in snapshot.Problems) Services.Log("Skills · " + problem);
            RefreshContextBar();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.Log("Skill discovery failed: " + ex); }
    }

    /// <summary>Turns "send only this turn" on or off for the conversation on screen. The transcript is untouched;
    /// what changes is the request the next turn builds from it.</summary>
    private void SetIgnoreHistory(bool value)
    {
        if (Services.ActiveSession.IgnoreHistory == value) return;
        Services.ActiveSession.IgnoreHistory = value;
        SaveSession();
        RefreshContextBar();
        AddToolNote("", value
            ? "History · earlier messages in this chat will not be sent with the next turn"
            : "History · earlier messages in this chat will be sent again", true, null, null, null);
    }

    private void AutoContext_Click(object sender, RoutedEventArgs e) => SetAutoContext(!Services.Settings.AutoAttachActiveDocument);

    private void SetAutoContext(bool value)
    {
        Services.Settings.AutoAttachActiveDocument = value; Services.Save();
        RefreshAutoContextButton();
        _ = RefreshActiveFileAsync();
    }

    private void RefreshAutoContextButton() => RefreshPermissionToolTip();

    private void Hamburger_Click(object sender, RoutedEventArgs e)
    {
        var auto = Item((Services.Settings.AutoAttachActiveDocument ? "✓ " : "") + "Auto context — attach active editor file", () => SetAutoContext(!Services.Settings.AutoAttachActiveDocument));
        auto.ToolTip = "When enabled, the file currently open in the editor is automatically included with every message.";
        var claude = string.Equals(Services.ActiveSession.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase);
        var history = Item((Services.ActiveSession.IgnoreHistory ? "✓ " : "") + "History — ignore earlier turns in this chat", () => SetIgnoreHistory(!Services.ActiveSession.IgnoreHistory), !claude);
        history.ToolTip = claude
            ? "Available on the LiteLLM backend."
            : "Earlier messages in this chat stay on screen and in the saved conversation, but are not sent with the next message. Takes effect from the next turn.";
        var defaultPermissions = Item((AllowAllPermissions ? "" : "✓ ") + "Default permissions", () => SetPermissionMode("default"));
        defaultPermissions.ToolTip = "Ask before protected actions according to the current settings.";
        var allowAll = Item((AllowAllPermissions ? "✓ " : "") + "Allow all", () => SetPermissionMode("allowAll"));
        allowAll.ToolTip = "Allow all enabled tools without approval.";
        var menu = Menu(
            auto,
            Item("Add context…", ShowContextMenu),
            history,
            Item("Copy…", () => Copy_Click(sender, e)),
            null,
            defaultPermissions,
            allowAll,
            null,
            Item("Refresh models", async () => await SafeRefreshAsync()));
        menu.PlacementTarget = HamburgerButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Adds or removes one name from the conversation's selection. This is a conversation setting, not a
    /// message: it is saved now and read when the next turn starts, which is also why a toggle during a running
    /// turn changes the following turn rather than the one already in flight.</summary>
    private void ToggleSkill(string name)
    {
        var selected = Services.ActiveSession.SelectedSkills ?? (Services.ActiveSession.SelectedSkills = new List<string>());
        var existing = selected.FirstOrDefault(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            selected.Remove(existing);
            StateText.Text = "Skill '" + name + "' removed from this conversation";
        }
        else
        {
            if (selected.Count >= ChatSessionStore.MaxSelectedSkills)
            {
                StateText.Text = "A conversation can use at most " + ChatSessionStore.MaxSelectedSkills + " selected skills.";
                return;
            }
            selected.Add(name);
            StateText.Text = "Skill '" + name + "' selected for this conversation";
        }
        SaveSession();
        RefreshContextBar();
    }

    /// <summary>Rebuilds the automatic chip from whatever the editor has open now. Cheap enough to run on every window
    /// activation, and it does nothing at all when the file has not actually changed, so clicking between tabs does
    /// not rebuild the chip strip.</summary>
    private async Task RefreshActiveFileAsync()
    {
        try
        {
            var path = Services.Settings.AutoAttachActiveDocument ? await ActiveDocumentPathAsync() : "";
            ChatContextItem? next = null;
            if (path.Length > 0)
            {
                var root = await SolutionRootAsync();
                next = new ChatContextItem { Label = ContextTools.Relative(root, path).Replace('\\', '/'), Kind = "file", Preview = path };
            }
            if (Same(_activeFileContext, next)) return;
            _activeFileContext = next;
            RefreshContextBar();
        }
        catch (Exception) { }
    }

    private static bool Same(ChatContextItem? a, ChatContextItem? b) =>
        a == null ? b == null : b != null && a.Preview == b.Preview && a.Label == b.Label;

    private async Task<string> ActiveDocumentPathAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = _dte ?? (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var path = dte?.ActiveDocument?.FullName ?? "";
            // File.Exists drops the editor's virtual documents — the diff view, the debugger's windows — which have a
            // FullName but are not files the agent could read.
            return File.Exists(path) ? path : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>What this message actually goes with: the automatic file first, then the user's own, minus anything the
    /// automatic one already says.</summary>
    private List<ChatContextItem> PendingContext()
    {
        var context = new List<ChatContextItem>();
        if (_activeFileContext != null) context.Add(_activeFileContext);
        foreach (var item in _attachments)
            if (!context.Any(a => a.Label == item.Label && a.Preview == item.Preview)) context.Add(item);
        return context;
    }

    /// <summary>The text of an attached file, for when the tools that would read it are switched off. Best-effort: a
    /// file that has gone, or that cannot be decoded, comes back null and keeps only its path in the request.</summary>
    private static string? ReadAttachedText(string path)
    {
        try { return File.Exists(path) ? TextFile.Read(path).Text : null; }
        catch (Exception) { return null; }
    }

    private static async Task OpenAsync(string path)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (File.Exists(path)) ((DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE)))?.ItemOperations.OpenFile(path);
    }

    private async Task OpenReferenceAsync(FileReference reference)
    {
        try
        {
            var root = await SolutionRootAsync();
            if (root == null) { StateText.Text = "Open a solution first"; return; }
            var path = PathGuard.Resolve(root, reference.Path);
            if (!File.Exists(path)) { StateText.Text = "Referenced file was not found"; return; }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var window = dte?.ItemOperations.OpenFile(path);
            if (window == null) { StateText.Text = "Could not open referenced file"; return; }
            window.Activate();
            var selection = dte?.ActiveDocument?.Selection as TextSelection;
            if (selection != null && reference.Line.HasValue)
            {
                if (reference.Column.HasValue) selection.MoveToLineAndOffset(reference.Line.Value, reference.Column.Value, false);
                else selection.GotoLine(reference.Line.Value, false);
            }
        }
        catch (ArgumentException) { StateText.Text = "Referenced file is outside the solution"; }
        catch (Exception ex) { StateText.Text = "Could not open referenced file"; Services.Log(ex.ToString()); }
    }

    /// <summary>Opens the file an indexed declaration lives in and moves the caret to its name. The path came from the
    /// index under the solution root, not from the model, so there is no outside-the-solution case here — but the file
    /// can have been deleted or the solution closed since the index was built, and each failure is said rather than
    /// leaving the click without an answer.</summary>
    private async Task OpenSymbolAsync(SymbolLocation location)
    {
        try
        {
            if (!File.Exists(location.Path)) { StateText.Text = "The symbol's file is no longer there"; return; }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var window = dte?.ItemOperations.OpenFile(location.Path);
            if (window == null) { StateText.Text = "Could not open the symbol's file"; return; }
            window.Activate();
            if (dte?.ActiveDocument?.Selection is TextSelection selection)
            {
                if (location.Column > 0) selection.MoveToLineAndOffset(location.Line, location.Column, false);
                else selection.GotoLine(location.Line, false);
            }
        }
        catch (Exception ex) { StateText.Text = "Could not open the symbol"; Services.Log(ex.ToString()); }
    }

    /// <summary>Builds the declaration index for the current solution, then redraws the conversation so replies added
    /// before it was ready gain their links. Best-effort by design: an unreadable tree costs the links and not the
    /// chat, and a running turn is left alone because redrawing it would rebuild the live bubble from the stored turn,
    /// which lags the text still streaming into it.</summary>
    private async Task EnsureSymbolIndexAsync()
    {
        if (_symbolIndexBuilding) return;
        string? root;
        try { root = await SolutionRootAsync(); }
        catch (Exception ex) { Services.Log("Symbol index could not start: " + ex); return; }
        var directory = root ?? "";
        if (directory.Length == 0) return;
        if (_symbolIndex != null && string.Equals(directory, _symbolIndexRoot, StringComparison.OrdinalIgnoreCase)) return;

        _symbolIndexBuilding = true;
        _symbolIndexRoot = directory;
        _symbolIndex = null;
        SymbolIndex? built = null;
        try { built = await Task.Run(() => SymbolIndex.Build(SourceFiles(directory))); }
        catch (Exception ex) { Services.Log("Symbol index could not be built: " + ex); }
        finally { _symbolIndexBuilding = false; }

        if (built == null) return;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        // A solution can be closed while a large tree is being read; an index of the previous one would link to files
        // the user has since closed. Leaving it unset makes the next activation build the new one.
        var current = await SolutionRootAsync();
        if (!string.Equals(current, directory, StringComparison.OrdinalIgnoreCase)) return;
        _symbolIndex = built;
        if (IsLoaded && _generation == null) RenderSession();
    }

    private static IReadOnlyList<string> SourceFiles(string root)
    {
        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // bin, obj, .git and .vs hold generated or stale copies; indexing them would link a declaration to a
                // file the user cannot edit and does not recognise.
                if (ContextTools.IsNoise(path)) continue;
                files.Add(path);
                if (files.Count >= SymbolIndex.MaxFiles) break;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>VS Code's Add Context menu. What cannot be done here is still listed, greyed, with the reason in the
    /// tooltip — a menu missing half its entries looks like a different menu, not a smaller one.</summary>
    private void AddContext_Click(object sender, RoutedEventArgs e) => ShowContextMenu();

    private void ShowContextMenu()
    {
        var menu = Menu(
            Item("Files & Folders…", async () => await PickFilesAsync()),
            Item("Selection", async () => await AttachSelectionAsync()),
            Item("Problems", async () => await AttachProblemsAsync()),
            null,
            Item("Symbols", () => { }, false),
            Item("Source Control", () => { }, false),
            Item("Terminal Selection", () => { }, false),
            Item("Codebase", () => { }, false));
        foreach (var item in menu.Items.OfType<MenuItem>().Where(m => !m.IsEnabled))
            item.ToolTip = item.Header switch
            {
                "Symbols" => "Needs Roslyn symbol search, which this build does not wire up.",
                "Source Control" => "Needs source-control provider access, which this build does not wire up.",
                "Terminal Selection" => "The Visual Studio terminal is not wired up in this build.",
                _ => "The agent already reaches the whole solution with search_files and list_files."
            };
        menu.PlacementTarget = HamburgerButton; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
    }

    private async Task PickFilesAsync()
    {
        var root = await SolutionRootAsync();
        if (root == null) { StateText.Text = "Open a solution first"; return; }
        await PickIntoAsync(root);
    }

    /// <summary>The picker shared by the paperclip and the '#' gesture.</summary>
    private async Task PickIntoAsync(string root)
    {
        var chosen = new List<string>();
        _picking = true;
        try
        {
            var dialog = new FilePickerDialog(root) { Owner = System.Windows.Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            chosen.AddRange(dialog.Selected);
        }
        finally { _picking = false; }

        foreach (var path in chosen)
            Attach(new ChatContextItem { Label = ContextTools.Relative(root, path).Replace('\\', '/'), Kind = "file", Preview = path });
        if (chosen.Count > 0) StateText.Text = chosen.Count + (chosen.Count == 1 ? " file attached" : " files attached");
        Composer.Focus();
    }

    private async Task AttachSelectionAsync()
    {
        var text = await Services.GetSelectionAsync();
        if (string.IsNullOrWhiteSpace(text)) { StateText.Text = "Nothing is selected in the editor"; return; }
        InsertContext(text);
    }

    /// <summary>The Error List, as text. Best-effort: it is empty in a solution that has not been built, which is not
    /// a failure worth reporting as one.</summary>
    private async Task AttachProblemsAsync()
    {
        var listed = await ProblemsAsync();
        if (listed.Length == 0) { StateText.Text = "The Error List is empty"; return; }
        Attach(new ChatContextItem
        {
            Label = "Problems · " + listed.Split('\n').Length + " entries",
            Kind = "problems",
            Preview = ChatStateLimits.Clip(listed, ChatStateLimits.MaxContextPreviewChars)
        });
        StateText.Text = "Problems attached";
    }

    /// <summary>The Error List, as text. The reader lives beside the build that fills it, so the Problems
    /// entry and the automatic post-edit verification cannot drift into two different readings of it.</summary>
    private Task<string> ProblemsAsync() => SolutionBuild.ReadErrorListAsync();

    private static async Task<string?> SolutionRootAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
        return ContextTools.SolutionRoot(dte);
    }
    private void Retry_Click(object sender, RoutedEventArgs e) => RetryLatest();
    private void RetryRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || _generation != null) return;
        var turn = Services.ActiveSession.Turns.LastOrDefault(t =>
            string.Equals(t.Messages.FirstOrDefault(m => m.Role == "user")?.Content, prompt, StringComparison.Ordinal));
        RetryTurn(turn);
    }
    private void RetryLatest()
    {
        var turn = Services.ActiveSession.Turns.LastOrDefault(t => t.Messages.Any(m => m.Role == "user") && (t.Status == ChatTurnStatus.Failed || t.Status == ChatTurnStatus.Complete));
        RetryTurn(turn);
    }
    private void RetryTurn(ChatTurnState? turn)
    {
        var session = Services.ActiveSession; var prompt = turn?.Messages.FirstOrDefault(m => m.Role == "user")?.Content; if (string.IsNullOrWhiteSpace(prompt) || _generation != null) return;
        while (session.Turns.Count > 0 && session.Turns[session.Turns.Count - 1] != turn) session.Turns.RemoveAt(session.Turns.Count - 1); if (turn != null) session.Turns.Remove(turn);
        RenderSession();
        // What the turn was asked with goes back into the composer with the words, so a retry asks the same question
        // rather than the same question with its attachments quietly missing.
        // The automatic chip is left out of what is put back: it is re-derived from the editor on the next activation,
        // and restoring it as a manual chip would leave a stale file pinned to the message.
        _attachments.Clear();
        if (turn != null) _attachments.AddRange(turn.Context.Where(c => !Same(c, _activeFileContext)));
        RefreshContextBar();
        // Set behind the same guard the '#' gesture uses: a prompt that happens to end in one must not open the picker.
        _picking = true;
        try { Composer.Text = prompt!; Composer.CaretIndex = Composer.Text.Length; }
        finally { _picking = false; }
        _ = SendAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await SafeRefreshAsync();
    private async Task SafeRefreshAsync() { try { StateText.Text = "Refreshing models…"; await Services.RefreshAsync(CancellationToken.None); } catch (Exception ex) { StateText.Text = ex.Message; } }
    private void Clear_Click(object sender, RoutedEventArgs e) => Clear(); private void Stop_Click(object sender, RoutedEventArgs e) => _generation?.Cancel();
    /// <summary>A starter prompt in the empty state, the way the chat view offers one before the first message. It is a
    /// real send rather than a filled draft, because a suggestion the reader has to press Enter on is a suggestion
    /// that just cost them a step.</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (!((sender as Button)?.Tag is string prompt)) return;
        _picking = true;
        try { Composer.Text = prompt; Composer.CaretIndex = prompt.Length; RefreshComposerState(); }
        finally { _picking = false; }
        _ = SendAsync();
    }

    private async void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        Composer.FlowDirection = TextDirection.For(Composer.Text);
        RefreshComposerState();
        await RefreshComposerSuggestionsAsync();
        if (_picking) return;

        // '#' at the start of a word is Visual Studio Code's gesture for "attach something". The character is taken
        // back out before the picker opens, so the prompt is not left carrying it whichever way the picker is closed.
        var text = Composer.Text;
        if (text.Length == 0 || text[text.Length - 1] != '#') return;
        if (text.Length > 1 && !char.IsWhiteSpace(text[text.Length - 2])) return;
        // The inline picker already owns the character when there are tools to choose from; taking it back would
        // turn the filter the user is typing into the file picker.
        if (CanMentionMcpTools()) return;
        _picking = true;
        try { Composer.Text = text.Substring(0, text.Length - 1); Composer.CaretIndex = Composer.Text.Length; }
        finally { _picking = false; }

        var root = await SolutionRootAsync();
        if (root == null) { StateText.Text = "Open a solution first"; return; }
        // The picker still opens — '#' keeps its old meaning with no tools — but not before saying why there is
        // nothing to mention: a user who expected tools should not have to discover that from a silent fallback.
        if (McpEnabledButNotConnected(root))
            StateText.Text = "MCP is not connected — enable a server in the Dashboard or press ↻ on its row";
        await PickIntoAsync(root);
    }
    private async void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SlashCommandPopup.IsOpen)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                var count = SlashCommandList.Items.Count;
                if (count > 0)
                {
                    var delta = e.Key == Key.Down ? 1 : -1;
                    SlashCommandList.SelectedIndex = (SlashCommandList.SelectedIndex + delta + count) % count;
                    SlashCommandList.ScrollIntoView(SlashCommandList.SelectedItem);
                }
                e.Handled = true; return;
            }
            if ((e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) || e.Key == Key.Tab)
            { e.Handled = true; InsertSelectedSuggestion(); return; }
            if (e.Key == Key.Escape) { SlashCommandPopup.IsOpen = false; e.Handled = true; return; }
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; await SendAsync(); }
    }

    private async Task RefreshComposerSuggestionsAsync()
    {
        // A programmatic write — retry restoring a prompt, a starter prompt, inserting a suggestion — must not open
        // a picker from the text it just put back. Retry in particular restores a prompt that may end in a token.
        if (_picking) { SlashCommandPopup.IsOpen = false; return; }

        var refreshVersion = ++_suggestionRefreshVersion;
        string filter;
        if (FileMentions.TryGetFilter(Composer.Text, Composer.CaretIndex, out _slashTokenStart, out _slashTokenLength, out filter))
        {
            _fileMentionMode = true;
            _toolMentionMode = false;
            _promptMode = false;
            var root = await SolutionRootAsync();
            if (root != null && !string.Equals(root, _mentionRoot, StringComparison.OrdinalIgnoreCase))
            {
                _mentionRoot = root;
                _mentionFiles = await Task.Run(() => ReadMentionFiles(root));
            }
            if (refreshVersion != _suggestionRefreshVersion ||
                !FileMentions.TryGetFilter(Composer.Text, Composer.CaretIndex, out _, out _, out var currentFilter) ||
                !string.Equals(filter, currentFilter, StringComparison.Ordinal)) return;
            var matches = new List<FileMentionEntry>();
            // Resources come first: a server offers a handful and they are the rows a reader is least likely to
            // find by scrolling through a solution tree. They are also offered when no solution is open, because
            // a resource does not live in one.
            matches.AddRange(Services.Mcp.Resources
                .Where(resource => Matches(filter, resource.DisplayName, resource.Uri, resource.Server.Name))
                .Take(50)
                .Select(resource => new FileMentionEntry
                {
                    Name = resource.Server.Name + " · " + resource.DisplayName,
                    Description = "MCP resource · " + resource.Uri,
                    Resource = resource
                }));
            if (root != null)
                matches.AddRange(_mentionFiles.Where(f => filter.Length == 0 || f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(100));
            SlashCommandList.ItemsSource = matches;
            SlashCommandList.SelectedIndex = matches.Count > 0 ? 0 : -1;
            SlashCommandPopup.IsOpen = matches.Count > 0;
            return;
        }

        _fileMentionMode = false;
        // '#' is the MCP tool picker when there are tools to offer and the backend is the one this loop runs. On
        // the Claude backend, with MCP off, or with no connected tool it keeps its old meaning below: remove the
        // character and open the file picker.
        if (CanMentionMcpTools() &&
            McpToolMentions.TryGetFilter(Composer.Text, Composer.CaretIndex,
                out _slashTokenStart, out _slashTokenLength, out filter))
        {
            _toolMentionMode = true;
            _promptMode = false;
            var tools = Services.Mcp.Tools
                .Where(tool => Matches(filter, tool.Name, tool.ExposedName, tool.DisplayName, tool.Server.Name,
                    McpToolMentions.TokenFor(tool)))
                .Take(50)
                .Select(tool => new FileMentionEntry
                {
                    Name = McpToolMentions.TokenFor(tool),
                    Description = "MCP tool · " + tool.Server.Name +
                                  (string.IsNullOrWhiteSpace(tool.Description)
                                      ? ""
                                      : " · " + ChatStateLimits.OneLine(tool.Description, 160)),
                    Tool = tool
                })
                .ToList();
            // The files row is filtered like any other, so a C# directive such as '#region' produces no popup at
            // all instead of a single row where Enter would open the file dialog.
            if (filter.Length == 0 || Matches(filter, "Files & Folders", "file", "folder"))
                tools.Add(new FileMentionEntry
                {
                    Name = "Files & Folders…",
                    Description = "Attach files and folders",
                    FilesPicker = true
                });
            SlashCommandList.ItemsSource = tools;
            SlashCommandList.SelectedIndex = tools.Count > 0 ? 0 : -1;
            SlashCommandPopup.IsOpen = tools.Count > 0;
            return;
        }

        _toolMentionMode = false;
        // /skill is the extension's own command on either backend, so its completion is offered before the
        // backend's own slash parser can claim the token. The snapshot is read, not rescanned on every keystroke:
        // the registry refreshes on load, on a turn and when the Skills tab is used.
        if (Services.Settings.EnableSkills &&
            SkillSlashCommand.TryGetNameFilter(Composer.Text, Composer.CaretIndex,
                out _slashTokenStart, out _slashTokenLength, out filter))
        {
            _skillMentionMode = true;
            _promptMode = false;
            if (Services.Skills.Snapshot.ScannedUtc == DateTime.MinValue)
            {
                try { await Services.PrepareSkillsAsync(await SolutionRootAsync()); }
                catch (Exception ex) { Services.Log("Skill discovery failed: " + ex); }
                if (refreshVersion != _suggestionRefreshVersion) return;
                if (!SkillSlashCommand.TryGetNameFilter(Composer.Text, Composer.CaretIndex,
                        out _slashTokenStart, out _slashTokenLength, out var currentSkillFilter) ||
                    !string.Equals(filter, currentSkillFilter, StringComparison.Ordinal)) return;
            }
            var skills = Services.Skills.Snapshot.Effective
                .Where(skill => Matches(filter, skill.Name, skill.Description))
                .Take(50)
                .Select(skill => new PromptSuggestion
                {
                    Name = skill.Name,
                    Description = (skill.Scope == SkillScope.Project ? "Project" : "Personal") +
                                  (string.IsNullOrWhiteSpace(skill.Description)
                                      ? ""
                                      : " · " + ChatStateLimits.OneLine(skill.Description, 160)),
                    Skill = skill
                })
                .ToList();
            SlashCommandList.ItemsSource = skills;
            SlashCommandList.SelectedIndex = skills.Count > 0 ? 0 : -1;
            SlashCommandPopup.IsOpen = skills.Count > 0;
            return;
        }
        _skillMentionMode = false;
        var claude = string.Equals(Services.ActiveSession.ExecutionBackend, "claude", StringComparison.OrdinalIgnoreCase);
        if (!ClaudeSlashCommands.TryGetFilter(Composer.Text, Composer.CaretIndex, out _slashTokenStart, out _slashTokenLength, out filter))
        { _promptMode = false; SlashCommandPopup.IsOpen = false; return; }

        // "/" is also the manual skill picker, on either backend. The rows a backend adds come after it, so a
        // skill is the first thing the list offers while MCP prompts and the CLI's own commands stay reachable.
        if (Services.Settings.EnableSkills && Services.Skills.Snapshot.ScannedUtc == DateTime.MinValue)
        {
            try { await Services.PrepareSkillsAsync(await SolutionRootAsync()); }
            catch (Exception ex) { Services.Log("Skill discovery failed: " + ex); }
            if (refreshVersion != _suggestionRefreshVersion) return;
            if (!ClaudeSlashCommands.TryGetFilter(Composer.Text, Composer.CaretIndex,
                    out _slashTokenStart, out _slashTokenLength, out var currentFilter) ||
                !string.Equals(filter, currentFilter, StringComparison.Ordinal)) return;
        }

        var rows = new List<object>();
        rows.AddRange(SkillSuggestions(filter));
        if (!claude)
        {
            // The LiteLLM backend has no CLI slash commands of its own, so "/" is where its servers' prompts live.
            _promptMode = true;
            rows.AddRange(Services.Mcp.Prompts
                .Where(prompt => Matches(filter, prompt.DisplayName, prompt.Name, prompt.Server.Name))
                .Take(100)
                .Select(prompt => new PromptSuggestion
                {
                    Name = "/" + prompt.Name,
                    Description = "MCP · " + prompt.Server.Name +
                                  (string.IsNullOrWhiteSpace(prompt.Description) ? "" : " · " + prompt.Description!.Trim()),
                    Prompt = prompt
                }));
        }
        else
        {
            _promptMode = false;
            rows.AddRange(ClaudeSlashCommands.Filter(filter));
        }
        SlashCommandList.ItemsSource = rows;
        SlashCommandList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        SlashCommandPopup.IsOpen = rows.Count > 0;
    }

    /// <summary>The skills "/" offers. This is the manual selection UI: a row the conversation already has is
    /// ticked, and choosing it takes it back. Only the effective definition of a name is listed; the Dashboard is
    /// where a shadowed copy and its reason are shown.</summary>
    private List<PromptSuggestion> SkillSuggestions(string filter)
    {
        if (!Services.Settings.EnableSkills) return new List<PromptSuggestion>();
        var selected = Services.ActiveSession.SelectedSkills ?? new List<string>();
        return Services.Skills.Snapshot.Effective
            .Where(skill => Matches(filter, skill.Name, skill.Description))
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(skill =>
            {
                var isSelected = selected.Any(name => string.Equals(name, skill.Name, StringComparison.OrdinalIgnoreCase));
                return new PromptSuggestion
                {
                    Name = (isSelected ? "✓ " : "") + skill.Name,
                    Description = "Skill · " + (skill.Scope == SkillScope.Project ? "Project" : "Personal") +
                                  (string.IsNullOrWhiteSpace(skill.Description)
                                      ? ""
                                      : " · " + ChatStateLimits.OneLine(skill.Description, 160)),
                    Skill = skill
                };
            })
            .ToList();
    }

    /// <summary>Whether a <c>#</c> in the composer can name an MCP tool. The tools come from the snapshot a previous
    /// LiteLLM turn or a Dashboard connection filled; a keystroke never connects a server by itself.</summary>
    private bool CanMentionMcpTools() =>
        string.Equals(Services.ActiveSession.ExecutionBackend, "litellm", StringComparison.OrdinalIgnoreCase) &&
        Services.Settings.EnableToolCalling &&
        Services.Settings.EnableMcp &&
        Services.Mcp.Tools.Count > 0;

    private static bool Matches(string filter, params string?[] values)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value) && value!.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static IReadOnlyList<FileMentionEntry> ReadMentionFiles(string root)
    {
        var files = new List<FileMentionEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ContextTools.IsNoise(path) || Array.IndexOf(ContextTools.ScannableExtensions, Path.GetExtension(path).ToLowerInvariant()) < 0) continue;
                files.Add(new FileMentionEntry { FullPath = path, Name = ContextTools.Relative(root, path).Replace('\\', '/') });
                if (files.Count >= 6000) break;
            }
        }
        catch (Exception) { }
        return files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void InsertSelectedSuggestion() => _ = InsertSelectedSuggestionAsync();

    private async Task InsertSelectedSuggestionAsync()
    {
        if (_toolMentionMode)
        {
            if (!(SlashCommandList.SelectedItem is FileMentionEntry row)) return;
            if (row.FilesPicker)
            {
                // The token goes back out before the picker opens, exactly as the old '#' gesture did, so the
                // prompt is not left carrying a '#' whichever way the dialog is closed.
                _picking = true;
                try
                {
                    Composer.Text = FileMentions.RemoveToken(Composer.Text, _slashTokenStart, MentionSpanLength());
                    Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart);
                    SlashCommandPopup.IsOpen = false;
                }
                finally { _picking = false; }
                var root = await SolutionRootAsync();
                if (root == null) { StateText.Text = "Open a solution first"; return; }
                await PickIntoAsync(root);
                Composer.Focus();
                return;
            }

            if (row.Tool == null) return;
            var token = McpToolMentions.TokenFor(row.Tool);
            _picking = true;
            try
            {
                Composer.Text = ClaudeSlashCommands.ReplaceToken(Composer.Text, _slashTokenStart,
                    MentionSpanLength(), token);
                Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart + token.Length + 1);
                SlashCommandPopup.IsOpen = false;
            }
            finally { _picking = false; }
            // Selecting is not invoking: the token narrows the next turn, and the row that says a call ran is
            // drawn only when the model actually calls it.
            Composer.Focus();
            return;
        }

        if (_fileMentionMode)
        {
            if (!(SlashCommandList.SelectedItem is FileMentionEntry file)) return;
            var resource = file.Resource;
            _picking = true;
            try
            {
                Composer.Text = FileMentions.RemoveToken(Composer.Text, _slashTokenStart, MentionSpanLength());
                Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart);
                SlashCommandPopup.IsOpen = false;
                if (resource == null)
                {
                    Attach(new ChatContextItem { Label = file.Name, Kind = "file", Preview = file.FullPath });
                    StateText.Text = "File attached";
                }
            }
            finally { _picking = false; }
            if (resource != null) await AttachResourceAsync(resource);
            Composer.Focus();
            return;
        }

        if (SlashCommandList.SelectedItem is PromptSuggestion skillRow && skillRow.Skill != null)
        {
            var skillName = skillRow.Skill.Name;
            _picking = true;
            try
            {
                if (_skillMentionMode)
                {
                    // Only the name word is replaced: the /skill command stays in front of it, so the next Enter
                    // takes the ordinary handled-command path instead of sending a bare name as ordinary text.
                    Composer.Text = ClaudeSlashCommands.ReplaceToken(Composer.Text, _slashTokenStart,
                        MentionSpanLength(), skillName);
                    Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart + skillName.Length);
                }
                else
                {
                    // Chosen from "/": the command word is only the way into this list, so it goes back out and
                    // the skill itself becomes the conversation's selection. The chip above the composer is then
                    // where it is dropped, exactly as before.
                    Composer.Text = FileMentions.RemoveToken(Composer.Text, _slashTokenStart, MentionSpanLength());
                    Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart);
                }
                SlashCommandPopup.IsOpen = false;
            }
            finally { _picking = false; }
            if (!_skillMentionMode) ToggleSkill(skillName);
            Composer.Focus();
            return;
        }

        if (_promptMode)
        {
            if (!(SlashCommandList.SelectedItem is PromptSuggestion suggestion)) return;
            var dialog = new McpPromptDialog(suggestion.Prompt, Services.Mcp) { Owner = System.Windows.Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                _picking = true;
                try
                {
                    Composer.Text = ClaudeSlashCommands.ReplaceToken(Composer.Text, _slashTokenStart,
                        MentionSpanLength(), dialog.Text);
                    Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart + dialog.Text.Length + 1);
                    SlashCommandPopup.IsOpen = false;
                }
                finally { _picking = false; }
            }
            Composer.Focus();
            return;
        }

        if (!(SlashCommandList.SelectedItem is ClaudeSlashCommand command)) return;
        _picking = true;
        try
        {
            Composer.Text = ClaudeSlashCommands.ReplaceToken(Composer.Text, _slashTokenStart, MentionSpanLength(), command.Name);
            Composer.CaretIndex = Math.Min(Composer.Text.Length, _slashTokenStart + command.Name.Length + 1);
            SlashCommandPopup.IsOpen = false;
        }
        finally { _picking = false; }
        Composer.Focus();
    }

    /// <summary>Reads an MCP resource now and attaches its text, rather than attaching a promise to read it later:
    /// a send happens after the chat window has had time to advance, and a failure at that point would have no
    /// composer left to report it in. The text is capped by <see cref="McpResourceContents.ToText"/>.</summary>
    private async Task AttachResourceAsync(McpResourceInfo resource)
    {
        StateText.Text = "Reading " + resource.Uri + "…";
        try
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
            {
                var read = await Services.Mcp.ReadResourceAsync(resource.Server.Id, resource.Uri, cancellation.Token);
                var text = string.Join(Environment.NewLine + Environment.NewLine,
                    read.Contents.Select(content => content.ToText(null)));
                if (string.IsNullOrWhiteSpace(text))
                {
                    StateText.Text = "The server returned no content for " + resource.Uri;
                    return;
                }
                Attach(new ChatContextItem
                {
                    Label = resource.DisplayName + " · " + resource.Server.Name,
                    Kind = "mcp-resource",
                    Preview = text
                });
                StateText.Text = "MCP resource attached";
            }
        }
        catch (OperationCanceledException) { StateText.Text = "The resource did not arrive within a minute."; }
        catch (Exception ex) { StateText.Text = "MCP resource could not be read: " + McpLog.Redact(ex.Message); }
    }

    private void RefreshSlashCommands() => _ = RefreshComposerSuggestionsAsync();

    private void SlashCommandList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A press on the scrollbar or the empty area below the rows also raises this event, and the selection is
        // preset to the first row: without the container check, scrolling a long list would attach the top file.
        if (SlashCommandList.SelectedItem == null) return;
        if (!(ItemsControl.ContainerFromElement(SlashCommandList, e.OriginalSource as DependencyObject) is ListBoxItem))
            return;
        e.Handled = true;
        InsertSelectedSuggestion();
    }

    /// <summary>The token under the caret runs from its marker to the next whitespace, not only to the caret. The
    /// picker's filter ends at the caret because that is what narrows the list; accepting a row must take the whole
    /// token, or a prompt edited in the middle of one keeps the tail ("@a.cs" with the caret after "a" would leave
    /// ".cs" behind).</summary>
    private int MentionSpanLength()
    {
        var end = _slashTokenStart + _slashTokenLength;
        while (end < Composer.Text.Length && !char.IsWhiteSpace(Composer.Text[end])) end++;
        return end - _slashTokenStart;
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ModelBox.SelectedItem is ModelInfo m) { ModelBox.ToolTip = m.Id; Services.Settings.SelectedModelKey = m.Key; Services.ActiveSession.SelectedModelKey = m.Key; Services.Save(); Services.SaveSessions(); } }
    private void ReloadBackend()
    {
        if (BackendBox == null) return;
        var backend = Services.ActiveSession.ExecutionBackend;
        BackendBox.SelectedItem = BackendBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, backend, StringComparison.OrdinalIgnoreCase)) ?? BackendBox.Items[0];
        ModelBox.IsEnabled = _generation == null && !string.Equals(backend, "claude", StringComparison.OrdinalIgnoreCase);
    }
    private void BackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_generation != null || !(BackendBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string backend)) return;
        var normalized = string.Equals(backend, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "litellm";
        if (!string.Equals(Services.ActiveSession.ExecutionBackend, normalized, StringComparison.Ordinal))
        {
            Services.ActiveSession.ExecutionBackend = normalized;
            SaveSession();
            // A genuine switch back is where the LiteLLM loop first gets to use MCP, and the load-time warm-up was
            // skipped while the Claude backend was selected. ReloadBackend selects programmatically without
            // changing the stored backend, so it cannot start a connect loop.
            if (normalized == "litellm") _ = AutoConnectMcpAsync(_loadVersion);
        }
        ModelBox.IsEnabled = normalized == "litellm";
        SlashCommandPopup.IsOpen = false;
        if (normalized == "claude") RefreshSlashCommands();
    }
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ModeBox.SelectedItem is ComboBoxItem item && item.Tag is string mode) _mode = mode; }

    private bool AllowAllPermissions => string.Equals(Services.Settings.PermissionMode, "allowAll", StringComparison.OrdinalIgnoreCase);

    private void ReloadPermissionMode() => RefreshPermissionToolTip();

    private void SetPermissionMode(string mode)
    {
        if (string.Equals(Services.Settings.PermissionMode, mode, StringComparison.OrdinalIgnoreCase)) return;
        Services.Settings.PermissionMode = mode;
        var options = (LiteLlmOptionsPage)LiteLlmPackage.Instance.GetDialogPage(typeof(LiteLlmOptionsPage));
        options.PermissionMode = mode;
        // Permission mode is intentionally independent from tool availability. Switching back from Allow all must
        // only restore approvals; it must not disable file editing, terminal tools, or tool calling themselves.
        Services.Save();
        RefreshPermissionToolTip();
        StateText.Text = mode == "allowAll" ? "Allow all enabled — tool approvals are automatic." : "Default permissions enabled.";
    }

    private void RefreshPermissionToolTip()
    {
        HamburgerButton.ToolTip = (Services.Settings.AutoAttachActiveDocument
            ? "Chat actions · Auto context ON"
            : "Chat actions · Auto context OFF") + (AllowAllPermissions
            ? " · Allow all permissions"
            : " · Default permissions");
    }
    private void ConversationScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.ExtentHeightChange == 0) _follow = ConversationScroll.VerticalOffset >= ConversationScroll.ScrollableHeight - 12; }
    private void ScrollIfFollowing() { if (_follow) ConversationScroll.ScrollToEnd(); }

    /// <summary>Sending always returns the view to the newest message, even if the user had scrolled away: otherwise
    /// the message they just wrote is drawn off-screen and the reply streams where they cannot see it. The scroll is
    /// queued because the bubble has not been laid out in its final position yet.</summary>
    private void ScrollToSent(Border message)
    {
        _follow = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            message.BringIntoView();
            ConversationScroll.ScrollToEnd();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Fills the list from the bubbles that are on screen now. Rebuilt on open rather than kept in step by an
    /// event: the list is only read when it is shown, and a turn that is still streaming can change it underneath.</summary>
    private void Prompts_Click(object sender, RoutedEventArgs e)
    {
        var rows = new List<PromptRow>(_userPrompts.Count);
        for (var index = 0; index < _userPrompts.Count; index++)
            rows.Add(new PromptRow(index + 1, _userPrompts[index]));
        PromptsList.ItemsSource = rows;
        if (rows.Count == 0)
        {
            StateText.Text = "No messages in this chat yet.";
            return;
        }
        PromptsPopup.IsOpen = true;
    }

    private void PromptsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Same guard as the composer's picker: a click on the scrollbar or the empty area must not jump to whatever
        // row happened to be selected.
        if (!(PromptsList.SelectedItem is PromptRow row)) return;
        if (!(ItemsControl.ContainerFromElement(PromptsList, e.OriginalSource as DependencyObject) is ListBoxItem)) return;
        e.Handled = true;
        PromptsPopup.IsOpen = false;
        JumpToPrompt(row.Element);
    }

    /// <summary>Brings an earlier prompt to the top of the view. Jumping is a deliberate move away from the live
    /// answer, so following is turned off: the next streamed delta must not drag the view back down.</summary>
    private void JumpToPrompt(Border element)
    {
        if (element == null) return;
        _follow = false;
        ConversationScroll.UpdateLayout();
        var position = element.TranslatePoint(new Point(0, 0), Messages);
        ConversationScroll.ScrollToVerticalOffset(Math.Max(0, position.Y - 12));
    }

    /// <summary>One prompt as the transcript drew it.</summary>
    private sealed class PromptEntry
    {
        internal PromptEntry(string text, Border element) { Text = text; Element = element; }
        internal string Text { get; }
        internal Border Element { get; }
    }

    /// <summary>A row in the prompt list. The number is its place in the conversation, so a long list can be scanned
    /// without reading every line; the full text lives in the tooltip.</summary>
    private sealed class PromptRow
    {
        internal PromptRow(int number, PromptEntry entry)
        {
            Number = number.ToString();
            Text = ChatStateLimits.OneLine(entry.Text, 120);
            Detail = entry.Text;
            Element = entry.Element;
        }
        public string Number { get; }
        public string Text { get; }
        public string Detail { get; }
        internal Border Element { get; }
    }
}


