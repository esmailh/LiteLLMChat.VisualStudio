using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>The MCP "Streamable HTTP" transport: one endpoint that answers a POST with either a single JSON message
/// or an SSE stream, and that may also hold an independent GET stream for messages nobody asked for.
///
/// A POST returns as soon as the response headers are read. The body is parsed on its own task because an SSE
/// response can stay open for as long as the server likes, and the session's receive loop must be free to read the
/// messages that arrive on it. Nothing here decides that a server is broken: a 404 or 405 on the first request is
/// recorded in <see cref="SuggestsSseFallback"/> so the caller can try the older HTTP+SSE transport.</summary>
public sealed class McpHttpTransport : IMcpTransport, IMcpStreamControl
{
    private readonly McpServerConfiguration _server;
    private readonly string? _solutionRoot;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly McpLog? _log;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _arrived = new SemaphoreSlim(0);
    private readonly ConcurrentQueue<JObject> _incoming = new ConcurrentQueue<JObject>();
    private readonly ConcurrentDictionary<HttpResponseMessage, bool> _active = new ConcurrentDictionary<HttpResponseMessage, bool>();
    private readonly ConcurrentDictionary<string, HttpResponseMessage> _streams = new ConcurrentDictionary<string, HttpResponseMessage>();
    private readonly ConcurrentDictionary<string, bool> _cancelRequested = new ConcurrentDictionary<string, bool>();
    private readonly object _sessionGate = new object();
    private string? _sessionId;
    private string? _protocolVersion;
    private int _getStreamStarted;
    private volatile bool _closed;
    private volatile bool _disposed;

