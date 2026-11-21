using System;
using System.Linq;

namespace LiteLLM.Core;

/// <summary>Recognises the one rejection that compaction can answer: a provider refusing the whole request
/// because the prompt no longer fits its window. It is a 400 (or, for a body-size limit, a 413) whose text says
/// what was exceeded — every provider words it differently, and a 400 for anything else must not start a fold.
///
/// When it matches, the turn folds the conversation and sends the round again rather than failing: the estimate
/// that decided nothing needed to move has just been proved low, and the answer the user asked for is still one
/// request away.</summary>
public static class ContextOverflow
{
    /// <summary>The wordings the OpenAI-compatible providers and their proxies use for this one error.</summary>
    private static readonly string[] Phrases =
    {
        "context_length_exceeded",
        "maximum context length",
        "context length",
        "context window",
        "too many tokens",
        "prompt is too long",
        "input is too long",
        "reduce the length",
        "maximum number of tokens",
        "exceeds the model's maximum",
        "string too long"
    };

    public static bool IsOverflow(LiteLlmException exception)
    {
        if (exception == null) return false;
        var status = exception.Exchange?.Status ?? 0;
        // No recorded status is accepted: a client that failed before the response was captured still carries the
        // provider's sentence, and wrongly folding costs one call while refusing costs the turn.
        if (status != 0 && status != 400 && status != 413) return false;
        return LooksLike(exception.Message + "\n" + exception.TechnicalDetails + "\n"
            + (exception.Exchange?.ResponseBody ?? ""));
    }

    /// <summary>The text test on its own, so a message the user pasted, a log line or a test can be asked the
    /// same question without inventing an exception around it.</summary>
    public static bool LooksLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Phrases.Any(phrase => text!.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
