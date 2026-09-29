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

namespace LiteLLM.VisualStudio;
public partial class ChatControl : UserControl
{
    internal static ChatControl? Current { get; private set; }

    /// <summary>One rendered message: the card, its content panel, and — while the turn runs — the live status line at its foot.</summary>
    private sealed class Bubble
    {
        internal Border Border = null!;
        internal StackPanel Body = null!;
        internal StackPanel? Live;
        internal TextBlock? Status;
        internal ProgressBar? Bar;
        internal ChatMessage? Message;
        internal HttpExchange? Exchange;
    }

    private readonly List<ChatMessage> _history = new List<ChatMessage>();
    private CancellationTokenSource? _generation;
    private DispatcherTimer? _ticker;
    private Bubble? _live;
    private HttpExchange? _lastExchange;
    private DateTime _started;
    private int _streamed;
    private string _phase = "Thinking";
    private bool _follow = true;
    private bool _narrow;
    private ChatTurnState? _activeTurn;
    private int _turnStart;
    /// <summary>What the user has attached to the next message. Draft state, like the composer's text: it belongs to
    /// the message being written, not yet to the conversation.</summary>
    private readonly List<ChatContextItem> _attachments = new List<ChatContextItem>();
    /// <summary>Set while a picker is open or the composer is being written to, so the '#' gesture does not fire on our own edits.</summary>
    private bool _picking;
    /// <summary>The file open in the editor, held apart from <see cref="_attachments"/> so that rebuilding it as the
    /// user moves between files can never disturb what was picked by hand, and so the × on one chip means "turn this
    /// off" rather than "forget the file until the next click". Null while the setting is off or no file is open.</summary>
    private ChatContextItem? _activeFileContext;
    private DTE? _dte;
    private WindowEvents? _windowEvents;

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
        Grid.SetRow(HeaderTools, narrow ? 1 : 0); Grid.SetColumn(HeaderTools, narrow ? 1 : 2); Grid.SetColumnSpan(HeaderTools, 1);
        HeaderTools.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        SessionBox.MaxWidth = narrow ? available : 180;
        ModelBox.MaxWidth = narrow ? available : 200;

