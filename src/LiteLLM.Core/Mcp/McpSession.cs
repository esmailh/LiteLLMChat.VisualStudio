using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class McpSessionOptions
{
    /// <summary>Whether the client advertises <c>roots</c>. Off means the server is never offered the workspace.</summary>
    public bool Roots { get; set; } = true;
    public bool Sampling { get; set; }
    public bool Elicitation { get; set; } = true;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
}

/// <summary>One server's connection: handshake, request correlation, notifications and shutdown.
///
/// Every request gets its own id and its own completion, because responses may return in any order and the channel
/// carries messages the client never asked for. A server request — sampling, elicitation, a roots listing — arrives
/// on that same channel, so it is answered from here rather than from the caller's request path. Nothing in this
/// class touches the UI; a handler that needs a card is supplied by the caller through
/// <see cref="ServerRequest"/> and runs off the receive loop.
///
/// Two protocol eras share the class. The session-oriented one starts with <c>initialize</c> and keeps a connection;
/// the stateless 2026-07-28 one starts with <c>server/discover</c> and carries the version, client identity and
/// capabilities in each request's <c>_meta</c>. Which one a server speaks is probed once, here, because the rest of
/// the client only needs to know that <see cref="Era"/> is one or the other.</summary>
public sealed class McpSession : IDisposable
{
    private const int MaxInputRequiredRounds = 5;

