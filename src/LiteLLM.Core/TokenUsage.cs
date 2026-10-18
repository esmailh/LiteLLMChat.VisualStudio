using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What one call cost, in the server's own accounting.
///
/// It is kept beside the messages rather than inside one: a <see cref="ChatMessage"/> is the transcript sent
/// back to the model, and a number about the request would ride along with every later round and be stored in
/// the session as something the conversation said. Here it belongs to the turn.
///
/// A missing report is null and never zero. A provider that omits <c>usage</c> while streaming has told us
/// nothing, and a zero would read as "this call was free".</summary>
public sealed class TokenUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Whether the server reported anything at all.</summary>
    public bool Any => PromptTokens > 0 || CompletionTokens > 0 || TotalTokens > 0;

    /// <summary>Reads the <c>usage</c> object of an OpenAI-compatible response, or of the final chunk of a
    /// streamed one. Returns null when the object is absent or carries no count.</summary>
    public static TokenUsage? Read(JToken? usage)
    {
        if (usage is not JObject) return null;
        var prompt = Number(usage["prompt_tokens"]);
        var completion = Number(usage["completion_tokens"]);
        var total = Number(usage["total_tokens"]);
        if (total == 0) total = Sum(prompt, completion);
        if (prompt == 0 && completion == 0 && total == 0) return null;
        return new TokenUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = total };
    }

    /// <summary>Adds one call's cost into a turn's running total. A call that reported nothing is ignored
    /// rather than counted as zero, so an unmeasured round cannot make the total look better than it was.</summary>
    public void Add(TokenUsage? other)
    {
        if (other == null) return;
        PromptTokens = Sum(PromptTokens, other.PromptTokens);
        CompletionTokens = Sum(CompletionTokens, other.CompletionTokens);
        TotalTokens = Sum(TotalTokens, other.TotalTokens);
    }

    /// <summary>The one line the transcript shows. Invariant formatting, because these are counts rather than
    /// a number the current locale should re-group.</summary>
    public string Line =>
        "Tokens · " + PromptTokens.ToString("N0", CultureInfo.InvariantCulture) + " in / "
        + CompletionTokens.ToString("N0", CultureInfo.InvariantCulture) + " out · "
        + TotalTokens.ToString("N0", CultureInfo.InvariantCulture) + " total";

    /// <summary>Usage is a report, not a contract: a server that omits it, formats it oddly or sends a number
    /// this type cannot hold must cost the turn nothing. Every path here answers with a count or with zero.</summary>
    private static int Number(JToken? token)
    {
        if (token == null || token.Type != JTokenType.Integer) return 0;
        try
        {
            // long first: a JSON integer arrives as one, and Value<int>() on a larger value throws.
            return Clamp(token.Value<long>());
        }
        catch (Exception)
        {
            // An integer too large for long arrives as a BigInteger and cannot be converted here at all.
            // Unreadable is reported as "not reported", never as an exception on the way to the model's answer.
            return 0;
        }
    }

    private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : value < 0 ? 0 : (int)value;

    /// <summary>Adds two non-negative counts without wrapping. Two honest counts can pass int.MaxValue on a
    /// long session, and a wrapped negative would draw a total smaller than its own parts.</summary>
    private static int Sum(int left, int right)
    {
        if (right <= 0) return Math.Max(0, left);
        if (left <= 0) return right;
        return left > int.MaxValue - right ? int.MaxValue : left + right;
    }
}
