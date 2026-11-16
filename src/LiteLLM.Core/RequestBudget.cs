using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiteLLM.Core;

public static class RequestBudget
{
    // This is a character safety bound, not a tokenizer estimate. Count the whole serialized request inputs,
    // including system instructions and schemas, rather than silently trusting the trimmed history alone.
    public static long Size(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools) =>
        (long)JsonConvert.SerializeObject(messages).Length + JsonConvert.SerializeObject(tools).Length;

    public static void EnsureFits(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxChars)
    {
        var size = Size(messages, tools);
        if (size > maxChars)
            throw new InvalidOperationException("Request context exceeds the " + maxChars + "-character safety budget (" + size +
                " characters including instructions and tool schemas). Start a new conversation, reduce attachments or select fewer MCP tools. No request was sent.");
    }
}
