using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>A conservative character estimate, not a tokenizer or server-reported usage.</summary>
public static class ModelRequestBudget
{
    /// <summary>The window a server's own model entry reports, or zero for a server that reports none.
    ///
    /// The last two spellings matter more than they look: a LiteLLM gateway answers <c>/v1/models</c> with a flat
    /// <c>max_input_tokens</c>, and older builds read only the nested <c>model_info</c> form. Every model it knew
    /// therefore read as "unknown", the turn fell back to <see cref="UserSettings.FallbackContextTokens"/>, and a
    /// model whose real window was 372 000 was budgeted at forty thousand tokens — which is less than a real
    /// server's tool schemas. The schemas are the one part a request gives up when it does not fit, so MCP was
    /// dropped from every round and the model truthfully answered that it had no such tool.</summary>
    public static int ReadContextTokens(JToken model)
    {
        foreach (var path in new[]
                 {
                     "context_window", "context_length", "max_model_len", "model_info.max_input_tokens",
                     "max_input_tokens"
                 })
        {
            var value = model.SelectToken(path);
            if (value?.Type == JTokenType.Integer && long.TryParse(value.ToString(), out var tokens) && tokens > 0)
                return (int)Math.Min(tokens, int.MaxValue);
        }
        return 0;
    }

    public static int Limit(ModelInfo model, UserSettings settings)
    {
        var capacity = model.ContextWindowTokens > 0 ? model.ContextWindowTokens : Math.Max(1024, settings.FallbackContextTokens);
        foreach (var entry in (settings.ModelContextOverrides ?? "").Split(';'))
        {
            var split = entry.LastIndexOf('=');
            if (split <= 0) continue;
            var key = entry.Substring(0, split).Trim();
            if ((key == model.Key || key == model.Id) && int.TryParse(entry.Substring(split + 1).Trim(), out var value) && value >= 1024)
                capacity = value;
        }
        // Reserve output and 10% headroom. This ratio is only an estimate, particularly for non-Latin
        // content; the provider's actual token limit remains authoritative.
        var available = Math.Max(0L, (long)capacity - Math.Max(1, settings.MaxTokens) - Math.Max(1024L, capacity / 10L));
        return (int)Math.Min(ChatStateLimits.MaxPromptChars, available * 2);
    }

    public static int HistoryAllowance(int limit, IReadOnlyList<ChatMessage> fixedMessages, IReadOnlyList<ToolDefinition>? tools) =>
        (int)Math.Max(0L, limit - RequestBudget.Size(fixedMessages, tools) - 2048L);
}
