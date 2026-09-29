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
            FinishReason = (string?)choice?["finish_reason"] ?? ""
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
    public static ChatMessage ParseCompletion(string json)
    {
        var root = JObject.Parse(json);
        var message = root["choices"]?.First?["message"] ?? throw new JsonException("Response contains no assistant message.");
        var result = new ChatMessage { Role = "assistant", Content = (string?)message["content"] ?? "", Reasoning = (string?)message["reasoning_content"] ?? "" };
        foreach (var item in message["tool_calls"] ?? new JArray()) result.ToolCalls.Add(new ToolCall { Id = (string?)item["id"] ?? "", Name = (string?)item["function"]?["name"] ?? "", Arguments = (string?)item["function"]?["arguments"] ?? "" });
        return result;
    }
}
