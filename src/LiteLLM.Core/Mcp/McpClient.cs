using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One line the turn's transcript owes the user. Connection failures, a list that could not be read and a
/// cap that cut something all arrive here rather than being folded into the tools the model is offered, because a
/// turn that quietly loses half its tools looks exactly like a turn with nothing to do.</summary>
public sealed class McpTurnRow
{
    public string Summary { get; set; } = "";
    public bool Ok { get; set; } = true;
    public string? Detail { get; set; }
}

/// <summary>What <see cref="McpClient.PrepareTurnAsync"/> found: the rows to draw and the untrusted instructions the
/// connected servers sent. A failed server is a row, never an exception — one unreachable server must not cost the
/// turn its other tools, let alone fail it.</summary>
public sealed class McpTurnReport
{
    public List<McpTurnRow> Rows { get; } = new List<McpTurnRow>();

    /// <summary>Server guidance, labeled and capped by the caller before it reaches a prompt.</summary>
    public string? Instructions { get; set; }
}

/// <summary>What one server is doing right now, for the Dashboard. <see cref="Error"/> is the last reason a connect
/// attempt failed and is cleared by a successful one.</summary>
public sealed class McpServerStatus
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public bool Connected { get; set; }
    public string Era { get; set; } = "";
    public string Version { get; set; } = "";
    public int Tools { get; set; }
    public string? Instructions { get; set; }
    public string? Error { get; set; }
}

/// <summary>A <c>notifications/progress</c> update forwarded from a tool call. The callback may be invoked on the
/// session's receive loop, so a UI listener has to marshal; the call itself throttles before it draws.</summary>
public sealed class McpProgressUpdate
{
    public double? Progress { get; set; }
    public double? Total { get; set; }
    public string? Message { get; set; }
}

/// <summary>The owner of every MCP connection: it decides which configured servers a turn may use, keeps their
/// sessions alive between turns, and exposes the one tool snapshot the agent loop reads.
///
/// Preparation is deliberately never fatal. A server that is misconfigured, unreachable, refusing authentication or
/// speaking a version the client cannot negotiate becomes a row, and the other servers are offered exactly as they
/// would have been without it. The snapshot is replaced as a whole, so a model call either sees the previous list or
/// the next one and never a half-updated map.
///
/// This class is in Core so the whole prepare/call path can be exercised with a fake transport; the window supplies
/// only the approval card and, later, the sampling/elicitation handlers.</summary>
public sealed class McpClient : IDisposable
{
    /// <summary>Ceiling on the guidance one server may add to the system prompt. A server controls this text.
    /// </summary>
    public const int MaxInstructionsPerServer = 2000;

    /// <summary>Ceiling on all servers' guidance together, so connecting ten chatty servers cannot crowd out the
    /// repository instructions or the Error List.</summary>
    public const int MaxInstructionsTotal = 6000;

    private readonly object _gate = new object();
    private readonly object _snapshotGate = new object();
    private readonly object _listsGate = new object();
    private readonly object _progressGate = new object();
    private readonly Dictionary<string, Connection> _connections =
        new Dictionary<string, Connection>(StringComparer.Ordinal);
    /// <summary>Bumped whenever a server is deliberately dropped. A connect that was already under way captured the
    /// value before it started, so it can tell that the decision it was made for has since been undone and must not
    /// register its session. Without this, a fast disable/enable could leave a session nobody owns.</summary>
    private readonly Dictionary<string, int> _generations =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<McpProgressUpdate>> _progress =
        new Dictionary<string, Action<McpProgressUpdate>>(StringComparer.Ordinal);
    private readonly SemaphoreSlim _prepare = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _refresh = new SemaphoreSlim(1, 1);
    private readonly McpTokenStore _tokens;
    /// <summary>Null means the built-in transports, which also need the solution root for
    /// <c>${workspaceFolder}</c>; a caller-supplied factory is left in charge of its own expansion.</summary>
    private readonly Func<McpServerConfiguration, McpLog, IMcpTransport>? _transportFactory;
    private readonly McpResultFormatter _formatter;
    private McpToolSnapshot _snapshot = McpToolSnapshot.Empty;
    private IReadOnlyList<McpResourceInfo> _resources = Array.Empty<McpResourceInfo>();
    private IReadOnlyList<McpResourceTemplate> _templates = Array.Empty<McpResourceTemplate>();
    private IReadOnlyList<McpPromptInfo> _prompts = Array.Empty<McpPromptInfo>();
    private int _maxTools = 64;
    private int _progressIds;
    private volatile bool _disposed;

    public McpClient(McpTokenStore? tokens = null,
        Func<McpServerConfiguration, McpLog, IMcpTransport>? transportFactory = null,
        McpResultFormatter? formatter = null)
    {
        _tokens = tokens ?? new McpTokenStore();
        _transportFactory = transportFactory;
        _formatter = formatter ?? new McpResultFormatter();
    }

    /// <summary>The definitions sent with the next request. Read per round, so a <c>tools/list_changed</c> that
    /// arrived between rounds is offered to the model on the next one.</summary>
    public IReadOnlyList<ToolDefinition> ToolDefinitions
    {
        get { lock (_snapshotGate) return _snapshot.Definitions; }
    }

