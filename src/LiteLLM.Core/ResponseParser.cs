using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public static class ResponseParser
{
    public static StreamUpdate ParseStreamChunk(string json, IDictionary<int, ToolCall>? accumulated = null)
    {
        var root = JObject.Parse(json);
        var choice = root["choices"]?.First;
        var delta = choice?["delta"] as JObject;
        var update = new StreamUpdate
        {
            ContentDelta = (string?)delta?["content"] ?? "",
            ReasoningDelta = (string?)delta?["reasoning_content"] ?? (string?)delta?["reasoning"] ?? "",
            FinishReason = (string?)choice?["finish_reason"] ?? "",
            // The usage chunk OpenAI-compatible servers send last has an empty "choices" array and no delta;
            // reading it here is what lets the client report the count without a second call.
            Usage = TokenUsage.Read(root["usage"])
        };
        var calls = delta?["tool_calls"] as JArray;
        if (calls == null) return update;
        foreach (var token in calls)
        {
            var index = (int?)token["index"] ?? 0;
            var call = accumulated != null && accumulated.TryGetValue(index, out var found) ? found : new ToolCall();
            call.Id += (string?)token["id"] ?? "";
            call.Name += (string?)token["function"]?["name"] ?? "";
            call.Arguments += (string?)token["function"]?["arguments"] ?? "";
            if (accumulated != null) accumulated[index] = call;
            update.ToolCalls.Add(call);
        }
        return update;
    }
    public static ChatMessage ParseCompletion(string json) => ParseCompletion(json, out _);

    /// <summary>Parses the message and, when the server reported it, what the call cost. The count is an
    /// <c>out</c> rather than a property of the message: it belongs to the call, not to anything that will be
    /// sent back in the next request.</summary>
    public static ChatMessage ParseCompletion(string json, out TokenUsage? usage)
    {
        var root = JObject.Parse(json);
        usage = TokenUsage.Read(root["usage"]);
        var message = root["choices"]?.First?["message"] ?? throw new JsonException("Response contains no assistant message.");
        var result = new ChatMessage { Role = "assistant", Content = (string?)message["content"] ?? "", Reasoning = (string?)message["reasoning_content"] ?? "" };
        foreach (var item in message["tool_calls"] ?? new JArray()) result.ToolCalls.Add(new ToolCall { Id = (string?)item["id"] ?? "", Name = (string?)item["function"]?["name"] ?? "", Arguments = (string?)item["function"]?["arguments"] ?? "" });
        return result;
    }
}