    private readonly McpServerConfiguration _server;
    private readonly IMcpTransport _transport;
    private readonly McpSessionOptions _options;
    private readonly McpLog _log;
    private readonly JObject _clientCapabilities;
    private readonly object _gate = new object();
    private readonly Dictionary<string, TaskCompletionSource<JObject>> _pending =
        new Dictionary<string, TaskCompletionSource<JObject>>(StringComparer.Ordinal);
    private readonly Dictionary<string, McpSubscription> _subscriptions =
        new Dictionary<string, McpSubscription>(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private int _nextId;
    private bool _closed;

    public McpSession(McpServerConfiguration server, IMcpTransport transport,
        McpSessionOptions? options = null, McpLog? log = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new McpSessionOptions();
        _log = log ?? new McpLog();
        _clientCapabilities = McpProtocolVersions.Capabilities(_options.Roots, _options.Sampling, _options.Elicitation);
    }

    public McpServerConfiguration Server => _server;
    public McpLog Log => _log;
    public McpEra Era { get; private set; } = McpEra.Legacy;
    public string NegotiatedVersion { get; private set; } = "";
    public JObject ServerCapabilities { get; private set; } = new JObject();
    public JObject ServerInfo { get; private set; } = new JObject();

    /// <summary>The server's own guidance, as sent. Untrusted text: the caller labels and caps it before it reaches
    /// a prompt.</summary>
    public string? Instructions { get; private set; }

    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Raised for every notification. The handler runs on the receive loop, so it must not wait for the
    /// loop itself to do something.</summary>
    public event Action<string, JObject?>? Notification;

    /// <summary>Raised once, with the reason the channel ended.</summary>
    public event Action<string>? Closed;

    /// <summary>Answers a server-initiated request. Return null to tell the server the method is not supported.
    /// The delegate runs on the thread pool, not on the receive loop. During a modern call it is also the handler
    /// for the requests inside <c>resultType: "input_required"</c>.</summary>
    public Func<JObject, CancellationToken, Task<JObject?>>? ServerRequest { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var problem = _server.ValidationError();
        if (problem != null) throw new McpException(problem);

        // The whole handshake is bounded, not only the wait for a response: a transport can block while starting
        // (an HTTP or SSE endpoint that accepts the request and then says nothing), and a connection attempt that
        // never ends would hold the client's prepare gate and with it every later turn.
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (_options.ConnectTimeout > TimeSpan.Zero && _options.ConnectTimeout != Timeout.InfiniteTimeSpan)
                limit.CancelAfter(_options.ConnectTimeout);
            try
            {
                await _transport.StartAsync(limit.Token).ConfigureAwait(false);
                _ = Task.Run(() => ReceiveLoopAsync());
                await HandshakeAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Dispose();
                throw new McpException("MCP server '" + _server.Name + "' did not connect within " +
                                       Describe(_options.ConnectTimeout) + ".");
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    /// <summary>Picks the era. <c>auto</c> probes with <c>server/discover</c> and falls back to <c>initialize</c>
    /// when the server does not understand it; an explicit override skips the probe or forces one, because a server
    /// that answers a probe badly still has to be reachable.</summary>
    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var configured = (_server.ProtocolVersion ?? "auto").Trim();
        // An explicit legacy version means the user has already decided; only auto and modern probe.
        var probe = configured.Length == 0 ||
                    configured.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                    configured.Equals("modern", StringComparison.OrdinalIgnoreCase);

        if (probe)
        {
            try
            {
                await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Info("The server/discover probe timed out; trying the initialize handshake.");
            }
            catch (McpException ex)
            {
                _log.Info("server/discover was not accepted (" + ex.Message + "); trying the initialize handshake.");
            }
        }

        await InitializeAsync(OfferedVersion(configured), cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var parameters = new JObject
        {
            ["_meta"] = McpProtocolVersions.Meta(_clientCapabilities, McpProtocolVersions.Modern)
        };
        var result = await SendRequestAsync("server/discover", parameters, _options.ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        NegotiatedVersion = McpProtocolVersions.Negotiate(ChooseVersion(result["supportedVersions"] as JArray));
        // The probe itself only exists in the stateless revision, so the server is modern whatever its list says.
        if (!McpProtocolVersions.IsModern(NegotiatedVersion)) NegotiatedVersion = McpProtocolVersions.Modern;
        Era = McpEra.Modern;
        ServerCapabilities = result["capabilities"] as JObject ?? new JObject();
        ServerInfo = (result["_meta"] as JObject)?["io.modelcontextprotocol/serverInfo"] as JObject
                     ?? result["serverInfo"] as JObject
                     ?? new JObject();
        Instructions = result["instructions"]?.Type == JTokenType.String
            ? result.Value<string>("instructions")
            : null;
        _transport.SetSession(null, NegotiatedVersion);
        _log.Info("Connected using protocol " + NegotiatedVersion + " (stateless).");
        // No notifications/initialized: the revision has no handshake to complete.
    }

    private async Task InitializeAsync(string offered, CancellationToken cancellationToken)
    {
        var parameters = new JObject
        {
            ["protocolVersion"] = offered,
            ["capabilities"] = _clientCapabilities.DeepClone(),
            ["clientInfo"] = McpProtocolVersions.ClientInfo
        };

        var result = await SendRequestAsync("initialize", parameters, _options.ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        NegotiatedVersion = McpProtocolVersions.Negotiate(result["protocolVersion"]?.ToString());
        Era = McpProtocolVersions.EraOf(NegotiatedVersion);
        ServerCapabilities = result["capabilities"] as JObject ?? new JObject();
        ServerInfo = result["serverInfo"] as JObject ?? new JObject();
        Instructions = result["instructions"]?.Type == JTokenType.String
            ? result.Value<string>("instructions")
            : null;
        _transport.SetSession(null, NegotiatedVersion);
        _log.Info("Connected using protocol " + NegotiatedVersion + ".");

        // Required by the session-oriented revisions: a server may hold back its first results until this arrives.
        // Sent without _meta even when the server counter-offered a modern version, because it was a legacy
        // handshake that asked for it.
        await _transport.SendAsync(McpJsonRpc.Notification("notifications/initialized"), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>What to offer in <c>initialize</c>. A configured known version is offered as-is; everything else
    /// offers this client's current legacy version.</summary>
    private static string OfferedVersion(string configured)
    {
        if (configured.Length == 0 || configured.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("modern", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("legacy", StringComparison.OrdinalIgnoreCase))
            return McpProtocolVersions.Legacy;
        foreach (var known in McpProtocolVersions.Known)
            if (known.Equals(configured, StringComparison.Ordinal)) return known;
        return McpProtocolVersions.Legacy;
    }

    private static string? ChooseVersion(JArray? supported)
    {
        if (supported == null) return null;
        var versions = supported.Where(token => token.Type == JTokenType.String)
            .Select(token => token.Value<string>()!)
            .ToList();
        if (versions.Count == 0) return null;
        if (versions.Contains(McpProtocolVersions.Modern)) return McpProtocolVersions.Modern;
        foreach (var known in McpProtocolVersions.Known)
            if (versions.Contains(known)) return known;
        return versions[0];
    }

    public Task<JObject> RequestAsync(string method, JObject? parameters, CancellationToken cancellationToken) =>
        RequestAsync(method, parameters, _options.RequestTimeout, cancellationToken);

    /// <summary>Sends a request and returns its <c>result</c>. In the stateless era a result may ask for client
    /// input instead; the request is then retried with a new id, carrying the answers and the server's opaque state.
    /// The loop is bounded: a server that keeps asking fails the call rather than holding the turn.</summary>
    public async Task<JObject> RequestAsync(string method, JObject? parameters, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var prepared = WithMeta(parameters);
        if (Era != McpEra.Modern || !McpProtocolVersions.SupportsInputRequired(method))
            return await SendRequestAsync(method, prepared, timeout, cancellationToken).ConfigureAwait(false);

        for (var round = 0; ; round++)
        {
            var result = await SendRequestAsync(method, prepared, timeout, cancellationToken).ConfigureAwait(false);
            if (!IsInputRequired(result)) return result;
            if (round >= MaxInputRequiredRounds)
                throw new McpException("MCP server '" + _server.Name + "' kept asking for more input during '" +
                                       method + "' after " + (round + 1) + " rounds.");
            prepared = await RetryWithInputAsync(method, prepared, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JObject> SendRequestAsync(string method, JObject? parameters, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = new JValue(Interlocked.Increment(ref _nextId));
        var key = McpJsonRpc.IdKey(id);
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed) throw new McpException("MCP server '" + _server.Name + "' is not connected.");
            _pending[key] = completion;
        }

        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (linked.Token.Register(() => completion.TrySetCanceled()))
        {
            if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
            JObject response;
            try
            {
                // The send is inside the same timeout as the wait. A server that accepts the POST but never answers
                // would otherwise leave HttpContent.SendAsync waiting forever, and the tool call with it.
                await _transport.SendAsync(McpJsonRpc.Request(id, method, parameters), linked.Token)
                    .ConfigureAwait(false);
                response = await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RemovePending(key);
                var timedOut = !cancellationToken.IsCancellationRequested;
                // Telling the server is best-effort: the turn is already moving on, and a server that does not
                // understand the notification must not turn a timeout into a second failure.
                await TryCancelAsync(id, timedOut ? "timeout" : "cancelled").ConfigureAwait(false);
                if (timedOut)
                    throw new McpException("MCP server '" + _server.Name + "' did not answer '" + method + "' within " +
                                           Describe(timeout) + ".");
                throw;
            }
            catch
            {
                RemovePending(key);
                throw;
            }

            // The caller wants the result, not the envelope; an error envelope becomes an exception here so no
            // caller has to remember to check for one.
            return McpJsonRpc.ResultOf(response) as JObject ?? new JObject();
        }
    }

    private static bool IsInputRequired(JObject result) =>
        result["resultType"]?.Type == JTokenType.String &&
        result.Value<string>("resultType") == "input_required";

    /// <summary>Answers every request the server attached to an <c>input_required</c> result and builds the retry's
    /// parameters. A request the client cannot answer fails the call: sending a made-up response would be worse
    /// than saying no, and the server cannot continue without a real one.</summary>
    private async Task<JObject> RetryWithInputAsync(string method, JObject? original, JObject result,
        CancellationToken cancellationToken)
    {
        var requests = result["inputRequests"] as JObject;
        JObject? answers = null;
        if (requests != null)
        {
            answers = new JObject();
            foreach (var property in requests.Properties())
            {
                var nested = property.Value as JObject;
                var requested = nested?["method"]?.Type == JTokenType.String
                    ? nested.Value<string>("method")!
                    : "an unnamed request";
                var handler = ServerRequest ?? throw new McpException(
                    "MCP server '" + _server.Name + "' asked for '" + requested + "' during '" + method +
                    "', but the client has no handler for it.", McpErrorCodes.MethodNotFound);

                var answer = await handler(new JObject
                {
                    ["method"] = requested,
                    ["params"] = nested?["params"] as JObject ?? new JObject()
                }, cancellationToken).ConfigureAwait(false);

                if (answer == null)
                    throw new McpException("MCP server '" + _server.Name + "' asked for '" + requested + "' during '" +
                                           method + "', which this client cannot provide.",
                        McpErrorCodes.MethodNotFound);
                answers[property.Name] = answer;
            }
        }

        var next = original?.DeepClone() as JObject ?? new JObject();
        if (answers != null) next["inputResponses"] = answers;
        if (result["requestState"] != null) next["requestState"] = result["requestState"]!.DeepClone();
        return WithMeta(next)!;
    }

    /// <summary>Adds or refreshes the per-request metadata of the stateless era. The required keys always win over
    /// whatever the caller put in <c>_meta</c>, and the caller's object is copied rather than modified because it
    /// may be the one a retry is built from.</summary>
    private JObject? WithMeta(JObject? parameters)
    {
        if (Era != McpEra.Modern) return parameters;
        var copy = parameters?.DeepClone() as JObject ?? new JObject();
        if (!(copy["_meta"] is JObject meta))
        {
            meta = new JObject();
            copy["_meta"] = meta;
        }
        foreach (var pair in McpProtocolVersions.Meta(_clientCapabilities, NegotiatedVersion))
            meta[pair.Key] = pair.Value;
        return copy;
    }

    public Task NotifyAsync(string method, JObject? parameters, CancellationToken cancellationToken) =>
        _transport.SendAsync(McpJsonRpc.Notification(method, WithMeta(parameters)), cancellationToken);

    /// <summary>A liveness check. <c>ping</c> was removed in 2026-07-28, where the stateless equivalent is the
    /// discover call the connection itself used.</summary>
    public Task<JObject> PingAsync(CancellationToken cancellationToken) =>
        Era == McpEra.Modern
            ? RequestAsync("server/discover", null, TimeSpan.FromSeconds(10), cancellationToken)
            : RequestAsync("ping", null, TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>Legacy-only: 2026-07-28 removed <c>logging/setLevel</c>. Callers can ask unconditionally; a modern
    /// session simply has nothing to send.</summary>
    public async Task SetLogLevelAsync(string level, CancellationToken cancellationToken)
    {
        if (Era == McpEra.Modern) return;
        await RequestAsync("logging/setLevel", new JObject { ["level"] = level }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads every page of a list method. The caps are reported through <see cref="McpPage"/>, never
    /// applied silently.</summary>
    public async Task<McpPage> ListAllAsync(string method, JObject? parameters, string itemsProperty,
        int maxItems = McpPagination.DefaultMaxItems, int maxPages = McpPagination.DefaultMaxPages,
        CancellationToken cancellationToken = default)
    {
        var first = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return await McpPagination.CollectAsync(first, itemsProperty, (cursor, token) =>
                RequestAsync(method, WithCursor(parameters, cursor), token),
            maxItems, maxPages, cancellationToken).ConfigureAwait(false);
    }

    private static JObject WithCursor(JObject? parameters, string cursor)
    {
        var copy = parameters?.DeepClone() as JObject ?? new JObject();
        copy["cursor"] = cursor;
        return copy;
    }

    /// <summary>Opens a <c>subscriptions/listen</c> stream. Only the stateless era defines it. The handle's
    /// <see cref="McpSubscription.Id"/> is the request id the server tags every message with.</summary>
    public async Task<McpSubscription> ListenAsync(JObject? notifications, CancellationToken cancellationToken)
    {
        if (Era != McpEra.Modern)
            throw new McpException("MCP server '" + _server.Name +
                                   "' was connected with the session protocol, which has no subscriptions/listen.");

        var id = new JValue(Interlocked.Increment(ref _nextId));
        var key = McpJsonRpc.IdKey(id);
        var raw = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        McpSubscription subscription;
        lock (_gate)
        {
            if (_closed) throw new McpException("MCP server '" + _server.Name + "' is not connected.");
            subscription = new McpSubscription(id, key, raw, CancelSubscription);
            _pending[key] = raw;
            _subscriptions[key] = subscription;
        }

        var parameters = WithMeta(new JObject { ["notifications"] = notifications ?? new JObject() });
        try
        {
            await _transport.SendAsync(McpJsonRpc.Request(id, "subscriptions/listen", parameters), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _pending.Remove(key);
                _subscriptions.Remove(key);
            }
            subscription.Cancelled();
            throw;
        }
        return subscription;
    }

    private void CancelSubscription(McpSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription.IdKey);
            _pending.Remove(subscription.IdKey);
        }
        subscription.Cancelled();

        // On HTTP the revision defines closing the stream as the cancellation; a transport that cannot do that gets
        // the notification instead.
        if (_transport is IMcpStreamControl control)
        {
            try { control.CancelStream(subscription.Id); } catch { }
            return;
        }
        _ = TryCancelAsync(subscription.Id, "cancelled");
    }

    public void Dispose()
    {
        Close("disposed");
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { _transport.Dispose(); } catch { }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                JObject? message;
                try { message = await _transport.ReceiveAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.Error("The connection failed: " + ex.Message);
                    Close("the connection failed: " + ex.Message);
                    return;
                }

                if (message == null) { Close("the connection was closed"); return; }
                Dispatch(message);
            }
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested) Close("the connection was closed");
        }
    }

    private void Dispatch(JObject message)
    {
        if (McpJsonRpc.IsResponse(message)) { CompleteResponse(message); return; }
        if (McpJsonRpc.IsRequest(message))
        {
            // Off the loop: answering may wait for a person, and the server's next message must still be read.
            _ = Task.Run(() => AnswerServerRequestAsync(message));
            return;
        }
        if (!McpJsonRpc.IsNotification(message)) return;

        var method = McpJsonRpc.MethodOf(message) ?? "";
        var parameters = message["params"] as JObject;
        RouteNotification(method, parameters, SubscriptionIdOf(message, parameters));
    }

    private void RouteNotification(string method, JObject? parameters, JToken? subscriptionId)
    {
        if (subscriptionId != null)
        {
            McpSubscription? subscription = null;
            lock (_gate)
            {
                _subscriptions.TryGetValue(McpJsonRpc.IdKey(subscriptionId), out subscription);
                if (subscription == null)
                {
                    // A server may echo a numeric id as a string; both name the same subscription.
                    var text = subscriptionId.ToString();
                    _subscriptions.TryGetValue("s:" + text, out subscription);
                    if (subscription == null) _subscriptions.TryGetValue("n:" + text, out subscription);
                }
            }
            subscription?.Receive(method, parameters);
        }
        RaiseNotification(method, parameters);
    }

    private static JToken? SubscriptionIdOf(JObject message, JObject? parameters)
    {
        var meta = (parameters?["_meta"] as JObject) ?? (message["_meta"] as JObject);
        return meta?["io.modelcontextprotocol/subscriptionId"];
    }

    private void CompleteResponse(JObject message)
    {
        var key = McpJsonRpc.IdKey(message["id"]);
        TaskCompletionSource<JObject>? completion;
        McpSubscription? subscription = null;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out completion)) return;
            _pending.Remove(key);
            if (_subscriptions.TryGetValue(key, out subscription)) _subscriptions.Remove(key);
        }
        if (subscription != null) subscription.Ended();
        completion.TrySetResult(message);
    }

    private void RaiseNotification(string method, JObject? parameters)
    {
        try { Notification?.Invoke(method, parameters); }
        catch (Exception ex) { _log.Warning("A notification handler failed: " + ex.Message); }
    }

    private async Task AnswerServerRequestAsync(JObject request)
    {
        var id = request["id"]!;
        var method = McpJsonRpc.MethodOf(request) ?? "";
        JObject response;
        try
        {
            var handler = ServerRequest;
            var result = handler == null ? null : await handler(request, _lifetime.Token).ConfigureAwait(false);
            response = result == null
                ? McpJsonRpc.Error(id, McpErrorCodes.MethodNotFound, "The client does not support '" + method + "'.")
                : McpJsonRpc.Result(id, result);
        }
        catch (McpException ex)
        {
            response = McpJsonRpc.Error(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            response = McpJsonRpc.Error(id, McpErrorCodes.InternalError, "The request was cancelled.");
        }
        catch (Exception ex)
        {
            response = McpJsonRpc.Error(id, McpErrorCodes.InternalError, ex.Message);
        }

        try { await _transport.SendAsync(response, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warning("An answer to '" + method + "' could not be sent: " + ex.Message); }
    }

    private async Task TryCancelAsync(JToken id, string reason)
    {
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await _transport.SendAsync(McpJsonRpc.Notification("notifications/cancelled",
                        new JObject { ["requestId"] = id, ["reason"] = reason }), timeout.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Debug("A cancellation notice could not be sent: " + ex.Message); }
    }

    private void RemovePending(string key)
    {
        lock (_gate) _pending.Remove(key);
    }

    private void Close(string reason)
    {
        TaskCompletionSource<JObject>[] waiting;
        McpSubscription[] subscriptions;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            waiting = _pending.Values.ToArray();
            subscriptions = _subscriptions.Values.ToArray();
            _pending.Clear();
            _subscriptions.Clear();
        }

        var message = "MCP server '" + _server.Name + "' is no longer connected (" + reason + ").";
        foreach (var completion in waiting)
            completion.TrySetException(new McpException(message));
        foreach (var subscription in subscriptions)
            subscription.SessionEnded(message);

        try { Closed?.Invoke(reason); }
        catch (Exception ex) { _log.Warning("A close handler failed: " + ex.Message); }
    }

    private static string Describe(TimeSpan timeout) =>
        timeout.TotalSeconds >= 1 ? timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s"
                                  : timeout.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
}