    public IReadOnlyList<McpToolInfo> Tools
    {
        get { lock (_snapshotGate) return _snapshot.Tools; }
    }

    public McpToolSnapshot Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    public IReadOnlyList<McpResourceInfo> Resources
    {
        get { lock (_listsGate) return _resources; }
    }

    public IReadOnlyList<McpResourceTemplate> ResourceTemplates
    {
        get { lock (_listsGate) return _templates; }
    }

    public IReadOnlyList<McpPromptInfo> Prompts
    {
        get { lock (_listsGate) return _prompts; }
    }

    /// <summary>Raised after the offered tool list changed, on whatever thread changed it.</summary>
    public event EventHandler? ToolsChanged;

    /// <summary>Raised after the resource, template or prompt lists changed.</summary>
    public event EventHandler? ListsChanged;

    /// <summary>Raised for <c>notifications/resources/updated</c>.</summary>
    public event Action<McpServerConfiguration, string>? ResourceUpdated;

    /// <summary>Raised for every log entry a connected server produced, including its stderr.</summary>
    public event Action<McpServerConfiguration, McpLogEntry>? Logged;

    /// <summary>Raised when a live session ended by itself; the next turn tries to connect again.</summary>
    public event Action<McpServerConfiguration, string>? ServerClosed;

    /// <summary>Answers <c>sampling/createMessage</c>. Null means the client cannot provide one and the server is
    /// told so. The window installs a handler only when the user allowed sampling.</summary>
    public McpSamplingHandler? Sampling { get; set; }

    /// <summary>Answers <c>elicitation/create</c>. Null means every request is declined.</summary>
    public McpElicitationHandler? Elicitation { get; set; }

    /// <summary>Answers <c>roots/list</c>. Null means the client advertised no roots.</summary>
    public McpRootsHandler? Roots { get; set; }

    public bool TryResolveTool(string exposedName, out McpToolInfo tool)
    {
        lock (_snapshotGate) return _snapshot.TryResolve(exposedName ?? "", out tool);
    }

    public McpServerStatus? StatusOf(string serverId)
    {
        Connection? connection;
        lock (_gate) _connections.TryGetValue(serverId ?? "", out connection);
        return connection == null ? null : StatusOf(connection);
    }

    /// <summary>Connects every server this turn may use and rebuilds the snapshot. Sessions that are already
    /// connected with the same configuration are reused, so the cost of a second turn is the list calls only.</summary>
    public async Task<McpTurnReport> PrepareTurnAsync(UserSettings settings, string? solutionRoot,
        CancellationToken cancellationToken)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var report = new McpTurnReport();
        await _prepare.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return report;
            if (ResetWhenDisabled(settings)) return report;

            var connections = await PrepareConnectionsAsync(settings, solutionRoot, report.Rows, cancellationToken)
                .ConfigureAwait(false);

            // Installed as one step with the map it was built from: a Disconnect that lands between the build and
            // the install rebuilds its own snapshot, and the stale one must not be put back over it.
            var snapshot = InstallSnapshot(connections);
            if (!string.IsNullOrEmpty(snapshot.Note))
                report.Rows.Add(new McpTurnRow { Summary = "MCP · " + snapshot.Note, Ok = false });

