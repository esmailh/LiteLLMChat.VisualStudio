using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>The legacy HTTP+SSE transport, which the 2024-11-05 and 2025-03-26 revisions used.
///
/// The client opens a GET stream and waits for an <c>endpoint</c> event that names where to POST. Messages come back
/// on the GET stream as <c>message</c> events. This is kept because a great many deployed servers still speak only
/// this, and it is also the fallback when a streamable endpoint answers 404 or 405.
///
/// Credentials are attached only when the named endpoint is on the same origin as the configured URL: the endpoint
/// is chosen by the server, and a server must not be able to point the client's bearer token at another host.</summary>
public sealed class McpSseTransport : IMcpTransport
{
    private readonly McpServerConfiguration _server;
    private readonly string? _solutionRoot;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly McpLog? _log;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _arrived = new SemaphoreSlim(0);
    private readonly ConcurrentQueue<JObject> _incoming = new ConcurrentQueue<JObject>();
    private readonly TaskCompletionSource<Uri> _endpoint = new TaskCompletionSource<Uri>();
    private Uri? _postUri;
    private string? _protocolVersion;
    private HttpResponseMessage? _streamResponse;
    private volatile bool _closed;
    private volatile bool _disposed;

    public McpSseTransport(McpServerConfiguration server, string? solutionRoot = null, HttpClient? http = null,
        McpLog? log = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _solutionRoot = solutionRoot;
        _log = log;
        _ownsClient = http == null;
        _client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Describe => _server.Url;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var problem = _server.ValidationError();
        if (problem != null) throw new McpException(problem);
        if (!Uri.TryCreate(_server.Url, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new McpException("MCP server '" + _server.Name + "' needs an absolute http(s) URL.");

        var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(request, baseUri);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException("MCP server '" + _server.Name + "' could not be reached: " + ex.Message, inner: ex);
        }

        if (McpHttpRetry.IsAuthFailure(response))
        {
            var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new McpException("MCP server '" + _server.Name + "' requires authentication (" + failure + ").",
                authRequired: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
            response.Dispose();
            throw new McpException("MCP server '" + _server.Name + "' answered with " + failure);
        }

        _streamResponse = response;
        _ = Task.Run(() => ReadStreamAsync(response));
        using (cancellationToken.Register(() => _endpoint.TrySetCanceled()))
        {
            try { _postUri = await _endpoint.Task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                response.Dispose();
                throw;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    public async Task SendAsync(JObject message, CancellationToken cancellationToken)
    {
        var uri = _postUri;
        if (uri == null || _closed) throw new McpException("MCP server '" + _server.Name + "' is not connected.");

        var response = await McpHttpRetry.SendAsync(_client, () => BuildPost(uri, message), _server.Name,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (McpHttpRetry.IsAuthFailure(response))
            {
                var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
                throw new McpException("MCP server '" + _server.Name + "' requires authentication (" + failure + ").",
                    authRequired: true);
            }
            if (!response.IsSuccessStatusCode)
            {
                var failure = await McpHttpRetry.FailureAsync(response).ConfigureAwait(false);
                throw new McpException("MCP server '" + _server.Name + "' answered with " + failure);
            }

            if (response.Content == null) return;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return;
            if (TryParse(body, out var reply)) Enqueue(reply!);
        }
        finally { response.Dispose(); }
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

    public void SetSession(string? sessionId, string? protocolVersion) => _protocolVersion = protocolVersion;

    private async Task ReadStreamAsync(HttpResponseMessage response)
    {
        try
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await SseParser.ReadAsync(stream, OnEventAsync, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _log?.Warning("The event stream from '" + _server.Name + "' ended: " + ex.Message);
        }
        finally
        {
            _streamResponse = null;
            response.Dispose();
            _endpoint.TrySetException(new McpException(
                "MCP server '" + _server.Name + "' closed its event stream before naming a message endpoint."));
            _closed = true;
            _arrived.Release();
        }
    }

    private Task OnEventAsync(string data, string? eventName)
    {
        if (string.Equals(eventName, "endpoint", StringComparison.OrdinalIgnoreCase))
        {
            ResolveEndpoint(data);
            return Task.CompletedTask;
        }
        if (eventName == null || eventName.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParse(data, out var message)) Enqueue(message!);
        }
        return Task.CompletedTask;
    }

    private void ResolveEndpoint(string data)
    {
        var baseUri = new Uri(_server.Url);
        if (!Uri.TryCreate(baseUri, data.Trim(), out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _endpoint.TrySetException(new McpException("MCP server '" + _server.Name + "' named an unusable endpoint: " +
                                                       (data.Length > 200 ? data.Substring(0, 200) + "…" : data)));
            return;
        }
        _endpoint.TrySetResult(uri);
    }

    private void Enqueue(JObject message)
    {
        _incoming.Enqueue(message);
        _arrived.Release();
    }

    private bool TryParse(string text, out JObject? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            message = JToken.Parse(text) as JObject;
            return message != null;
        }
        catch (JsonException)
        {
            _log?.Debug("A message from '" + _server.Name + "' was not JSON and was ignored.");
            return false;
        }
    }

    private HttpRequestMessage BuildPost(Uri uri, JObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyHeaders(request, uri);
        return request;
    }

    private void ApplyHeaders(HttpRequestMessage request, Uri target)
    {
        if (!SameOrigin(new Uri(_server.Url), target)) return;

        foreach (var pair in _server.Headers)
            request.Headers.TryAddWithoutValidation(pair.Key, McpSecrets.Expand(pair.Value, _solutionRoot));

        var token = McpSecrets.Unprotect(_server.ProtectedAccessToken);
        if (token.Length > 0 && !_server.Headers.ContainsKey("Authorization"))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrEmpty(_protocolVersion))
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _closed = true;
        _arrived.Release();
        _endpoint.TrySetCanceled();
        // The reader may be blocked in a network read, which the token does not interrupt; closing the stream does.
        try { _streamResponse?.Dispose(); } catch { }
        _streamResponse = null;
        if (_ownsClient) _client.Dispose();
    }
}
