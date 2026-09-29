using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class LiteLlmClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ApiKeyProtector _protector;
    private readonly bool _ownsClient;
    public LiteLlmClient(HttpClient? http = null, ApiKeyProtector? protector = null)
    {
        _ownsClient = http == null; _http = http ?? new HttpClient(); _protector = protector ?? new ApiKeyProtector();
    }

    /// <summary>The last round trip, successful or not — what the "copy request" / "copy response" commands read.</summary>
    public HttpExchange? LastExchange { get; private set; }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(ServerConfiguration server, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using (var response = await SendWithRetryAsync(() => CreateRequest(server, HttpMethod.Get, "v1/models", null), timeoutSeconds, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Record(server, "v1/models", "", response, body, false);
            EnsureSuccess(response, body);
            try
            {
                var data = JObject.Parse(body)["data"] as JArray ?? throw new JsonException("Missing data array.");
                return data.Select(x => new ModelInfo { Id = (string?)x["id"] ?? "", OwnedBy = (string?)x["owned_by"] ?? "", ServerId = server.Id, ServerName = server.Name }).Where(x => x.Id.Length > 0).ToList();
            }
            catch (JsonException ex) { throw new LiteLlmException("The server returned an invalid model list.", ex.Message, ex) { Exchange = LastExchange }; }
        }
    }

    public async Task<ChatMessage> CompleteAsync(ServerConfiguration server, string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, CancellationToken cancellationToken)
    {
        var payload = CreatePayload(model, messages, settings, tools, false);
        using (var response = await SendWithRetryAsync(() => CreateRequest(server, HttpMethod.Post, "v1/chat/completions", payload), settings.RequestTimeoutSeconds, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Record(server, "v1/chat/completions", Pretty(payload), response, body, false);
            EnsureSuccess(response, body);
            try { return ResponseParser.ParseCompletion(body); }
            catch (JsonException ex) { throw new LiteLlmException("The server returned an invalid chat response.", ex.Message, ex) { Exchange = LastExchange }; }
        }
    }

    public async Task StreamAsync(ServerConfiguration server, string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, Func<StreamUpdate, Task> onUpdate, CancellationToken cancellationToken)
    {
        var payload = CreatePayload(model, messages, settings, tools, true);
        using (var request = CreateRequest(server, HttpMethod.Post, "v1/chat/completions", payload))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.RequestTimeoutSeconds)));
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { throw NetworkException(ex, cancellationToken); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var failure = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Record(server, "v1/chat/completions", Pretty(payload), response, failure, true);
                    EnsureSuccess(response, failure);
                }
                var media = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Record(server, "v1/chat/completions", Pretty(payload), response, body, false);
                    var fallback = ResponseParser.ParseCompletion(body);
                    await onUpdate(new StreamUpdate { ContentDelta = fallback.Content, ReasoningDelta = fallback.Reasoning, ToolCalls = fallback.ToolCalls, FinishReason = "stop" }).ConfigureAwait(false); return;
                }
                var accumulated = new Dictionary<int, ToolCall>();
                var transcript = new StringBuilder();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    try
                    {
                        await SseParser.ReadAsync(stream, async data =>
                        {
                            transcript.Append("data: ").Append(data).Append('\n');
                            if (data != "[DONE]") await onUpdate(ResponseParser.ParseStreamChunk(data, accumulated)).ConfigureAwait(false);
                        }, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        LastExchange = new HttpExchange { Url = Url(server, "v1/chat/completions"), RequestBody = Pretty(payload), ResponseBody = transcript.ToString(), Status = 200, Streamed = true };
                        throw new LiteLlmException("The model stopped responding mid-stream.", "The stream produced nothing for " + Math.Max(5, settings.RequestTimeoutSeconds) + "s.") { Exchange = LastExchange };
                    }
                LastExchange = new HttpExchange { Url = Url(server, "v1/chat/completions"), RequestBody = Pretty(payload), ResponseBody = transcript.ToString(), Status = 200, Streamed = true };
            }
        }
    }

    private void Record(ServerConfiguration server, string relative, string requestBody, HttpResponseMessage response, string responseBody, bool streamed) =>
        LastExchange = new HttpExchange { Url = Url(server, relative), RequestBody = requestBody, ResponseBody = responseBody, Status = (int)response.StatusCode, Streamed = streamed };

    private static string Url(ServerConfiguration server, string relative) => server.BaseUrl.TrimEnd('/') + "/" + relative;
    private static string Pretty(JObject payload) => payload.ToString(Formatting.Indented);

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
                try
                {
                    var response = await _http.SendAsync(factory(), HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                    if (attempt < 2 && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)) { response.Dispose(); await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false); continue; }
                    return response;
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && attempt < 2 && !cancellationToken.IsCancellationRequested) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { throw NetworkException(ex, cancellationToken); }
            }
        }
    }
    private HttpRequestMessage CreateRequest(ServerConfiguration server, HttpMethod method, string relative, JObject? payload)
    {
        if (!Uri.TryCreate(server.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)) throw new LiteLlmException("The server URL is invalid.", server.BaseUrl);
        var request = new HttpRequestMessage(method, new Uri(baseUri, relative));
        var key = _protector.Unprotect(server.ProtectedApiKey);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload != null) request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        return request;
    }
    private static JObject CreatePayload(string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, bool stream)
    {
        var payload = new JObject { ["model"] = model, ["stream"] = stream, ["temperature"] = settings.Temperature, ["max_tokens"] = settings.MaxTokens, ["messages"] = new JArray(messages.Select(Serialize)) };
        if (settings.EnableToolCalling && tools != null && tools.Count > 0) payload["tools"] = JArray.FromObject(tools);
        return payload;
    }
    /// <summary>An assistant turn that asked for tools must echo those calls back, and every tool result must name the call it answers — OpenAI-compatible servers reject the next request with HTTP 400 otherwise.</summary>
    private static JObject Serialize(ChatMessage message)
    {
        var result = new JObject { ["role"] = message.Role };
        if (message.ToolCalls.Count > 0)
        {
            // Empty string, not null: OpenAI accepts either, but stricter OpenAI-compatible servers reject a null content.
            result["content"] = message.Content;
            // DeepSeek's thinking mode hands its thoughts back as reasoning_content and refuses the next turn
            // ("The reasoning_content in the thinking mode must be passed back to the API") unless the assistant
            // turn that called a tool carries them again. Only that turn: elsewhere the field is merely redundant.
            if (message.Reasoning.Length > 0) result["reasoning_content"] = message.Reasoning;
            result["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JObject { ["name"] = call.Name, ["arguments"] = call.Arguments.Length > 0 ? call.Arguments : "{}" }
            }));
        }
        else result["content"] = message.Content;
        if (message.Role == "tool")
        {
            result["tool_call_id"] = message.ToolCallId;
            if (message.ToolName.Length > 0) result["name"] = message.ToolName;
        }
        return result;
    }
    private void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        throw new LiteLlmException(ErrorMapper.Explain(response.StatusCode, body), "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + body) { Exchange = LastExchange };
    }
    /// <summary>A cancellation the caller asked for is a stop, not a failure — reporting it as one would paint a
    /// red error over a turn the user deliberately ended.</summary>
    private static Exception NetworkException(Exception ex, CancellationToken external) =>
        external.IsCancellationRequested
            ? (Exception)new OperationCanceledException(external)
            : new LiteLlmException(ex is TaskCanceledException ? "The LiteLLM request timed out." : "Could not connect to the LiteLLM server.", ex.Message, ex);
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