            var registered = Registered(connections);
            RebuildLists(registered);
            report.Instructions = BuildInstructions(registered);
            return report;
        }
        finally
        {
            _prepare.Release();
        }
    }

    /// <summary>Connects the enabled servers that apply to the open solution and rebuilds the offered tools and
    /// lists, without the parts that belong to a turn — no instructions, no per-turn cap note. The chat window
    /// calls this when it loads, so closing and reopening Visual Studio does not mean re-enabling every server;
    /// a server that cannot be reached is returned as a row rather than failing the call.</summary>
    public async Task<McpTurnReport> ConnectEnabledAsync(UserSettings settings, string? solutionRoot,
        CancellationToken cancellationToken)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var report = new McpTurnReport();
        await _prepare.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return report;
            if (ResetWhenDisabled(settings)) return report;

            var connections = await PrepareConnectionsAsync(settings, solutionRoot, report.Rows, cancellationToken)
                .ConfigureAwait(false);
            InstallSnapshot(connections);
            RebuildLists(Registered(connections));
            return report;
        }
        finally
        {
            _prepare.Release();
        }
    }

    /// <summary>What both a turn and a window load do: drop sessions that are no longer active, then connect the
    /// active ones together and read their lists. Kept in one place so the two callers cannot drift.</summary>
    private async Task<List<Connection>> PrepareConnectionsAsync(UserSettings settings, string? solutionRoot,
        List<McpTurnRow> rows, CancellationToken cancellationToken)
    {
        var active = (settings.McpServers ?? new List<McpServerConfiguration>())
            .Where(server => server != null && server.Enabled && server.AppliesTo(solutionRoot))
            .ToList();
        DropInactive(active);
        _maxTools = Math.Max(1, settings.McpMaxToolsPerTurn);

        var connections = await ConnectAllAsync(active, settings, solutionRoot, rows, cancellationToken)
            .ConfigureAwait(false);

        foreach (var connection in connections)
        {
            var row = await LoadToolsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (row != null) rows.Add(row);
            await LoadListsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        return connections;
    }

    /// <summary>The feature switch and the server list are two different things: turning MCP off must take the
    /// live sessions down, and both preparation paths answer that the same way.</summary>
    private bool ResetWhenDisabled(UserSettings settings)
    {
        if (settings.EnableMcp) return false;
        DisconnectAll();
        SetSnapshot(McpToolSnapshot.Empty);
        SetLists(new List<McpResourceInfo>(), new List<McpResourceTemplate>(), new List<McpPromptInfo>());
        return true;
    }

    /// <summary>Builds the per-run MCP configuration the Claude CLI is launched with. Servers are authenticated the
    /// same way this client authenticates them, and nothing is connected: the CLI starts its own connections. Null
    /// means the feature is off or no enabled server is exposed, and the caller then adds no MCP arguments at all —
    /// which leaves the CLI's own configuration exactly as it was.</summary>
    public async Task<McpClaudeLease?> PrepareClaudeRunAsync(UserSettings settings, string? solutionRoot,
        CancellationToken cancellationToken)
    {
        if (settings == null || !settings.EnableMcp) return null;
        var selected = settings.EnabledMcpServers(solutionRoot)
            .Where(server => server.ExposeToClaude)
            .ToList();
        if (selected.Count == 0) return null;

        var authenticated = new List<McpServerConfiguration>(selected.Count);
        foreach (var server in selected)
            authenticated.Add(await WithAuthenticationAsync(server, cancellationToken).ConfigureAwait(false));

        // The snapshot is the only source of tool names and hints when a server has been connected. It may be empty,
        // and then the CLI allow-list is the explicit "always allow" entries only — a tool the user never approved
        // stays denied, which is the safe direction.
        var run = McpClaudeConfig.Prepare(settings, solutionRoot, authenticated, Tools);
        return run.HasServers ? McpClaudeConfig.Write(run) : null;
    }

    /// <summary>Connects one server on demand for the Dashboard's Test button. Nothing is offered to the model until
    /// the next turn, which is what keeps a manual connect from changing the request in flight.</summary>
    public async Task<McpServerStatus> ConnectAsync(McpServerConfiguration server, UserSettings settings,
        string? solutionRoot, CancellationToken cancellationToken)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        await _prepare.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return new McpServerStatus { Server = server, Error = "The MCP client is closed." };
            return await ConnectCoreAsync(server, settings, solutionRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _prepare.Release();
        }
    }

    /// <summary>Drops this server's session and starts it again. <see cref="ConnectAsync"/> deliberately reuses a
    /// live session whose connection settings have not changed, so it cannot restart a wedged server or re-read a
    /// process that has stopped answering; this is the Dashboard's Refresh, and the disconnect happens under the
    /// same gate as the connect so another round cannot reconnect it in between.</summary>
    public async Task<McpServerStatus> ReconnectAsync(McpServerConfiguration server, UserSettings settings,
        string? solutionRoot, CancellationToken cancellationToken)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        await _prepare.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return new McpServerStatus { Server = server, Error = "The MCP client is closed." };
            Disconnect(server.Id);
            return await ConnectCoreAsync(server, settings, solutionRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _prepare.Release();
        }
    }

    private async Task<McpServerStatus> ConnectCoreAsync(McpServerConfiguration server, UserSettings settings,
        string? solutionRoot, CancellationToken cancellationToken)
    {
        var rows = new List<McpTurnRow>();
        var connections = await ConnectAllAsync(new List<McpServerConfiguration> { server }, settings,
            solutionRoot, rows, cancellationToken).ConfigureAwait(false);
        var connection = connections.FirstOrDefault();
        if (connection == null)
            // No row means the server was dropped (disabled, removed, refreshed) while its handshake was running.
            // That is not a failure, but the caller still needs a sentence rather than a blank reason.
            return new McpServerStatus
            {
                Server = server,
                Error = rows.FirstOrDefault()?.Summary ?? "The connection was dropped before it was registered."
            };

        await LoadToolsAsync(connection, cancellationToken).ConfigureAwait(false);
        await LoadListsAsync(connection, cancellationToken).ConfigureAwait(false);
        RebuildSnapshotLocked();
        RebuildLists(Connections());
        return StatusOf(connection);
    }

    public void Disconnect(string serverId)
    {
        Connection? connection;
        lock (_gate)
        {
            // Bumped even when nothing is registered: a connect for this server may be in flight, and the decision
            // that produced it has just been taken back.
            _generations[serverId ?? ""] = GenerationOf(serverId) + 1;
            _connections.TryGetValue(serverId ?? "", out connection);
            if (connection != null) _connections.Remove(connection.Live.Id);
        }
        connection?.Session.Dispose();
        if (connection == null) return;
        RebuildSnapshotLocked();
        RebuildLists(Connections());
    }

    /// <summary>Whether a grant is held for this server. Sign out is only offered when there is something to
    /// forget; an unused OAuth server starts with no grant at all.</summary>
    public bool HasSignIn(McpServerConfiguration server) =>
        server != null && server.Kind != McpTransportKind.Stdio && server.UseOAuth &&
        _tokens.Get(McpTokenStore.KeyOf(server)) != null;

    /// <summary>Takes the grant the Dashboard obtained through the browser. It lives in the token store rather than
    /// in settings, so it is never written to the settings file or to an export.</summary>
    public void StoreSignIn(McpServerConfiguration server, McpOAuthToken token)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));
        if (token == null) throw new ArgumentNullException(nameof(token));
        _tokens.Set(McpTokenStore.KeyOf(server), token);
    }

    public void ForgetSignIn(McpServerConfiguration server)
    {
        if (server == null) return;
        _tokens.Remove(McpTokenStore.KeyOf(server));
    }

    private void DisconnectAll()
    {
        List<Connection> all;
        lock (_gate)
        {
            all = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var connection in all) connection.Session.Dispose();
    }

    /// <summary>Calls one tool and converts the result. The progress callback, when supplied, is invoked from the
    /// session's receive loop and can arrive after the call has returned its last update; the caller throttles and
    /// ignores late ones.</summary>
    public async Task<McpFormattedResult> CallToolAsync(McpToolInfo tool, JObject? arguments,
        Action<McpProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        var connection = Find(tool.Server.Id) ??
                         throw new McpException("MCP server '" + tool.Server.Name +
                                               "' is not connected. Try the request again.");
        if (connection.Session.IsClosed)
            throw new McpException("MCP server '" + tool.Server.Name + "' is no longer connected.");

        var parameters = new JObject
        {
            ["name"] = tool.Name,
            ["arguments"] = arguments ?? new JObject()
        };
        string? progressId = null;
        if (progress != null)
        {
            progressId = "mcp-progress-" + Interlocked.Increment(ref _progressIds)
                .ToString(CultureInfo.InvariantCulture);
            parameters["_meta"] = new JObject { ["progressToken"] = progressId };
            lock (_progressGate) _progress[progressId] = progress;
        }

        try
        {
            var result = await connection.Session
                .RequestAsync("tools/call", parameters, connection.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            return _formatter.Format(tool, result);
        }
        finally
        {
            if (progressId != null)
                lock (_progressGate) _progress.Remove(progressId);
        }
    }

    public async Task<bool> PingAsync(string serverId, CancellationToken cancellationToken)
    {
        var connection = Find(serverId);
        if (connection == null || connection.Session.IsClosed) return false;
        try
        {
            await connection.Session.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            connection.Log.Warning("Ping failed: " + ex.Message);
            return false;
        }
    }

    public Task<McpPage> ListResourcesAsync(string serverId, CancellationToken cancellationToken) =>
        SessionOf(serverId).ListAllAsync("resources/list", null, "resources",
            cancellationToken: cancellationToken);

    public Task<McpPage> ListResourceTemplatesAsync(string serverId, CancellationToken cancellationToken) =>
        SessionOf(serverId).ListAllAsync("resources/templates/list", null, "resourceTemplates",
            cancellationToken: cancellationToken);

    public Task<McpPage> ListPromptsAsync(string serverId, CancellationToken cancellationToken) =>
        SessionOf(serverId).ListAllAsync("prompts/list", null, "prompts", cancellationToken: cancellationToken);

    public Task<McpResourceRead> ReadResourceAsync(string serverId, string uri,
        CancellationToken cancellationToken) =>
        McpResources.ReadAsync(SessionOf(serverId), uri, cancellationToken);

    public Task<McpPromptResult> GetPromptAsync(string serverId, string name, JObject? arguments,
        CancellationToken cancellationToken) =>
        McpPrompts.GetAsync(SessionOf(serverId), name, arguments, cancellationToken);

    public Task<McpCompletionResult> CompleteAsync(string serverId, JObject reference, string argumentName,
        string argumentValue, JObject? context, CancellationToken cancellationToken) =>
        McpCompletion.CompleteAsync(SessionOf(serverId), reference, argumentName, argumentValue, context,
            cancellationToken);

    public McpLog? LogOf(string serverId)
    {
        Connection? connection;
        lock (_gate) _connections.TryGetValue(serverId ?? "", out connection);
        return connection?.Log;
    }

    private McpSession SessionOf(string serverId) =>
        Find(serverId)?.Session ??
        throw new McpException("MCP server '" + (serverId ?? "") + "' is not connected.");

    private Connection? Find(string? serverId)
    {
        lock (_gate)
            return serverId != null && _connections.TryGetValue(serverId, out var connection) ? connection : null;
    }

    private int GenerationOf(string? serverId)
    {
        lock (_gate)
            return serverId != null && _generations.TryGetValue(serverId, out var generation) ? generation : 0;
    }

    private List<Connection> Connections()
    {
        lock (_gate) return _connections.Values.ToList();
    }

    private void DropInactive(List<McpServerConfiguration> active)
    {
        List<Connection> removed;
        lock (_gate)
        {
            removed = _connections.Values
                // Compared against the live settings entry, not the authenticated clone: the clone carries the
                // bearer header this client hydrated from the token store, so it never matches the entry and every
                // prepare would drop a perfectly good OAuth session.
                .Where(connection => !active.Any(server =>
                    string.Equals(server.Id, connection.Live.Id, StringComparison.Ordinal) &&
                    connection.Live.SameConnectionAs(server)))
                .ToList();
            foreach (var connection in removed) _connections.Remove(connection.Live.Id);
        }
        foreach (var connection in removed) connection.Session.Dispose();
    }

    private async Task<List<Connection>> ConnectAllAsync(List<McpServerConfiguration> servers, UserSettings settings,
        string? solutionRoot, List<McpTurnRow> rows, CancellationToken cancellationToken)
    {
        // Connected together: a turn should wait for the slowest server once, not once per server. Results are read
        // back in configuration order, so the transcript and the offered names do not depend on who answered first.
        var running = servers.Select(server =>
            ConnectOneAsync(server, settings, solutionRoot, cancellationToken)).ToList();
        var results = await Task.WhenAll(running).ConfigureAwait(false);

        var connections = new List<Connection>();
        foreach (var result in results)
        {
            if (result.Connection != null) connections.Add(result.Connection);
            else if (result.Row != null) rows.Add(result.Row);
        }
        return connections;
    }

    private async Task<(Connection? Connection, McpTurnRow? Row)> ConnectOneAsync(McpServerConfiguration live,
        UserSettings settings, string? solutionRoot, CancellationToken cancellationToken)
    {
        try
        {
            var generation = GenerationOf(live.Id);
            var existing = Find(live.Id);
            // Compare the configuration the session actually connected with against a freshly authenticated one,
            // not against the settings entry: the entry carries no bearer header, so the old comparison never
            // matched and every prepare restarted a signed-in server. The comparison is against the clone because
            // a grant that was refreshed or replaced must also produce a new session. The root is part of the
            // identity because transports expand ${workspaceFolder} when they are created, and a session started
            // for another solution would keep pointing there.
            var config = await WithAuthenticationAsync(live, cancellationToken).ConfigureAwait(false);
            if (existing != null && !existing.Session.IsClosed &&
                existing.Config.SameConnectionAs(config) &&
                string.Equals(existing.Root, solutionRoot, StringComparison.OrdinalIgnoreCase))
            {
                // A Disconnect between the lookup and here would otherwise hand back a session the caller has
                // already dropped, and the row would still say it is connected.
                if (GenerationOf(live.Id) != generation) return (null, null);
                existing.Live = live;
                // The session holds this same options object, so a timeout changed in the settings takes effect on
                // the running session instead of being ignored until a reconnect.
                existing.Options.ConnectTimeout = TimeSpan.FromSeconds(Seconds(
                    live.ConnectTimeoutSeconds, settings.McpConnectTimeoutSeconds));
                existing.Options.RequestTimeout = TimeSpan.FromSeconds(Seconds(
                    live.TimeoutSeconds, settings.McpToolTimeoutSeconds));
                existing.SamplingAllowed = settings.McpAllowSampling;
                existing.ElicitationAllowed = settings.McpAllowElicitation;
                existing.RootsAllowed = settings.McpAllowRoots;
                return (existing, null);
            }
            if (existing != null) Remove(existing);

            var log = new McpLog();
            var options = new McpSessionOptions
            {
                Sampling = settings.McpAllowSampling,
                Elicitation = settings.McpAllowElicitation,
                Roots = settings.McpAllowRoots,
                ConnectTimeout = TimeSpan.FromSeconds(Seconds(
                    live.ConnectTimeoutSeconds, settings.McpConnectTimeoutSeconds)),
                RequestTimeout = TimeSpan.FromSeconds(Seconds(live.TimeoutSeconds, settings.McpToolTimeoutSeconds))
            };
            var connection = new Connection(live, config, log, options, solutionRoot)
            {
                SamplingAllowed = settings.McpAllowSampling,
                ElicitationAllowed = settings.McpAllowElicitation,
                RootsAllowed = settings.McpAllowRoots
            };
            log.Added += entry => RaiseLogged(live, entry);

            var transport = _transportFactory != null
                ? _transportFactory(config, log)
                : CreateTransport(config, log, solutionRoot);
            var session = new McpSession(config, transport, options, log);
            connection.Session = session;
            session.ServerRequest = (request, token) => AnswerServerRequestAsync(connection, request, token);
            session.Notification += (method, parameters) => OnNotification(connection, method, parameters);
            session.Closed += reason => OnClosed(connection, reason);

            try
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A transport that fails to start throws before the session's own dispose-on-failure, and that
                // process must not be left running by every failed attempt.
                session.Dispose();
                throw;
            }
            if (!AddIfCurrent(connection, generation))
            {
                // Disabled (or refreshed) while the handshake was running: the session must not outlive the choice
                // the user has already taken back, and it must not be registered where the next attempt would find it.
                // The check and the registration happen under one lock, so a Disconnect cannot slip between them.
                session.Dispose();
                return (null, null);
            }

            // Best effort, and bounded: a server without the logging capability answers method not found, which is
            // not a reason to call the connection failed — but a server that never answers must not hold the turn.
            try
            {
                using (var level = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await session.SetLogLevelAsync(settings.McpLogLevel, level.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { log.Debug("The log level could not be set: " + ex.Message); }

            return (connection, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, Failure(live, ex));
        }
    }

    /// <summary>Hydrates the bearer token the transports send. A stored grant that is near expiry is refreshed
    /// first; a grant the server has revoked is deleted so the next sign-in starts clean. A refresh that fails for
    /// any other reason keeps the token it has — the request will report the real problem.</summary>
    private async Task<McpServerConfiguration> WithAuthenticationAsync(McpServerConfiguration server,
        CancellationToken cancellationToken)
    {
        var config = server.Clone();
        if (config.Kind == McpTransportKind.Stdio || !config.UseOAuth) return config;

        var key = McpTokenStore.KeyOf(config);
        var grant = _tokens.Get(key);
        if (grant == null) return config;

        if (grant.NeedsRefresh(DateTimeOffset.UtcNow) && grant.CanRefresh)
        {
            using (var oauth = new McpOAuthClient())
            {
                try
                {
                    grant = await oauth.RefreshAsync(grant, cancellationToken).ConfigureAwait(false);
                    _tokens.Set(key, grant);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (McpException ex) when (ex.InvalidGrant)
                {
                    _tokens.Remove(key);
                    grant = null;
                }
                catch (Exception)
                {
                    // Keep what we have: an unreachable token endpoint must not turn a working session into a
                    // failed one, and the server's 401 is the authoritative answer.
                }
            }
        }

        if (grant != null && !string.IsNullOrEmpty(grant.AccessToken) &&
            !config.Headers.ContainsKey("Authorization"))
            config.Headers["Authorization"] = "Bearer " + grant.AccessToken;
        return config;
    }

    private async Task<McpTurnRow?> LoadToolsAsync(Connection connection, CancellationToken cancellationToken)
    {
        if (!Supports(connection.Session, "tools")) return null;
        try
        {
            var page = await connection.Session
                .ListAllAsync("tools/list", null, "tools", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            lock (_gate) connection.Tools = page.Items.OfType<JObject>().ToList();
            return page.Truncated
                ? new McpTurnRow
                {
                    Summary = "MCP · " + connection.Live.Name + ": " + (page.Note ?? "the tool list was shortened"),
                    Ok = false
                }
                : null;
        }
        catch (McpException ex) when (ex.Code == McpErrorCodes.MethodNotFound)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpTurnRow
            {
                Summary = "MCP · " + connection.Live.Name + " tools unavailable: " + McpLog.Redact(ex.Message),
                Ok = false
            };
        }
    }

    private async Task LoadListsAsync(Connection connection, CancellationToken cancellationToken)
    {
        if (Supports(connection.Session, "resources"))
        {
            try
            {
                var resources = await McpResources.ListAsync(connection.Session, cancellationToken)
                    .ConfigureAwait(false);
                var templates = await McpResources.ListTemplatesAsync(connection.Session, cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    connection.ResourceItems = resources.Items.OfType<JObject>().ToList();
                    connection.TemplateItems = templates.Items.OfType<JObject>().ToList();
                }
            }
            catch (McpException ex) when (ex.Code == McpErrorCodes.MethodNotFound) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                connection.Log.Debug("The resource list could not be read: " + ex.Message);
            }
        }

        if (Supports(connection.Session, "prompts"))
        {
            try
            {
                var prompts = await McpPrompts.ListAsync(connection.Session, cancellationToken).ConfigureAwait(false);
                lock (_gate) connection.PromptItems = prompts.Items.OfType<JObject>().ToList();
            }
            catch (McpException ex) when (ex.Code == McpErrorCodes.MethodNotFound) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                connection.Log.Debug("The prompt list could not be read: " + ex.Message);
            }
        }
    }

    /// <summary>Whether the server said it has a capability. Null is "it did not say", in which case the method is
    /// tried and a method-not-found answer is accepted silently.</summary>
    private static bool Supports(McpSession session, string capability)
    {
        var value = session.ServerCapabilities?[capability];
        if (value == null) return true;
        if (value.Type == JTokenType.Null) return false;
        return value.Type != JTokenType.Boolean || value.Value<bool>();
    }

    private McpToolSnapshot BuildSnapshot(List<Connection> connections)
    {
        lock (_gate)
        {
            return McpToolSnapshot.Build(connections
                .Where(connection => !connection.Session.IsClosed)
                .Select(connection => new McpToolListing
                {
                    Server = connection.Live,
                    Tools = connection.Tools.ToList()
                }), _maxTools);
        }
    }

    private void RebuildSnapshotLocked()
    {
        var connections = Connections();
        SetSnapshot(BuildSnapshot(connections));
    }

    /// <summary>Installs the snapshot for <paramref name="expected"/>, but only for those still registered at the
    /// moment of the install. A Dashboard Disconnect takes the map's own lock and rebuilds the snapshot; if it lands
    /// after this turn built its list, installing that list unchanged would put the removed server's tools back.</summary>
    private McpToolSnapshot InstallSnapshot(List<Connection> expected)
    {
        McpToolSnapshot snapshot;
        lock (_gate)
        {
            var registered = expected.Where(connection =>
                _connections.TryGetValue(connection.Live.Id, out var current) &&
                ReferenceEquals(current, connection)).ToList();
            snapshot = BuildSnapshot(registered);
            lock (_snapshotGate) _snapshot = snapshot;
        }
        try { ToolsChanged?.Invoke(this, EventArgs.Empty); } catch { }
        return snapshot;
    }

    /// <summary>The subset of the prepare's connections the map still owns. Used for the lists and the instructions:
    /// a server that was disconnected while its lists were being read must not contribute a resource or a prompt.</summary>
    private List<Connection> Registered(List<Connection> expected)
    {
        lock (_gate)
            return expected.Where(connection =>
                _connections.TryGetValue(connection.Live.Id, out var current) &&
                ReferenceEquals(current, connection)).ToList();
    }

    private void SetSnapshot(McpToolSnapshot snapshot)
    {
        lock (_snapshotGate) _snapshot = snapshot;
        try { ToolsChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private void RebuildLists(List<Connection> connections)
    {
        var resources = new List<McpResourceInfo>();
        var templates = new List<McpResourceTemplate>();
        var prompts = new List<McpPromptInfo>();
        lock (_gate)
        {
            foreach (var connection in connections)
            {
                foreach (var entry in connection.ResourceItems)
                    resources.Add(McpResourceInfo.From(connection.Live, entry));
                foreach (var entry in connection.TemplateItems)
                    templates.Add(McpResourceTemplate.From(connection.Live, entry));
                foreach (var entry in connection.PromptItems)
                    prompts.Add(McpPromptInfo.From(connection.Live, entry));
            }
        }
        SetLists(resources, templates, prompts);
    }

    private void SetLists(List<McpResourceInfo> resources, List<McpResourceTemplate> templates,
        List<McpPromptInfo> prompts)
    {
        lock (_listsGate)
        {
            _resources = resources;
            _templates = templates;
            _prompts = prompts;
        }
        try { ListsChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private void OnNotification(Connection connection, string method, JObject? parameters)
    {
        switch (method)
        {
            case "notifications/tools/list_changed":
                _ = RefreshToolsAsync(connection);
                break;

            case "notifications/resources/list_changed":
            case "notifications/prompts/list_changed":
                _ = RefreshListsAsync(connection);
                break;

            case "notifications/resources/updated":
                try { ResourceUpdated?.Invoke(connection.Live, parameters?["uri"]?.Value<string>() ?? ""); }
                catch { }
                break;

            case "notifications/message":
                connection.Log.Add(parameters?["level"]?.Value<string>() ?? "info",
                    parameters?["data"]?.ToString() ?? "");
                break;

            case "notifications/progress":
                RouteProgress(parameters);
                break;
        }
    }

    /// <summary>A list change is refreshed in the background. It must not be awaited on the receive loop that
    /// reported it: the refresh sends a request on the very session whose loop is calling this.</summary>
    private async Task RefreshToolsAsync(Connection connection)
    {
        if (_disposed) return;
        try
        {
            await _refresh.WaitAsync().ConfigureAwait(false);
            try
            {
                if (connection.Session.IsClosed) return;
                var page = await connection.Session
                    .ListAllAsync("tools/list", null, "tools", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                int count;
                lock (_gate)
                {
                    connection.Tools = page.Items.OfType<JObject>().ToList();
                    count = connection.Tools.Count;
                }
                connection.Log.Info("The server reported a changed tool list; " + count + " tools are now offered.");
                RebuildSnapshotLocked();
            }
            finally { _refresh.Release(); }
        }
        catch (Exception ex)
        {
            connection.Log.Warning("The changed tool list could not be read: " + ex.Message);
        }
    }

    private async Task RefreshListsAsync(Connection connection)
    {
        if (_disposed) return;
        try
        {
            await _refresh.WaitAsync().ConfigureAwait(false);
            try
            {
                if (connection.Session.IsClosed) return;
                await LoadListsAsync(connection, CancellationToken.None).ConfigureAwait(false);
                RebuildLists(Connections());
            }
            finally { _refresh.Release(); }
        }
        catch (Exception ex)
        {
            connection.Log.Warning("The changed list could not be read: " + ex.Message);
        }
    }

    private void RouteProgress(JObject? parameters)
    {
        var id = parameters?["progressToken"]?.ToString();
        if (string.IsNullOrEmpty(id)) return;
        Action<McpProgressUpdate>? handler;
        lock (_progressGate) _progress.TryGetValue(id!, out handler);
        if (handler == null) return;

        var update = new McpProgressUpdate
        {
            Progress = NumberOf(parameters?["progress"]),
            Total = NumberOf(parameters?["total"]),
            Message = parameters?["message"]?.Type == JTokenType.String ? parameters.Value<string>("message") : null
        };
        try { handler(update); } catch { }
    }

    private static double? NumberOf(JToken? token) =>
        token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            ? token.Value<double>()
            : (double?)null;

    private async Task<JObject?> AnswerServerRequestAsync(Connection connection, JObject request,
        CancellationToken cancellationToken)
    {
        var method = McpJsonRpc.MethodOf(request) ?? "";
        var parameters = request["params"] as JObject ?? new JObject();
        switch (method)
        {
            case "sampling/createMessage":
                return connection.SamplingAllowed && Sampling != null
                    ? await Sampling(connection.Live, parameters, cancellationToken).ConfigureAwait(false)
                    : null;

            case "elicitation/create":
                return connection.ElicitationAllowed && Elicitation != null
                    ? await Elicitation(connection.Live, parameters, cancellationToken).ConfigureAwait(false)
                    : DeclineElicitation();

            case "roots/list":
                return connection.RootsAllowed && Roots != null
                    ? await Roots(connection.Live, cancellationToken).ConfigureAwait(false)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>A decline is the protocol's own answer and is better than a made-up acceptance: the server can
    /// continue with what it has, where "not supported" leaves it stuck.</summary>
    private static JObject DeclineElicitation() => new JObject { ["action"] = "decline" };

    private void OnClosed(Connection connection, string reason)
    {
        if (_disposed) return;
        bool removed;
        lock (_gate)
        {
            // Only the session that is still registered may take the registration away. A session dropped on purpose
            // closes after the map no longer holds it — and by then the map may hold its replacement, which removing
            // by id alone would evict: the process would keep running while the window said "not connected".
            removed = _connections.TryGetValue(connection.Live.Id, out var current) && ReferenceEquals(current, connection);
            if (removed) _connections.Remove(connection.Live.Id);
        }
        if (!removed) return;

        // Nothing else will dispose this session: a close that arrived by itself takes it out of the map, and the
        // transport behind it — an HttpClient, an SSE stream, a stdio process — would otherwise stay alive for the
        // lifetime of the extension. A deliberate Disconnect removed it first, so it never reaches this line.
        try { connection.Session.Dispose(); } catch { }

        connection.Log.Warning("The connection ended: " + reason);
        RebuildSnapshotLocked();
        RebuildLists(Connections());
        try { ServerClosed?.Invoke(connection.Live, reason); } catch { }
    }

    private void RaiseLogged(McpServerConfiguration server, McpLogEntry entry)
    {
        try { Logged?.Invoke(server, entry); } catch { }
    }

    /// <summary>Registers a finished connection only if its server's generation has not moved since the connect
    /// started. A <see cref="Disconnect"/> bumps the generation under the same lock, so checking and recording
    /// together leaves no window where a dropped server's session could still be registered.</summary>
    private bool AddIfCurrent(Connection connection, int generation)
    {
        lock (_gate)
        {
            var current = _generations.TryGetValue(connection.Live.Id, out var value) ? value : 0;
            if (_disposed || current != generation) return false;
            _connections[connection.Live.Id] = connection;
            return true;
        }
    }

    private void Remove(Connection connection)
    {
        lock (_gate) _connections.Remove(connection.Live.Id);
        connection.Session.Dispose();
    }

    private static McpTurnRow Failure(McpServerConfiguration server, Exception exception)
    {
        var reason = exception is McpException mcp && mcp.AuthRequired
            ? "authentication required — sign in from the MCP dashboard"
            : McpLog.Redact(exception.Message);
        if (reason.Length == 0) reason = exception.GetType().Name;
        return new McpTurnRow
        {
            Summary = "MCP · " + server.Name + " unavailable: " + reason,
            Ok = false
        };
    }

    private static int Seconds(int specific, int global) => Math.Max(1, specific > 0 ? specific : global);

    private static string? BuildInstructions(IEnumerable<Connection> connections)
    {
        var text = new StringBuilder();
        foreach (var connection in connections)
        {
            var instructions = connection.Session.Instructions;
            if (string.IsNullOrWhiteSpace(instructions)) continue;
            var trimmed = instructions!.Trim();
            if (trimmed.Length > MaxInstructionsPerServer)
                trimmed = trimmed.Substring(0, MaxInstructionsPerServer) + "…";
            var entry = "[" + connection.Live.Name + "]\n" + trimmed;
            if (text.Length + entry.Length + 2 > MaxInstructionsTotal) break;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(entry);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static IMcpTransport CreateTransport(McpServerConfiguration server, McpLog log, string? solutionRoot)
    {
        switch (server.Kind)
        {
            case McpTransportKind.Stdio:
                return new McpStdioTransport(server, solutionRoot, log.Warning);
            case McpTransportKind.Sse:
                return new McpSseTransport(server, solutionRoot, null, log);
            default:
                return new McpHttpTransport(server, solutionRoot, null, log);
        }
    }

    private McpServerStatus StatusOf(Connection connection) => new McpServerStatus
    {
        Server = connection.Live,
        Connected = !connection.Session.IsClosed,
        Era = connection.Session.Era == McpEra.Modern ? "modern" : "legacy",
        Version = connection.Session.NegotiatedVersion,
        Tools = connection.Tools.Count,
        Instructions = connection.Session.Instructions
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _prepare.Dispose();
        _refresh.Dispose();
    }

    /// <summary>One server's live state. The configuration used to connect is kept separately from the entry in
    /// settings: a later edit that does not change the connection must not force a reconnect, and the entry the
    /// tool's policy reads is the live one so "always allow" persists to the object that is saved.</summary>
    private sealed class Connection
    {
        public Connection(McpServerConfiguration live, McpServerConfiguration config, McpLog log,
            McpSessionOptions options, string? root)
        {
            Live = live;
            Config = config;
            Log = log;
            Options = options;
            Root = root;
        }

        public McpServerConfiguration Live { get; set; }
        public McpServerConfiguration Config { get; }
        /// <summary>The solution root the transport was created for, the one ${workspaceFolder} was expanded with.</summary>
        public string? Root { get; }
        public McpLog Log { get; }
        public McpSessionOptions Options { get; }
        public McpSession Session { get; set; } = null!;
        public TimeSpan RequestTimeout => Options.RequestTimeout;
        public bool SamplingAllowed { get; set; }
        public bool ElicitationAllowed { get; set; }
        public bool RootsAllowed { get; set; }
        public List<JObject> Tools { get; set; } = new List<JObject>();
        public List<JObject> ResourceItems { get; set; } = new List<JObject>();
        public List<JObject> TemplateItems { get; set; } = new List<JObject>();
        public List<JObject> PromptItems { get; set; } = new List<JObject>();
    }
}
