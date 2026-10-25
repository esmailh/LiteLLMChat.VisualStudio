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
    /// <summary>What each model has cost across every conversation on this machine. Global rather than scoped
    /// with the conversations: a model is configured once for the whole IDE, so a total that reset when the user
    /// opened a different solution would answer a different question than "how much has this model used".</summary>
    internal UsageLedger Usage { get; }
    /// <summary>Every MCP server's connection and the tool list offered with a request. Owned here because the
    /// connections outlive a turn: a server discovered by one message is reused by the next.</summary>
    internal McpClient Mcp { get; }
    /// <summary>The discovered skills. Owned here because the list is shared by the chat's menu, the Dashboard and
    /// every turn, and its cache is only useful if those three read the same instance.</summary>
    internal SkillRegistry Skills { get; } = new SkillRegistry();
    internal event EventHandler? ModelsChanged;
    internal event EventHandler? SessionsChanged;
    internal event EventHandler? UsageChanged;
    /// <summary>Raised after a save, because the window's MCP chip is drawn from the settings and a server added,
    /// turned off or imported in the Dashboard has to change it without waiting for the next turn.</summary>
    internal event EventHandler? SettingsChanged;
    internal ExtensionServices(AsyncPackage package)
    {
        _package = package;
        Settings = _store.Load();
        NormalizeSkillSettings(Settings);
        Sessions = SessionStore.Load();
        Usage = new UsageLedger();
        Mcp = new McpClient();
        // The dashboard's log panel keeps the full ring; the output pane gets the entries that describe a problem,
        // so a server that fails while the user is not looking still leaves a trace where they already look.
        Mcp.Logged += (server, entry) =>
        {
            if (entry.Level == "error" || entry.Level == "warning")
                Log("MCP · " + server.Name + ": " + entry.Message);
        };
        // A run file holds resolved secrets, so one left behind by a crash must not live forever. Sweeping at startup
        // is enough: a file is normally deleted in the Claude turn's finally.
        try
        {
            var swept = McpClaudeConfig.SweepStale(TimeSpan.FromDays(1));
            if (swept > 0) Log("Removed " + swept + " stale MCP configuration file(s).");
        }
        catch (Exception ex) { Log("Stale MCP configuration files could not be swept: " + ex.Message); }
        // Seeded once from what is already on disk: a conversation written before the ledger existed still kept
        // its turns' usage, and a new build that showed zero would look as if those turns had been free.
        if (Usage.Seed(ChatSessionStore.LoadAllSaved().SelectMany(d => d.Sessions)))
        {
            try { Usage.Save(); }
            catch (Exception ex) { Log("Token usage ledger could not be written: " + ex.Message); }
        }
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
    /// <summary>The last call's token counts, or null when that server did not report any.</summary>
    internal TokenUsage? LastUsage => _client.LastUsage;
    internal void ApplyOptions()
    {
        var o = (LiteLlmOptionsPage)_package.GetDialogPage(typeof(LiteLlmOptionsPage));
        Settings.RequestTimeoutSeconds = o.RequestTimeoutSeconds; Settings.StreamResumeAttempts = o.StreamResumeAttempts; Settings.EnableStreaming = o.EnableStreaming; Settings.MaxTokens = o.MaxTokens; Settings.Temperature = o.Temperature; Settings.ShowReasoning = o.ShowReasoning; Settings.EnableToolCalling = o.EnableToolCalling; Settings.PermissionMode = o.PermissionMode; Settings.MaxToolRounds = o.MaxToolRounds; Settings.LoggingLevel = o.LoggingLevel;
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
        // The global MCP switches only. Settings.McpServers is written by the Dashboard and deliberately left
        // alone here: applying options must not replace the server list with an empty one.
        Settings.EnableMcp = o.EnableMcp; Settings.McpAutoApproveReadOnly = o.McpAutoApproveReadOnly;
        Settings.McpAllowSampling = o.McpAllowSampling; Settings.McpAllowElicitation = o.McpAllowElicitation;
        Settings.McpAllowRoots = o.McpAllowRoots; Settings.McpConnectTimeoutSeconds = o.McpConnectTimeoutSeconds;
        Settings.McpToolTimeoutSeconds = o.McpToolTimeoutSeconds; Settings.McpMaxToolsPerTurn = o.McpMaxToolsPerTurn;
        Settings.McpLogLevel = o.McpLogLevel; Settings.McpStrictClaudeConfig = o.McpStrictClaudeConfig;
    }
    internal void Save() { ApplyOptions(); _store.Save(Settings); SettingsChanged?.Invoke(this, EventArgs.Empty); }
    internal void SaveSessions() { SessionStore.Save(Sessions); SessionsChanged?.Invoke(this, EventArgs.Empty); }

    private static void NormalizeSkillSettings(UserSettings settings)
    {
        settings.DisabledSkillPaths = (settings.DisabledSkillPaths ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.SkillsPersonalDirectory ??= "";
        if (string.IsNullOrWhiteSpace(settings.SkillsProjectDirectory))
            settings.SkillsProjectDirectory = SkillLocations.DefaultProjectDirectory;
    }

    internal SkillDiscoveryOptions SkillOptions(string? solutionRoot) =>
        SkillDiscoveryOptions.FromSettings(Settings, solutionRoot);

    /// <summary>Scans the configured directories. The registry caches by a signature of every SKILL.md's path,
    /// length and last-write time, so calling this before a turn or a menu is cheap when nothing changed.</summary>
    internal SkillRegistrySnapshot PrepareSkills(string? solutionRoot, CancellationToken token = default) =>
        Skills.Refresh(SkillOptions(solutionRoot), token);

    internal Task<SkillRegistrySnapshot> PrepareSkillsAsync(string? solutionRoot, CancellationToken token = default) =>
        Skills.RefreshAsync(SkillOptions(solutionRoot), token);
    /// <summary>Adds a finished turn's server-reported usage to the per-model ledger. A failed write is logged and
    /// not thrown: the turn is over, its transcript row is already drawn, and losing the receipt for one turn must
    /// not become a failed conversation. The event is raised either way, because the in-memory total did change.</summary>
    internal void RecordUsage(string modelKey, TokenUsage? usage)
    {
        if (usage == null || !usage.Any) return;
        Usage.Record(modelKey, usage, DateTime.UtcNow);
        try { Usage.Save(); }
        catch (Exception ex) { Log("Token usage ledger could not be written: " + ex.Message); }
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }
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
    internal IReadOnlyList<ToolDefinition> Tools => ToolsFor(null);

    /// <param name="mcpTools">Null offers the live snapshot, which is what a turn that selected nothing gets. A
    /// non-null list — including an empty one — is offered instead, so a narrowed turn sends exactly the tools the
    /// user selected and nothing else. The built-in tools are never narrowed.</param>
    internal IReadOnlyList<ToolDefinition> ToolsFor(IReadOnlyList<ToolDefinition>? mcpTools, bool skillTools = false)
    {
        if (!Settings.EnableToolCalling) return Array.Empty<ToolDefinition>();
        IEnumerable<ToolDefinition> tools = ContextTools.Definitions;
        if (Settings.EnableFileEditing) tools = tools.Concat(EditTools.Definitions);
        if (Settings.EnableTerminalTools) tools = tools.Concat(TerminalTools.Definitions).Concat(new[] { GitTools.Definition });
        // Read per request from the snapshot the turn prepared: a tools/list_changed that arrives between
        // rounds is offered on the next one, and the reverse map dispatch uses is replaced with it. A narrowed
        // turn pins its own list instead, because the selection was resolved against the snapshot it prepared.
        if (Settings.EnableMcp) tools = tools.Concat(mcpTools ?? Mcp.ToolDefinitions);
        // Only a turn that prepared a skill plan gets the two skill tools. They are not part of the always-on
        // registry: with skills off, or with no skill discovered, offering load_skill would describe a door with
        // nothing behind it.
        if (Settings.EnableSkills && skillTools) tools = tools.Concat(SkillTools.Definitions);
        return tools.ToList();
    }

    internal Task StreamAsync(ModelInfo model, IReadOnlyList<ChatMessage> messages, Func<StreamUpdate, Task> update, CancellationToken token, IReadOnlyList<ToolDefinition>? mcpTools = null, bool skillTools = false)
    {
        var server = Settings.Servers.First(s => s.Id == model.ServerId); Log("Chat request started: " + model.Id + " on " + server.Name);
        return _client.StreamAsync(server, model.Id, messages, Settings, ToolsFor(mcpTools, skillTools), update, token);
    }
    internal async Task<ChatMessage> CompleteAsync(ModelInfo model, IReadOnlyList<ChatMessage> messages, CancellationToken token, IReadOnlyList<ToolDefinition>? mcpTools = null, bool skillTools = false) => await _client.CompleteAsync(Settings.Servers.First(s => s.Id == model.ServerId), model.Id, messages, Settings, ToolsFor(mcpTools, skillTools), token);

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
    public void Dispose() { Mcp.Dispose(); _client.Dispose(); }
}
internal static class PackageGuids { internal static readonly Guid OutputPane = new Guid("0D30360D-A923-465B-94C4-760E67C28B7A"); }
