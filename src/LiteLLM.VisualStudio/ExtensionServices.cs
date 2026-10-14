using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

internal sealed class ExtensionServices : IDisposable
{
    private readonly AsyncPackage _package;
    private readonly SettingsStore _store = new SettingsStore();
    private readonly LiteLlmClient _client = new LiteLlmClient();
    internal readonly ModelCatalog Catalog = new ModelCatalog();
    internal ChatSessionStore SessionStore { get; private set; } = new ChatSessionStore(ChatSessionStore.ScopedPath(""));
    private string _sessionScope = "";
    internal UserSettings Settings { get; private set; }
    internal ChatSessionDocument Sessions { get; private set; }
    internal event EventHandler? ModelsChanged;
    internal event EventHandler? SessionsChanged;
    internal ExtensionServices(AsyncPackage package)
    {
        _package = package;
        Settings = _store.Load();
        Sessions = SessionStore.Load();
    }

    /// <summary>Switches the conversation document to the solution currently open in Visual Studio. The full solution
    /// path is used only to derive a stable local filename; it is never written into the conversation file.</summary>
    internal async Task RefreshSessionScopeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await _package.GetServiceAsync(typeof(DTE));
        var scope = dte?.Solution?.FullName ?? "";
        if (string.Equals(scope, _sessionScope, StringComparison.OrdinalIgnoreCase)) return;
        _sessionScope = scope;
        SessionStore = new ChatSessionStore(ChatSessionStore.ScopedPath(scope));
        Sessions = SessionStore.Load();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }
    internal IReadOnlyList<ModelInfo> Models => Catalog.GetAll(Settings.Servers.Where(s => s.Enabled).Select(s => s.Id)).Where(m => !Settings.DisabledModelKeys.Contains(m.Key)).ToList();
    /// <summary>The last raw round trip, for the chat's copy commands.</summary>
    internal HttpExchange? LastExchange => _client.LastExchange;
    internal void ApplyOptions()
    {
        var o = (LiteLlmOptionsPage)_package.GetDialogPage(typeof(LiteLlmOptionsPage));
        Settings.RequestTimeoutSeconds = o.RequestTimeoutSeconds; Settings.EnableStreaming = o.EnableStreaming; Settings.MaxTokens = o.MaxTokens; Settings.Temperature = o.Temperature; Settings.ShowReasoning = o.ShowReasoning; Settings.EnableToolCalling = o.EnableToolCalling; Settings.PermissionMode = o.PermissionMode; Settings.MaxToolRounds = o.MaxToolRounds; Settings.LoggingLevel = o.LoggingLevel;
        Settings.EnableFileEditing = o.EnableFileEditing; Settings.ConfirmEachEdit = o.ConfirmEachEdit; Settings.VerifyBuildAfterEdits = o.VerifyBuildAfterEdits;
        Settings.EnableTerminalTools = o.EnableTerminalTools; Settings.ConfirmEachCommand = o.ConfirmEachCommand; Settings.CommandTimeoutSeconds = o.CommandTimeoutSeconds;
        // The tests check is offered even when the build check is off; it simply never runs, because it runs
        // only against assemblies a passing build has just produced. Reading the value in either way is what
        // keeps the dialog's state and the saved state the same thing.
        Settings.VerifyTestsAfterEdits = o.VerifyTestsAfterEdits;
        Settings.ClaudeExecutable = o.ClaudeExecutable; Settings.ClaudeAllowedTools = o.ClaudeAllowedTools; Settings.ClaudeDisallowedTools = o.ClaudeDisallowedTools;
        // Miss this line and the value lives only in the options dialog: the next save writes the loaded
        // settings back over it and the setting silently reverts.
        Settings.UtilityModelKey = o.UtilityModelKey;
    }
    internal void Save() { ApplyOptions(); _store.Save(Settings); }
    internal void SaveSessions() { SessionStore.Save(Sessions); SessionsChanged?.Invoke(this, EventArgs.Empty); }
    internal ChatSessionState ActiveSession => Sessions.Sessions.First(s => s.Id == Sessions.ActiveSessionId);
    internal void ActivateSession(Guid id)
    {
        if (!Sessions.Sessions.Any(s => s.Id == id)) return;
        Sessions.ActiveSessionId = id; Sessions.Sessions.First(s => s.Id == id).UpdatedUtc = DateTime.UtcNow; SaveSessions();
    }
    internal ChatSessionState NewSession()
    {
        var session = ChatSessionStore.NewSession(); Sessions.Sessions.Insert(0, session); Sessions.ActiveSessionId = session.Id; SaveSessions(); return session;
    }
    /// <summary>Deletes a conversation and moves the active pointer off it. Saving fires <see cref="SessionsChanged"/>,
    /// so the chat's history list is rebuilt from the document rather than patched, and the pointer it lands on is the
    /// one the chat then renders.</summary>
    internal void DeleteSession(Guid id) { ChatSessionStore.Delete(Sessions, id); SaveSessions(); }
    internal async Task RefreshAsync(CancellationToken token)
    {
        foreach (var server in Settings.Servers.Where(x => x.Enabled))
            try { Log("Discovering models from " + server.Name); Catalog.Put(server.Id, await _client.GetModelsAsync(server, Settings.RequestTimeoutSeconds, token)); Log("Model discovery succeeded: " + server.Name); }
            catch (Exception ex) { Log("Model discovery failed for " + server.Name + ": " + ex.Message); }
        ModelsChanged?.Invoke(this, EventArgs.Empty); await UpdateStatusAsync();
    }
    internal async Task TestAsync(ServerConfiguration server, CancellationToken token) { Catalog.Put(server.Id, await _client.GetModelsAsync(server, Settings.RequestTimeoutSeconds, token)); ModelsChanged?.Invoke(this, EventArgs.Empty); await UpdateStatusAsync(); }
    /// <summary>The tools the agent is offered, and the instructions that go with them. Tool calling off means no tools
    /// at all; file editing off keeps the read tools but drops the writing ones, and the instructions stop promising
    /// an edit tool that is not there — which is what stops the model printing a diff and calling it done.</summary>
    internal IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            if (!Settings.EnableToolCalling) return Array.Empty<ToolDefinition>();
            IEnumerable<ToolDefinition> tools = ContextTools.Definitions;
            if (Settings.EnableFileEditing) tools = tools.Concat(EditTools.Definitions);
            if (Settings.EnableTerminalTools) tools = tools.Concat(TerminalTools.Definitions).Concat(new[] { GitTools.Definition });
            return tools.ToList();
        }
    }

    internal Task StreamAsync(ModelInfo model, IReadOnlyList<ChatMessage> messages, Func<StreamUpdate, Task> update, CancellationToken token)
    {
        var server = Settings.Servers.First(s => s.Id == model.ServerId); Log("Chat request started: " + model.Id + " on " + server.Name);
        return _client.StreamAsync(server, model.Id, messages, Settings, Tools, update, token);
    }
    internal async Task<ChatMessage> CompleteAsync(ModelInfo model, IReadOnlyList<ChatMessage> messages, CancellationToken token) => await _client.CompleteAsync(Settings.Servers.First(s => s.Id == model.ServerId), model.Id, messages, Settings, Tools, token);

    /// <summary>A call the extension makes on its own behalf rather than one the user asked for: the context
    /// summary. It carries its own settings, because a summary must not be given the session's token budget, and
    /// it is given no tools at all — there is nothing for it to look up, and offering them invites it to try.
    /// It also leaves the recorded exchange alone, so the copy-request command keeps naming the round the user
    /// is looking at.</summary>
    internal Task<ChatMessage> CompleteInternalAsync(ModelInfo model, IReadOnlyList<ChatMessage> messages, UserSettings settings, CancellationToken token) =>
        _client.CompleteAsync(Settings.Servers.First(s => s.Id == model.ServerId), model.Id, messages, settings, null, token, false);
    internal async Task<string> GetSelectionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE?)await _package.GetServiceAsync(typeof(DTE)); var selection = dte?.ActiveDocument?.Selection as TextSelection; return selection?.Text ?? "";
    }
    internal void Log(string message)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); var pane = await GetOutputPaneAsync(); pane?.OutputStringThreadSafe("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine); });
    }
    private async Task<IVsOutputWindowPane?> GetOutputPaneAsync() { var output = (IVsOutputWindow?)await _package.GetServiceAsync(typeof(SVsOutputWindow)); if (output == null) return null; var id = PackageGuids.OutputPane; output.CreatePane(ref id, "LiteLLM", 1, 1); output.GetPane(ref id, out var pane); return pane; }
    private async Task UpdateStatusAsync() { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); var bar = (IVsStatusbar?)await _package.GetServiceAsync(typeof(SVsStatusbar)); if (bar != null) { var text = Models.Count > 0 ? $"LiteLLM ✓  {Models.Count} models / {Settings.Servers.Count(s => s.Enabled)} servers" : $"LiteLLM ⚠  {Settings.Servers.Count(s => s.Enabled)} servers"; bar.SetText(text); } }
    public void Dispose() => _client.Dispose();
}
internal static class PackageGuids { internal static readonly Guid OutputPane = new Guid("0D30360D-A923-465B-94C4-760E67C28B7A"); }
