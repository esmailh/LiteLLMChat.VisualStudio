using System;
using System.Net;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;
public sealed class LiteLlmException : Exception
{
    public LiteLlmException(string message, string technicalDetails, Exception? inner = null) : base(message, inner) => TechnicalDetails = technicalDetails;
    public string TechnicalDetails { get; }
    /// <summary>The exchange that failed, so the caller can offer "copy request" / "copy response".</summary>
    public HttpExchange? Exchange { get; set; }
}

/// <summary>The raw bytes of one round trip. Kept so a failure can be inspected instead of guessed at.</summary>
public sealed class HttpExchange
{
    public string Url { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string ResponseBody { get; set; } = "";
    public int Status { get; set; }
    public bool Streamed { get; set; }
}

public static class ErrorMapper
{
    public static string ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "The LiteLLM server rejected the request (400).",
        HttpStatusCode.Unauthorized => "The API key was rejected (401).",
        HttpStatusCode.Forbidden => "This API key is not allowed to perform that operation (403).",
        HttpStatusCode.NotFound => "The LiteLLM endpoint or model was not found (404).",
        (HttpStatusCode)429 => "The server rate limit was reached. Try again shortly.",
        _ when (int)status >= 500 => "The LiteLLM server encountered an error. Try again shortly.",
        _ => "The LiteLLM request failed (HTTP " + (int)status + ")."
    };

    /// <summary>A status code alone does not explain anything, so quote the server's own message — it is the only
    /// thing that says which parameter it disliked.</summary>
    public static string Explain(HttpStatusCode status, string body)
    {
        var detail = Detail(body);
        return detail.Length == 0 ? ForStatus(status) : ForStatus(status) + " " + detail;
    }

    /// <summary>Digs the human-readable reason out of whatever shape the server used.</summary>
    public static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var trimmed = body.Trim();
        try
        {
            var root = JToken.Parse(trimmed);
            var error = root["error"];
            var message = (string?)error?["message"] ?? (string?)error?["type"] ?? (string?)error ?? (string?)root["message"] ?? (string?)root["detail"] ?? (string?)root["error_description"];
            if (message != null && message.Length > 0) return Trim(message);
            if (error != null && error.Type != JTokenType.String) return Trim(error.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception) { }
        return Trim(trimmed);
    }

    private static string Trim(string value)
    {
        var single = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length > 500 ? single.Substring(0, 500) + "…" : single;
    }
}
