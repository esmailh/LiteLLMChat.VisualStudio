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

    /// <summary>The token counts the last call reported, or null when it reported none. Reset at the start of
    /// every call rather than left standing: a provider that omits usage must not be credited with the previous
    /// call's numbers.</summary>
    public TokenUsage? LastUsage { get; private set; }

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

    /// <summary>One complete (non-streamed) round trip. <paramref name="record"/> is false for a call the
    /// extension makes on its own behalf — a context summary — so that a background call does not overwrite the
    /// request and response JSON the user has open on the row they are looking at. The exchange is still built
    /// and still travels on any exception, so a failure is never reported without the payload that caused it;
    /// it is only the slot the copy commands read that is left alone.</summary>
    public async Task<ChatMessage> CompleteAsync(ServerConfiguration server, string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, CancellationToken cancellationToken, bool record = true)
    {
        LastUsage = null;
        var payload = CreatePayload(model, messages, settings, tools, false);
        using (var response = await SendWithRetryAsync(() => CreateRequest(server, HttpMethod.Post, "v1/chat/completions", payload), settings.RequestTimeoutSeconds, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var exchange = new HttpExchange { Url = Url(server, "v1/chat/completions"), RequestBody = Pretty(payload), ResponseBody = body, Status = (int)response.StatusCode, Streamed = false };
            if (record) LastExchange = exchange;
            EnsureSuccess(response, body, exchange);
            try
            {
                var message = ResponseParser.ParseCompletion(body, out var usage);
                LastUsage = usage;
                return message;
            }
            catch (JsonException ex) { throw new LiteLlmException("The server returned an invalid chat response.", ex.Message, ex) { Exchange = exchange }; }
        }
    }

    /// <summary>One streamed round trip, continued when the server cuts the answer short.
    ///
    /// A stream that ends without a finish reason — a reset socket, a proxy that timed out while the model was
    /// quiet, a worker that restarted — is an unfinished answer rather than a failed one. What has arrived is
    /// shown and kept, and the same turn is asked to go on from it, up to <see cref="UserSettings.StreamResumeAttempts"/>
    /// times. Each attempt carries the answer so far as the assistant message, because a model that cannot see
    /// its own beginning repeats it, and <see cref="StreamContinuationFilter"/> drops the repeat the ones that
    /// do repeat it emit anyway. A request that never reached the model is simply sent again: nothing of the
    /// answer exists to be duplicated. A partial tool call is never continued — half a call is not a call.</summary>
    public async Task StreamAsync(ServerConfiguration server, string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, Func<StreamUpdate, Task> onUpdate, CancellationToken cancellationToken)
    {
        LastUsage = null;
        // Asking for usage is an OpenAI request field, and a direct Azure connection may run an API version that
        // rejects it outright; a proxied Azure deployment reaches the provider through LiteLLM, which does not.
        // So the field is left off for Azure rather than risking a 400 before the first token.
        var includeUsage = !IsAzure(server);
        var content = new StringBuilder();           // what has been shown, which is also what a continuation carries
        var request = messages;                      // the turn as the next request sends it; replaced by a continuation
        var payloadOfTurn = (JObject?)null;          // the turn's own request, for the copy commands
        var exchanges = new StringBuilder();          // every attempt's raw stream, for the copy commands
        TokenUsage? usage = null;
        var toolCallsSeen = false;
        var reasoningSeen = false;
        var delivered = false;                       // anything at all reached the caller in this turn
        var resumes = 0;
        var resends = 0;
        var announceResume = false;                   // the next update says the answer is being continued
        StreamContinuationFilter? filter = null;      // set once an answer is being continued
        var deadline = TimeSpan.FromSeconds(Math.Max(5, settings.RequestTimeoutSeconds));
        var maxResumes = Math.Max(0, settings.StreamResumeAttempts);

        // Whatever the repeat filter is still holding belongs to the answer unless it turns out to be a repeat.
        // Called when the attempt ends rather than on every chunk, so a held sentence is not stuck on screen.
        async Task ReleaseHeldAsync()
        {
            if (filter == null) return;
            var rest = filter.Flush();
            if (rest.Length == 0) return;
            content.Append(rest);
            await onUpdate(new StreamUpdate { ContentDelta = rest }).ConfigureAwait(false);
        }

        while (true)
        {
            var payload = CreatePayload(model, request, settings, tools, true, includeUsage);
            if (payloadOfTurn == null) payloadOfTurn = payload;
            using (var message = CreateRequest(server, HttpMethod.Post, "v1/chat/completions", payload))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(deadline);
                HttpResponseMessage response;
                try { response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    // Nothing of the answer exists yet, so sending the request again cannot duplicate a word.
                    // Anything already shown would make this the wrong thing to do, and a continuation is the
                    // only request worth sending then.
                    if (!cancellationToken.IsCancellationRequested && content.Length == 0 && resends++ < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * resends), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw NetworkException(ex, cancellationToken);
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Record(server, "v1/chat/completions", Pretty(payload), response, failure, true);
                        // Some OpenAI-compatible servers reject the whole request over a field they do not know.
                        // The usage line is worth one retry, never the answer: drop the field and send the same
                        // request again. includeUsage stays false, so a server that still refuses cannot loop us.
                        if (includeUsage && IsClientError(response) && failure.IndexOf("stream_options", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            includeUsage = false;
                            continue;
                        }
                        EnsureSuccess(response, failure);
                    }
                    var media = response.Content.Headers.ContentType?.MediaType;
                    if (!string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Record(server, "v1/chat/completions", Pretty(payload), response, body, false);
                        var fallback = ResponseParser.ParseCompletion(body, out var fallbackUsage);
                        LastUsage = fallbackUsage;
                        // A server that answers a continuation as a whole message gets the same repeat filter.
                        if (filter != null && fallback.Content.Length > 0) fallback.Content = filter.Accept(fallback.Content) + filter.Flush();
                        content.Append(fallback.Content);
                        var wasResumed = announceResume; announceResume = false;
                        await onUpdate(new StreamUpdate { ContentDelta = fallback.Content, ReasoningDelta = fallback.Reasoning, ToolCalls = fallback.ToolCalls, FinishReason = "stop", Usage = fallbackUsage, Resumed = wasResumed }).ConfigureAwait(false); return;
                    }
                    var accumulated = new Dictionary<int, ToolCall>();
                    var transcript = new StringBuilder();
                    var finished = false;
                    Exception? broken = null;
                    Stream? stream = null;
                    try
                    {
                        stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        await SseParser.ReadAsync(stream, async data =>
                        {
                            transcript.Append("data: ").Append(data).Append('\n');
                            if (data == "[DONE]") { finished = true; return; }
                            var update = ResponseParser.ParseStreamChunk(data, accumulated);
                            if (announceResume) { update.Resumed = true; announceResume = false; }
                            if (update.Usage != null) { if (usage == null) usage = update.Usage; else usage.Add(update.Usage); LastUsage = usage; }
                            if (update.FinishReason.Length > 0) finished = true;
                            if (update.ReasoningDelta.Length > 0) { reasoningSeen = true; delivered = true; }
                            if (update.ToolCalls.Count > 0) { toolCallsSeen = true; delivered = true; }
                            if (filter != null && update.ContentDelta.Length > 0) update.ContentDelta = filter.Accept(update.ContentDelta);
                            if (update.ContentDelta.Length > 0) delivered = true;
                            content.Append(update.ContentDelta);
                            // Each chunk is what the watchdog is waiting for. A long answer that keeps writing is
                            // not a silent one, and killing it at the request timeout would be a lie about it.
                            timeout.CancelAfter(deadline);
                            await onUpdate(update).ConfigureAwait(false);
                        }, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        AppendAttempt(exchanges, transcript);
                        LastExchange = StreamExchange(server, payloadOfTurn, exchanges);
                        throw new LiteLlmException("The model stopped responding mid-stream.", "The stream produced nothing for " + (int)deadline.TotalSeconds + "s.") { Exchange = LastExchange };
                    }
                    catch (IOException ex) { broken = ex; }
                    finally { stream?.Dispose(); }

                    AppendAttempt(exchanges, transcript);
                    LastExchange = StreamExchange(server, payloadOfTurn, exchanges);
                    if (finished)
                    {
                        // What the filter still held was not a repeat after all; it belongs to the answer.
                        await ReleaseHeldAsync().ConfigureAwait(false);
                        return;
                    }

                    // The stream ended without a finish reason. An unfinished answer is continued; a request that
                    // produced nothing at all is simply sent again, because there is nothing to carry over.
                    if (StreamContinuation.CanContinue(content.Length, toolCallsSeen) && resumes < maxResumes)
                    {
                        // A second drop while a repeat is still being judged: if what is held was never a repeat,
                        // it is part of the answer and has to be shown before the next request is built from it.
                        await ReleaseHeldAsync().ConfigureAwait(false);
                        resumes++;
                        announceResume = true;
                        filter = new StreamContinuationFilter(content.ToString());
                        request = StreamContinuation.Messages(messages, content.ToString());
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // A request that produced nothing — no text, no reasoning, no tool call — may simply be sent
                    // again. Anything delivered is already on screen and repeating the request would show it twice.
                    if (!delivered && content.Length == 0 && resends++ < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * resends), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // Before reporting the turn over, whatever the filter was still holding is released: a repeat
                    // comes back empty, and anything else is text the user would otherwise lose.
                    await ReleaseHeldAsync().ConfigureAwait(false);
                    throw BrokenStream(broken, content.Length, reasoningSeen, toolCallsSeen, resumes, maxResumes);
                }
            }
        }
    }

    /// <summary>The failure sentence for a stream the server closed. Every one of these keeps the exchange, so
    /// the part of the answer that did arrive can still be copied out of the transcript.</summary>
    private LiteLlmException BrokenStream(Exception? broken, int contentLength, bool reasoningSeen, bool toolCallsSeen, int resumes, int maxResumes)
    {
        var reset = broken == null ? "" : " (" + broken.Message + ")";
        string message, detail;
        if (toolCallsSeen && contentLength == 0)
        {
            message = "The server closed the stream while the model was calling a tool.";
            detail = "A tool call arrives in pieces, so a call the connection cut in half cannot be run. The turn stopped here." + reset;
        }
        else if (contentLength == 0)
        {
            message = reasoningSeen
                ? "The server closed the stream while the model was still reasoning."
                : "The server closed the stream before the model answered.";
            detail = "Nothing of the answer had arrived to continue from." + reset;
        }
        else
        {
            message = maxResumes == 0
                ? "The server closed the stream before the answer was finished."
                : "The server closed the stream again after the answer was continued.";
            detail = "The answer was continued " + resumes + " time" + (resumes == 1 ? "" : "s")
                + " and the connection dropped again. The " + contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " characters that arrived are kept." + reset;
        }
        return new LiteLlmException(message, detail) { Exchange = LastExchange };
    }

    /// <summary>One attempt's raw stream, after the first. A comment line marks the join, so the copied response
    /// reads as one stream rather than as two that happen to follow each other.</summary>
    private static void AppendAttempt(StringBuilder all, StringBuilder attempt)
    {
        if (attempt.Length == 0) return;
        if (all.Length > 0) all.Append(": the connection dropped and the answer was continued\n\n");
        all.Append(attempt);
    }

    private HttpExchange StreamExchange(ServerConfiguration server, JObject? payload, StringBuilder response) =>
        new HttpExchange { Url = Url(server, "v1/chat/completions"), RequestBody = payload == null ? "" : Pretty(payload), ResponseBody = response.ToString(), Status = 200, Streamed = true };

    /// <summary>A refusal rather than a failure on the server's side — the shape a rejected request field
    /// comes back in, and the only status range worth re-sending the same turn for.</summary>
    private static bool IsClientError(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status >= 400 && status < 500;
    }

    private void Record(ServerConfiguration server, string relative, string requestBody, HttpResponseMessage response, string responseBody, bool streamed) =>
        LastExchange = new HttpExchange { Url = Url(server, relative), RequestBody = requestBody, ResponseBody = responseBody, Status = (int)response.StatusCode, Streamed = streamed };

    private static string Url(ServerConfiguration server, string relative) => server.BaseUrl.TrimEnd('/') + "/" + relative;
    private static bool IsAzure(ServerConfiguration server) => string.Equals(server.Provider, "Azure OpenAI", StringComparison.OrdinalIgnoreCase);
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
        var isAzure = IsAzure(server);
        var requestPath = relative;
        if (isAzure && relative == "v1/chat/completions")
        {
            if (string.IsNullOrWhiteSpace(server.Deployment)) throw new LiteLlmException("Azure deployment is required.", "Set a deployment name for this Azure OpenAI connection.");
            requestPath = "openai/deployments/" + Uri.EscapeDataString(server.Deployment) + "/chat/completions";
        }
        var requestUri = new Uri(baseUri, requestPath);
        if (isAzure && !string.IsNullOrWhiteSpace(server.ApiVersion))
            requestUri = new Uri(requestUri.AbsoluteUri + (requestUri.Query.Length == 0 ? "?" : "&") + "api-version=" + Uri.EscapeDataString(server.ApiVersion));
        var request = new HttpRequestMessage(method, requestUri);
        var key = _protector.Unprotect(server.ProtectedApiKey);
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (isAzure) request.Headers.TryAddWithoutValidation("api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload != null) request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        return request;
    }
    private static JObject CreatePayload(string model, IReadOnlyList<ChatMessage> messages, UserSettings settings, IReadOnlyList<ToolDefinition>? tools, bool stream, bool includeUsage = false)
    {
        var payload = new JObject { ["model"] = model, ["stream"] = stream, ["temperature"] = settings.Temperature, ["max_tokens"] = settings.MaxTokens, ["messages"] = new JArray(messages.Select(Serialize)) };
        // Without this a streamed answer carries no usage line at all: the server sends counts on a final chunk
        // only when the request asked for them, and a stream is the default path through this client.
        if (stream && includeUsage) payload["stream_options"] = new JObject { ["include_usage"] = true };
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
    /// <summary>Fails the response the way every other call does. The exchange that caused it is passed in
    /// rather than read off <see cref="LastExchange"/>, because a call made with <c>record: false</c> never put
    /// its exchange there and the one it would find belongs to an earlier request.</summary>
    private void EnsureSuccess(HttpResponseMessage response, string body, HttpExchange? exchange = null)
    {
        if (response.IsSuccessStatusCode) return;
        throw new LiteLlmException(ErrorMapper.Explain(response.StatusCode, body), "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n" + body) { Exchange = exchange ?? LastExchange };
    }
    /// <summary>A cancellation the caller asked for is a stop, not a failure — reporting it as one would paint a
    /// red error over a turn the user deliberately ended.</summary>
    private static Exception NetworkException(Exception ex, CancellationToken external) =>
        external.IsCancellationRequested
            ? (Exception)new OperationCanceledException(external)
            : new LiteLlmException(ex is TaskCanceledException ? "The LiteLLM request timed out." : "Could not connect to the LiteLLM server.", ex.Message, ex);
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
