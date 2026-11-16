using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>A conservative character estimate, not a tokenizer or server-reported usage.</summary>
public static class ModelRequestBudget
{
    public static int ReadContextTokens(JToken model)
    {
        foreach (var path in new[] { "context_window", "context_length", "max_model_len", "model_info.max_input_tokens" })
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
