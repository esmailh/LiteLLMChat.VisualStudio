using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public static class McpErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>The server needs the user to sign in. This is this client's own code; MCP reports the condition as
    /// HTTP 401, which has no JSON-RPC envelope to carry it.</summary>
    public const int AuthRequired = -32001;
}

public sealed class McpException : Exception
{
    public McpException(string message, int code = McpErrorCodes.InternalError, JToken? data = null,
        bool retryable = false, bool authRequired = false, Exception? inner = null, bool invalidGrant = false)
        : base(message, inner)
    {
        Code = code;
        Data = data;
        Retryable = retryable;
        AuthRequired = authRequired;
        InvalidGrant = invalidGrant;
    }

    public int Code { get; }
    /// <summary>True when an OAuth refresh was answered with <c>invalid_grant</c>: the stored grant is gone and has
    /// to be deleted, which is a different outcome from a token endpoint that was merely unreachable.</summary>
    public bool InvalidGrant { get; }
    /// <summary>The server's own <c>error.data</c>, kept for the log. Never shown as the whole message.</summary>
    public new JToken? Data { get; }
    public bool Retryable { get; }
    public bool AuthRequired { get; }

    public static McpException FromError(JObject error)
    {
        var code = error["code"]?.Type == JTokenType.Integer ? error["code"]!.Value<int>() : McpErrorCodes.InternalError;
        var message = error["message"]?.ToString() ?? "The server reported an error without a message.";
        // 5xx-equivalent protocol errors and rate limiting are worth one retry; a bad request or a missing method is not.
        var retryable = code == McpErrorCodes.InternalError || code == -32000 || code == -32002;
        return new McpException(message, code, error["data"], retryable);
    }
}

/// <summary>JSON-RPC 2.0 envelopes the transports carry. The MCP framing is ordinary JSON-RPC with one exception
/// this class records: a request id may be a string or a number, and the response must echo it exactly, so ids are
/// never coerced.</summary>
public static class McpJsonRpc
{
    public const string Version = "2.0";

    public static JObject Request(JToken id, string method, JObject? parameters = null)
    {
        var message = new JObject { ["jsonrpc"] = Version, ["id"] = id, ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        return message;
    }

    public static JObject Notification(string method, JObject? parameters = null)
    {
        var message = new JObject { ["jsonrpc"] = Version, ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        return message;
    }

    public static JObject Result(JToken id, JToken? result)
    {
        return new JObject { ["jsonrpc"] = Version, ["id"] = id, ["result"] = result ?? new JObject() };
    }

    public static JObject Error(JToken id, int code, string message, JToken? data = null)
    {
        var error = new JObject { ["code"] = code, ["message"] = message };
        if (data != null) error["data"] = data;
        return new JObject { ["jsonrpc"] = Version, ["id"] = id, ["error"] = error };
    }

    public static string? MethodOf(JObject message) => message["method"]?.Type == JTokenType.String
        ? message["method"]!.Value<string>()
        : null;

    /// <summary>Whether this message answers one of ours. A response always carries an id and one of result/error;
    /// a server request carries an id and a method, so the two cannot be confused.</summary>
    public static bool IsResponse(JObject message) =>
        message["id"] != null && message["id"]!.Type != JTokenType.Null &&
        (message["result"] != null || message["error"] != null);

    public static bool IsRequest(JObject message) =>
        message["id"] != null && message["id"]!.Type != JTokenType.Null && message["method"] != null;

    public static bool IsNotification(JObject message) =>
        (message["id"] == null || message["id"]!.Type == JTokenType.Null) && message["method"] != null;

    public static string IdKey(JToken? id) => id == null || id.Type == JTokenType.Null
        ? ""
        : id.Type == JTokenType.String ? "s:" + id.Value<string>() : "n:" + id.ToString(Formatting.None);

    public static JToken ResultOf(JObject response)
    {
        if (response["error"] is JObject error) throw McpException.FromError(error);
        return response["result"] ?? new JObject();
    }
}