    public McpHttpTransport(McpServerConfiguration server, string? solutionRoot = null, HttpClient? http = null,
        McpLog? log = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _solutionRoot = solutionRoot;
        _log = log;
        _ownsClient = http == null;
        _client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Describe => _server.Url;

    /// <summary>Set when the server answered 404 or 405: the endpoint is not a streamable one, and the legacy
    /// HTTP+SSE transport is the one to try next.</summary>
    public bool SuggestsSseFallback { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problem = _server.ValidationError();
        if (problem != null) throw new McpException(problem);
        if (!Uri.TryCreate(_server.Url, UriKind.Absolute, out _))
            throw new McpException("MCP server '" + _server.Name + "' needs an absolute URL.");
        return Task.CompletedTask;
    }

    public async Task SendAsync(JObject message, CancellationToken cancellationToken)
    {
        if (_closed) throw new McpException("MCP server '" + _server.Name + "' is not connected.");
        var response = await McpHttpRetry.SendAsync(_client, () => BuildPost(message), _server.Name, cancellationToken)
            .ConfigureAwait(false);
        var id = message["id"];
        await AcceptAsync(response, id == null || id.Type == JTokenType.Null ? null : McpJsonRpc.IdKey(id))
            .ConfigureAwait(false);
    }

    /// <summary>Closing the stream is how the stateless revision cancels a <c>subscriptions/listen</c> on HTTP. A
    /// cancellation can arrive before the response headers do, so the request is remembered and the response is
    /// disposed the moment it appears.</summary>
    public void CancelStream(JToken requestId)
    {
        var key = McpJsonRpc.IdKey(requestId);
        _cancelRequested[key] = true;
        if (!_streams.TryRemove(key, out var response)) return;
        _cancelRequested.TryRemove(key, out _);
        try { response.Dispose(); } catch { }
    }

    public async Task<JObject?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var message)) return message;
            if (_closed) return null;
            try { await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    /// <summary>Called by the session once it has negotiated. The session id itself is captured from the server's
    /// response header, because only the transport sees it.</summary>
    public void SetSession(string? sessionId, string? protocolVersion)
    {
        lock (_sessionGate)
        {
            if (!string.IsNullOrEmpty(sessionId)) _sessionId = sessionId;
            _protocolVersion = protocolVersion;
        }
        StartGetStream();
    }

    private async Task AcceptAsync(HttpResponseMessage response, string? idKey)
    {
        if (McpHttpRetry.IsAuthFailure(response))
        {
            var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new McpException("MCP server '" + _server.Name + "' requires authentication (" + failure +
                                   "). Sign in from the MCP dashboard.", authRequired: true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            SuggestsSseFallback = true;
            var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new McpException("MCP server '" + _server.Name + "' did not accept a streamable HTTP request (" +
                                   failure + ").");
        }

        if (!response.IsSuccessStatusCode)
        {
            var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new McpException("MCP server '" + _server.Name + "' answered with " + failure);
        }

        CaptureSession(response);
        StartGetStream();

        if (response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.NoContent ||
            response.Content == null)
        {
            response.Dispose();
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            if (idKey != null)
            {
                if (_cancelRequested.TryRemove(idKey, out _))
                {
                    response.Dispose();
                    return;
                }
                _streams[idKey] = response;
            }
            // Reading must not wait here: the stream may carry several messages and may stay open.
            _active[response] = true;
            _ = Task.Run(() => ReadEventsAsync(response, idKey));
            return;
        }

        string body;
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        finally { response.Dispose(); }
        Enqueue(Parse(body, "response"));
    }

    private async Task ReadEventsAsync(HttpResponseMessage response, string? idKey)
    {
        try
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await SseParser.ReadAsync(stream, OnEventAsync, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _log?.Warning("The response stream from '" + _server.Name + "' ended: " + ex.Message);
        }
        finally
        {
            _active.TryRemove(response, out _);
            if (idKey != null)
            {
                _streams.TryRemove(idKey, out _);
                _cancelRequested.TryRemove(idKey, out _);
            }
            response.Dispose();
        }
    }

    private Task OnEventAsync(string data, string? eventName)
    {
        if (eventName != null && !eventName.Equals("message", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        Enqueue(Parse(data, "event"));
        return Task.CompletedTask;
    }

    private void Enqueue(JObject? message)
    {
        if (message == null) return;
        _incoming.Enqueue(message);
        _arrived.Release();
    }

    private JObject? Parse(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (JToken.Parse(text) is JObject message) return message;
        }
        catch (JsonException) { }
        _log?.Debug("A " + source + " from '" + _server.Name + "' was not a JSON-RPC object and was ignored.");
        return null;
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        lock (_sessionGate)
        {
            if (response.Headers.TryGetValues("MCP-Session-Id", out var ids))
            {
                var id = ids.FirstOrDefault();
                if (!string.IsNullOrEmpty(id)) _sessionId = id;
            }
            if (response.Headers.TryGetValues("MCP-Protocol-Version", out var versions))
            {
                var version = versions.FirstOrDefault();
                if (!string.IsNullOrEmpty(version)) _protocolVersion = version;
            }
        }
    }

    private void StartGetStream()
    {
        string? sessionId;
        lock (_sessionGate) sessionId = _sessionId;
        if (string.IsNullOrEmpty(sessionId)) return;
        if (Interlocked.CompareExchange(ref _getStreamStarted, 1, 0) != 0) return;
        _ = Task.Run(() => GetStreamAsync());
    }

    private async Task GetStreamAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _server.Url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyHeaders(request);
            using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                       _lifetime.Token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                    response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Allowed: the server offers no independent event stream. Responses still come back on POSTs.
                    _log?.Debug("'" + _server.Name + "' has no independent event stream.");
                    return;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _log?.Debug("The event stream from '" + _server.Name + "' was refused.");
                    return;
                }
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    await SseParser.ReadAsync(stream, OnEventAsync, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _log?.Debug("The event stream from '" + _server.Name + "' ended: " + ex.Message);
        }
    }

    private HttpRequestMessage BuildPost(JObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _server.Url)
        {
            Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddMethodHeaders(request, message);
        ApplyHeaders(request);
        return request;
    }

    /// <summary>The stateless revision also names the call in headers, so a gateway can route without parsing the
    /// body. Only sent once the server is known to speak that revision, or for the probe that establishes it.</summary>
    private void AddMethodHeaders(HttpRequestMessage request, JObject message)
    {
        var method = message["method"]?.Type == JTokenType.String ? message.Value<string>("method") : null;
        if (method == null) return;
        string? version;
        lock (_sessionGate) version = _protocolVersion;
        if (!McpProtocolVersions.IsModern(version) && method != "server/discover") return;

        request.Headers.TryAddWithoutValidation("Mcp-Method", method);
        var name = (message["params"] as JObject)?["name"]?.Type == JTokenType.String
            ? message["params"]!["name"]!.Value<string>()
            : null;
        if (!string.IsNullOrEmpty(name)) request.Headers.TryAddWithoutValidation("Mcp-Name", name);
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var pair in _server.Headers)
            request.Headers.TryAddWithoutValidation(pair.Key, McpSecrets.Expand(pair.Value, _solutionRoot));

        var token = McpSecrets.Unprotect(_server.ProtectedAccessToken);
        if (token.Length > 0 && !_server.Headers.ContainsKey("Authorization"))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        lock (_sessionGate)
        {
            if (!string.IsNullOrEmpty(_sessionId))
                request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);
            if (!string.IsNullOrEmpty(_protocolVersion))
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        // Closing the open response streams is what unblocks their readers; a reader waiting in a network read does
        // not observe the token.
        foreach (var response in _active.Keys)
        {
            try { response.Dispose(); } catch { }
        }
        _active.Clear();
        foreach (var response in _streams.Values)
        {
            try { response.Dispose(); } catch { }
        }
        _streams.Clear();
        string? sessionId;
        lock (_sessionGate) sessionId = _sessionId;
        var client = _client;
        if (sessionId != null && sessionId.Length > 0)
        {
            // Removing the session is best-effort and must not hold up closing the tool window, but the client the
            // transport created has to outlive the request it is still using.
            _ = Task.Run(async () =>
            {
                try { await DeleteSessionAsync(client, sessionId).ConfigureAwait(false); }
                catch { }
                if (_ownsClient) client.Dispose();
            });
        }
        else if (_ownsClient) client.Dispose();

        _closed = true;
        _arrived.Release();
    }

    private async Task DeleteSessionAsync(HttpClient client, string sessionId)
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        using (var request = new HttpRequestMessage(HttpMethod.Delete, _server.Url))
        {
            request.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
            ApplyHeaders(request);
            using (await client.SendAsync(request, timeout.Token).ConfigureAwait(false)) { }
        }
    }
}