        // The composer's own button strip gets the same treatment: beside the status line when there is room, and on a
        // line of its own under it when there is not, where it has the whole pane to wrap into rather than being cut
        // off at the right edge. It stays in the star column for the same reason the header row does.
        Grid.SetRow(ComposerTools, narrow ? 1 : 0);
        Grid.SetColumn(ComposerTools, narrow ? 0 : 1);
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
        if (!_themeReported) { _themeReported = true; Services.Log(VsTheme.Describe()); }
        Services.ModelsChanged += ModelsChanged; Services.SessionsChanged += SessionsChanged;
        // The edit tools ask the chat, rather than a modal dialog, because the question belongs in the conversation
        // where the change it is asking about is already on screen.
        EditTools.Confirm = AskToApplyEditAsync;
        ReloadSessions(); RenderSession(); ReloadModels();
        RefreshAutoContextButton();
        await WatchEditorAsync();
        if (Services.Models.Count == 0) await SafeRefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A tool window can be closed and reopened, and each reopen builds a new control. Dropping the DTE event here
        // is what stops the closed ones from being kept alive by the editor and refreshing a tree nobody can see.
        if (_windowEvents == null) return;
        try { _windowEvents.WindowActivated -= OnWindowActivated; } catch (Exception) { }
        _windowEvents = null; _dte = null;
    }
    private void ModelsChanged(object sender, EventArgs e) => Dispatcher.Invoke(ReloadModels);
    private void SessionsChanged(object sender, EventArgs e) => Dispatcher.Invoke(ReloadSessions);
    private void ReloadSessions() { SessionBox.ItemsSource = null; SessionBox.ItemsSource = Services.Sessions.Sessions; SessionBox.SelectedValuePath = "Id"; SessionBox.SelectedValue = Services.Sessions.ActiveSessionId; RefreshDeleteButton(false); }
    private void ReloadModels() { var selected = Services.ActiveSession.SelectedModelKey.Length > 0 ? Services.ActiveSession.SelectedModelKey : Services.Settings.SelectedModelKey; ModelBox.ItemsSource = null; ModelBox.ItemsSource = Services.Models; ModelBox.SelectedItem = Services.Models.FirstOrDefault(m => m.Key == selected) ?? Services.Models.FirstOrDefault(); StateText.Text = Services.Models.Count == 0 ? "Configure a server in the Dashboard" : "Ready"; }
    private void RenderSession()
    {
        Messages.Children.Clear(); Messages.Children.Add(EmptyState); _history.Clear();
        foreach (var turn in Services.ActiveSession.Turns)
            foreach (var message in turn.Messages)
            {
                if (message.Role == "user") AddMessage("You", message.Content, false);
                else if (message.Role == "assistant") AddMessage("Assistant", Describe(message), true);
                else if (message.Role == "tool") AddToolNote(message.ToolName, "Tool result · " + message.ToolName, true);
                _history.Add(ChatStateLimits.CopyMessage(message));
            }
        EmptyState.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveSession() { var session = Services.ActiveSession; session.UpdatedUtc = DateTime.UtcNow; session.SelectedModelKey = (ModelBox.SelectedItem as ModelInfo)?.Key ?? session.SelectedModelKey; if (session.HasMessages && session.Title == "New chat") { var first = session.Turns.SelectMany(t => t.Messages).FirstOrDefault(m => m.Role == "user"); if (first != null) session.Title = ChatStateLimits.OneLine(first.Content, 42); } Services.SaveSessions(); }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        var input = Composer.Text.Trim(); if (input.Length == 0 || !(ModelBox.SelectedItem is ModelInfo model) || _generation != null) return;
        Composer.Clear(); EmptyState.Visibility = Visibility.Collapsed;
        _history.Add(new ChatMessage { Role = "user", Content = input }); AddMessage("You", input, false);
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
            Services.ApplyOptions();
            // Copilot Chat treats the round budget as a setting and asks before spending more rather than dying
            // at a fixed count. Clamp after ApplyOptions so a mid-session change still takes effect this turn.
            var limit = ChatStateLimits.ClampToolRounds(Services.Settings.MaxToolRounds);
            var stopped = false; ChatMessage? last = null; var finish = "";
            for (var round = 1; ; round++)
            {
                var assistant = new ChatMessage { Role = "assistant" };
                _history.Add(assistant); last = assistant;
                finish = "";
                // Sanitizing is what keeps the request valid: every tool result must answer a call the assistant
                // announced and every announced call must be answered, or an OpenAI-compatible server rejects
                // the whole transcript. A cancelled tool batch or a history loaded from an older build can
                // otherwise leave the two out of step.
                var history = ChatStateLimits.Sanitize(_history.Take(_history.Count - 1));
                // Built here, not stored: the system prompt and the attachment block are added at request time and the
                // transcript keeps only what the user typed and what the model answered.
                // With the tools switched off an attached file is only a path, and the model has no way to open it, so
                // a bounded excerpt goes with the request instead. The turn keeps the path either way, so the chat and
                // the stored session are unaffected by which way the setting is set.
                var context = Services.Settings.EnableToolCalling
                    ? _activeTurn?.Context
                    : AgentPrompt.InlineFiles(_activeTurn?.Context, ReadAttachedText);
                var request = AgentPrompt.Build(history, context, Services.Settings.EnableToolCalling, Services.Settings.EnableFileEditing);

                var view = AddAssistantBubble(round == 1 ? "Thinking" : "Reading the tool results");
                _streamed = 0; _phase = round == 1 ? "Thinking" : "Reading the tool results";

                if (Services.Settings.EnableStreaming)
                    await Services.StreamAsync(model, request, async update =>
                    {
                        assistant.Content += update.ContentDelta; assistant.Reasoning += update.ReasoningDelta;
                        if (update.ToolCalls.Count > 0) assistant.ToolCalls = update.ToolCalls;
                        if (update.FinishReason.Length > 0) finish = update.FinishReason;
                        _streamed = assistant.Content.Length;
                        _phase = assistant.Content.Length > 0 ? "Writing" : "Thinking";
                        await Dispatcher.InvokeAsync(() => SetContent(view, assistant));
                    }, token).ConfigureAwait(true);
                else
                {
                    var complete = await Services.CompleteAsync(model, request, token).ConfigureAwait(true);
                    assistant.Content = complete.Content; assistant.Reasoning = complete.Reasoning; assistant.ToolCalls = complete.ToolCalls;
                    SetContent(view, assistant);
                }
                _lastExchange = LastExchange; view.Exchange = _lastExchange;   // attach the raw JSON to the bubble that came from it
                // One assistant message per round, appended in order. Overwriting the turn's first assistant
                // instead would record every round's calls against a single message, leaving the stored turn
                // with results that answer nothing and calls that are never answered.
                _activeTurn?.Messages.Add(ChatStateLimits.CopyMessage(assistant));
                FinishStatus(view);

                if (assistant.ToolCalls.Count == 0) { SaveSession(); break; }

                foreach (var call in assistant.ToolCalls)
                {
                    token.ThrowIfCancellationRequested();
                    _phase = "Running " + call.Name;
                    // The revision count says whether this call produced an edit, which is what puts the diff and the
                    // undo on this row rather than on the previous one.
                    var revisions = EditTools.Revisions;
                    var result = await ContextTools.ExecuteAsync(LiteLlmPackage.Instance, call.Name, call.Arguments).ConfigureAwait(true);
                    var toolMessage = new ChatMessage { Role = "tool", ToolCallId = call.Id, ToolName = call.Name, Content = result.Content };
                    _history.Add(toolMessage);
                    _activeTurn?.Messages.Add(ChatStateLimits.CopyMessage(toolMessage));
                    AddToolNote(call.Name, result.Summary, result.Ok, null, null, EditTools.Revisions > revisions ? EditTools.Latest : null);
                }
                SaveSession();   // once per round, not per tool: each save re-serializes the whole document

                // The budget is only consulted once the results are in, so what goes back to the model is always
                // a complete exchange — the same reason Copilot Chat checks at the top of its loop.
                if (round < limit) continue;
                if (ChatStateLimits.AtToolRoundsCeiling(limit))
                {
                    stopped = true;
                    AddToolNote("", "Stopped at the " + ChatStateLimits.MaxToolRoundsCeiling + "-round ceiling. The results so far are kept — send another message to continue from them.", false);
                    break;
                }
                var raised = ChatStateLimits.ExtendToolRounds(limit);
                if (!await AskToContinueAsync(limit, raised, token).ConfigureAwait(true)) { stopped = true; break; }
                limit = raised;
            }
            // Skipped when the user chose to stop: `last` is then the tool request they declined to continue, so
            // its emptiness says nothing about the model running out of budget, and the stop note is already up.
            if (!stopped && last != null && last.Content.Length == 0 && last.ToolCalls.Count == 0)
            {
                if (last.Reasoning.Length > 0)
                    AddToolNote("", "The model spent its whole " + Services.Settings.MaxTokens + "-token budget on reasoning and never wrote an answer. Raise \"Maximum tokens\" in Tools ▸ Options ▸ LiteLLM.", false);
                else if (finish == "length") AddToolNote("", "The reply hit the " + Services.Settings.MaxTokens + "-token limit before it produced any text.", false);
                else throw new LiteLlmException("The model returned an empty response.", "No content or tool calls.");
            }
            else if (!stopped && finish == "length") AddToolNote("", "The reply was cut off at the " + Services.Settings.MaxTokens + "-token limit.", false);
            if (_activeTurn != null) { _activeTurn.Status = ChatTurnStatus.Complete; _activeTurn.CompletedUtc = DateTime.UtcNow; }
            SaveSession(); StateText.Text = "Ready";
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
        finally { _ticker?.Stop(); _ticker = null; if (_live != null) FinishStatus(_live); _live = null; _generation.Dispose(); _generation = null; SetBusy(false); _activeTurn = null; Composer.Focus(); }
    }

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
    private void AddToolNote(string name, string summary, bool ok, HttpExchange? exchange = null, string? detail = null, AppliedEdit? edit = null)
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
        border.ContextMenu = Menu(
            Item("Copy error report", () => Copy(Report(exchange, summary, full), "Report"), !ok && exchange != null),
            Item("Copy summary", () => Copy(summary, "Summary")),
            Item("Copy full detail", () => Copy(full, "Detail"), full.Length > 0),
            null,
            Item("Copy diff", () => Copy(edit!.Diff, "Diff"), edit != null),
            Item("Undo this edit", () => UndoEdit(edit!), edit != null),
            Item("Copy request (JSON)", () => Copy(exchange?.RequestBody, "Request"), exchange != null),
            Item("Copy response (JSON)", () => Copy(exchange?.ResponseBody, "Response"), exchange != null));
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

    /// <summary>The question the edit tools ask when "Confirm before each edit" is on. It is a card in the transcript
    /// rather than a modal, so the diff it is asking about stays readable, and Skip has to be as easy to reach as
    /// Allow — a confirmation that only offers a way forward is not a confirmation.</summary>
    private async Task<bool> AskToApplyEditAsync(EditProposal proposal)
    {
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

    private Border AddMessage(string author, string content, bool assistant)
    {
        var bubble = CreateBubble(author, assistant);
        SetContent(bubble, new ChatMessage { Role = "assistant", Content = content });
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
        body.Children.Add(new TextBlock { Text = author, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        var border = new Border { Style = (Style)FindResource("Card"), Child = body, HorizontalAlignment = assistant ? HorizontalAlignment.Stretch : HorizontalAlignment.Right, MaxWidth = 900, MinWidth = 90, Margin = new Thickness(0, 0, 0, 12) };
        var bubble = new Bubble { Border = border, Body = body };
        border.ContextMenu = Menu(
            Item("Copy message", () => Copy(Describe(bubble.Message), "Message")),
            Item("Retry response", RetryLatest, assistant),
            null,
            Item("Copy request (JSON)", () => Copy(bubble.Exchange?.RequestBody, "Request"), bubble.Exchange != null),
            Item("Copy response (JSON)", () => Copy(bubble.Exchange?.ResponseBody, "Response"), bubble.Exchange != null));
        Messages.Children.Add(border);
        return bubble;
    }

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
        if (message.ToolCalls.Count == 0) return message.Content;
        var calls = string.Join("\n", message.ToolCalls.Select(c => c.Name + "(" + c.Arguments + ")"));
        return (message.Content.Length > 0 ? message.Content + "\n\n" : "") + "Tool calls:\n" + calls;
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
        menu.PlacementTarget = CopyButton; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
    }

    private static FrameworkElement Directed(string text) { var rendered = MarkdownRenderer.Render(text); return rendered; }

    /// <summary>Replaces the bubble's content, keeping the author line and any live status panel at the foot.</summary>
    private void SetContent(Bubble bubble, ChatMessage message)
    {
        bubble.Message = message;
        var body = bubble.Body; var keep = bubble.Live != null ? 1 : 0;
        while (body.Children.Count > 1 + keep) body.Children.RemoveAt(1);
        if (message.Reasoning.Length > 0 && ShowReasoning)
            body.Children.Insert(1, new Expander { Header = "Thinking · Show reasoning", Content = Directed(message.Reasoning), Margin = new Thickness(0, 2, 0, 6) });
        if (message.Content.Length > 0) body.Children.Insert(body.Children.Count - keep, Directed(message.Content));
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
        SendButton.IsEnabled = !value; StopButton.IsEnabled = value; ModelBox.IsEnabled = !value; SessionBox.IsEnabled = !value;
        RefreshDeleteButton(value);
        SendLabel.Text = value ? "Working…" : "Send  →";
        SendProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value) SendProgress.IsIndeterminate = true;
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
        Services.NewSession();   // fires SessionsChanged, which is what reloads the session list
        Messages.Children.Clear(); Messages.Children.Add(EmptyState); EmptyState.Visibility = Visibility.Visible;
        Composer.Clear(); ClearAttachments();
        StateText.Text = "Ready";
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

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        // Sitting in a chat window, "Clear" is read as "clear the conversation" — but that is the header's New chat.
        // What it can clear is the draft, and with an empty composer that was nothing at all, which is why it looked
        // dead. It now empties the composer and the attachments together and says which it did.
        var hadDraft = Composer.Text.Trim().Length > 0;
        var hadContext = _attachments.Count > 0;
        Composer.Clear(); ClearAttachments(); Composer.Focus();
        StateText.Text = !hadDraft && !hadContext ? "Nothing to clear"
            : hadDraft && hadContext ? "Draft and attachments cleared"
            : hadDraft ? "Draft cleared" : "Attachments cleared";
        RefreshClearButton();
    }
    /// <summary>Kept truthful rather than always enabled, so the button can never be the silent no-op it was.</summary>
    private void RefreshClearButton() => ClearInput.IsEnabled = Composer.Text.Length > 0 || _attachments.Count > 0;
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
        ContextBar.Visibility = _activeFileContext != null || _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshClearButton();
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
    private async Task WatchEditorAsync()
    {
        if (_windowEvents != null) return;   // Loaded can fire more than once; a second handler would double every refresh
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            _dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            if (_dte == null) return;
            _windowEvents = _dte.Events.WindowEvents;
            _windowEvents.WindowActivated += OnWindowActivated;
            await RefreshActiveFileAsync();
        }
        catch (Exception) { /* no DTE — design time, or a shell with no solution: the composer simply carries no chip */ }
    }

    private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus) => _ = RefreshActiveFileAsync();

    private void AutoContext_Click(object sender, RoutedEventArgs e) => SetAutoContext(!Services.Settings.AutoAttachActiveDocument);

    private void SetAutoContext(bool value)
    {
        Services.Settings.AutoAttachActiveDocument = value; Services.Save();
        RefreshAutoContextButton();
        _ = RefreshActiveFileAsync();
    }

    private void RefreshAutoContextButton()
    {
        var on = Services.Settings.AutoAttachActiveDocument;
        AutoContextButton.Content = on ? "◎ Auto" : "○ Auto";
        // The state is shown by the button's surface, never by its foreground. The label keeps the foreground every other
        // button in the row has, so it reads whatever the theme's accent happens to be; the accent rides on the wash and
        // the outline, where being close to the card colour only makes the button plainer, not its text unreadable.
        AutoContextButton.Style = (Style)FindResource(on ? "AutoButtonOn" : "AutoButtonOff");
        AutoContextButton.ToolTip = on
            ? "The file open in the editor is attached to every message. Click to stop."
            : "Attach the file open in the editor to every message.";
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
        menu.PlacementTarget = ContextButton; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
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

    /// <summary>The Error List's window kind. The ErrorList/ErrorItem automation types are not in the interop assembly
    /// this project compiles against, so the window is found by GUID and read late-bound.</summary>
    private const string ErrorListKind = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";

    private async Task<string> ProblemsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var window = dte?.Windows.Item(ErrorListKind)?.Object;
            if (window == null) return "";
            if (window.GetType().InvokeMember("ErrorItems", BindingFlags.GetProperty, null, window, null) is not System.Collections.IEnumerable items) return "";

            var text = new System.Text.StringBuilder();
            foreach (var item in items)
            {
                if (text.Length >= 12000) break;
                var type = item.GetType();
                string Read(string name) { try { return type.InvokeMember(name, BindingFlags.GetProperty, null, item, null)?.ToString() ?? ""; } catch (Exception) { return ""; } }

                text.Append(Read("ErrorLevel").Replace("vsErrorType", "").ToLowerInvariant()).Append(": ").Append(Read("Description"));
                var file = Read("FileName");
                if (file.Length > 0) text.Append(" — ").Append(file).Append(':').Append(Read("Line"));
                text.Append('\n');
            }
            return text.ToString().TrimEnd('\n');
        }
        catch (Exception) { return ""; }   // no build has run, or the Error List is not available yet
    }

    private static async Task<string?> SolutionRootAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
        return ContextTools.SolutionRoot(dte);
    }
    private void Retry_Click(object sender, RoutedEventArgs e) => RetryLatest();
    private void RetryLatest()
    {
        var session = Services.ActiveSession; var turn = session.Turns.LastOrDefault(t => t.Messages.Any(m => m.Role == "user") && (t.Status == ChatTurnStatus.Failed || t.Status == ChatTurnStatus.Complete)); var prompt = turn?.Messages.FirstOrDefault(m => m.Role == "user")?.Content; if (string.IsNullOrWhiteSpace(prompt) || _generation != null) return;
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

    private async void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        Composer.FlowDirection = TextDirection.For(Composer.Text);
        RefreshClearButton();
        if (_picking) return;

        // '#' at the start of a word is Visual Studio Code's gesture for "attach something". The character is taken
        // back out before the picker opens, so the prompt is not left carrying it whichever way the picker is closed.
        var text = Composer.Text;
        if (text.Length == 0 || text[text.Length - 1] != '#') return;
        if (text.Length > 1 && !char.IsWhiteSpace(text[text.Length - 2])) return;
        _picking = true;
        try { Composer.Text = text.Substring(0, text.Length - 1); Composer.CaretIndex = Composer.Text.Length; }
        finally { _picking = false; }

        var root = await SolutionRootAsync();
        if (root == null) { StateText.Text = "Open a solution first"; return; }
        await PickIntoAsync(root);
    }
    private async void Composer_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; await SendAsync(); } }
    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ModelBox.SelectedItem is ModelInfo m) { Services.Settings.SelectedModelKey = m.Key; Services.ActiveSession.SelectedModelKey = m.Key; Services.Save(); Services.SaveSessions(); } }
    private void ConversationScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.ExtentHeightChange == 0) _follow = ConversationScroll.VerticalOffset >= ConversationScroll.ScrollableHeight - 12; }
    private void ScrollIfFollowing() { if (_follow) ConversationScroll.ScrollToEnd(); }
}
