using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLLM.Core;

/// <summary>The parts two HTTP transports share: one retry for a rate limit or a transient server failure, and a
/// failure sentence that says what the server actually answered.
///
/// A retry is capped in both count and delay. The user is waiting on a turn, and a server that says "come back in
/// an hour" must not hold the tool window for an hour; the failure is reported instead.</summary>
internal static class McpHttpRetry
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> build,
        string serverName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(build(), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new McpException("MCP server '" + serverName + "' could not be reached: " + ex.Message, inner: ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException("The request to MCP server '" + serverName + "' timed out.");
            }

            if (response.StatusCode == (HttpStatusCode)429 && attempt == 0)
            {
                var delay = Delay(response);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if ((int)response.StatusCode >= 500 && attempt == 0)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    public static bool IsAuthFailure(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden;

    public static async Task<string> FailureAsync(HttpResponseMessage response)
    {
        var body = "";
        try { body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim(); }
        catch { }
        if (body.Length > 300) body = body.Substring(0, 300) + "…";
        return "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + (body.Length > 0 ? ": " + body : "");
    }

    private static TimeSpan Delay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta) return Clamp(delta);
        if (retryAfter?.Date is DateTimeOffset date) return Clamp(date - DateTimeOffset.UtcNow);
        return TimeSpan.FromSeconds(1);
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;
}
