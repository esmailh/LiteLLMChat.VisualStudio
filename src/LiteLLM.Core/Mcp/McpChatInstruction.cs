using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLLM.Core;

/// <summary>What a message asked the conversation to remember about MCP. <see cref="None"/> is the answer for
/// every message that is not an instruction about it at all.</summary>
public enum McpChatRequest
{
    None,
    /// <summary>Offer the servers' tools on every turn of this chat from now on.</summary>
    EveryTurn,
    /// <summary>Stop doing that: back to a turn that is offered them only when its own message says so.</summary>
    OnlyWhenMentioned
}

/// <summary>An instruction in a message that outlives the message: "use MCP in this chat from now on".
///
/// A turn normally decides its own MCP offer from its own text (<see cref="McpToolSelection"/>), which is right for
/// one message and wrong for a chat the user has already explained itself in: the sentence is repeated every turn,
/// or paid for by a setting the user has to find. So a message that names MCP and asks for a rule about the rest of
/// the chat is read as a preference of the conversation (<see cref="ChatSessionState.McpEveryTurn"/>) and kept with
/// it — and the opposite sentence clears it, because a chat that can only be talked into a state and never out of one
/// is a trap.
///
/// The grammar is deliberately narrow: one sentence has to name MCP, carry a scope word that makes it about the
/// conversation rather than this message ("this chat", "from now on", "always", "این چت", "همیشه", "از این به بعد"),
/// and ask to use, offer or stop it. A passing mention, a question about MCP and a request for one turn all stay
/// <see cref="None"/>, because a chat that changed its own behaviour from a mention would be a chat that cannot be
/// predicted. A miss costs one repeated sentence; a wrong hit puts every later turn's MCP schemas in a chat that
/// never asked for them.</summary>
public static class McpChatInstruction
{
    /// <summary>Words that make the sentence about this conversation rather than this message. A phrase rather than a
    /// word where one word would be too broad: "every" belongs to "every file" as much as to "every turn".</summary>
    private static readonly string[][] Scopes =
    {
        new[] { "this", "chat" }, new[] { "this", "conversation" }, new[] { "this", "thread" },
        new[] { "from", "now", "on" }, new[] { "from", "here", "on" },
        new[] { "the", "rest", "of", "this", "chat" }, new[] { "the", "whole", "chat" }, new[] { "the", "entire", "chat" },
        new[] { "every", "turn" }, new[] { "every", "message" }, new[] { "every", "round" },
        new[] { "each", "turn" }, new[] { "each", "message" }, new[] { "every", "time" },
        new[] { "always" },
        new[] { "این", "چت" }, new[] { "این", "گفتگو" }, new[] { "همیشه" },
        new[] { "هر", "بار" }, new[] { "هر", "پیام" }, new[] { "هر", "دور" },
        new[] { "از", "این", "به", "بعد" }, new[] { "از", "این", "پس" }, new[] { "دیگه" },
        new[] { "کلا" }, new[] { "تمام", "این", "چت" }
    };

    /// <summary>Words that ask for the servers' tools. "Offer" and "available" are here for a message that talks about
    /// the tool list rather than about a call.</summary>
    private static readonly string[] Verbs =
    {
        "use", "uses", "using", "used", "call", "calls", "calling", "called", "offer", "offers", "offered",
        "access", "enable", "enabled", "keep", "keeps", "give", "allow", "allowed", "available",
        "استفاده", "صدا", "فراخوان", "دسترس", "اختیار", "روشن", "فعال", "بده", "باش", "خاموش", "قطع"
    };

    /// <summary>Verbs that say nothing about MCP on their own: "turn" is a noun in "every turn", and reading it as a
    /// verb would let a remark about a slow turn change the chat. Beside an <c>off</c> or an <c>on</c> it is the
    /// other half of a request to switch the tools off the chat.</summary>
    private static readonly string[] SwitchVerbs = { "turn", "switch", "shut", "toggle" };

    private static readonly string[] SwitchStates = { "off", "on", "down" };

    /// <summary>Words that turn the sentence into the opposite instruction. Only read in the sentence that carries
    /// the verb, so "don't open the file; always use MCP" is not a refusal.</summary>
    private static readonly string[] Negations =
    {
        "dont", "doesnt", "didnt", "not", "never", "stop", "stops", "without", "disable", "disabled", "off", "no",
        "refrain", "ignore",
        "نکن", "نخواه", "نمی", "بدون", "خاموش", "غیر", "نباش", "نیست"
    };

    /// <summary>What the message asked the chat to remember, or <see cref="McpChatRequest.None"/> when it asked for
    /// nothing that lasts. Only the first sentence that reads as an instruction counts: a message that contradicts
    /// itself is answered by the first thing it said.</summary>
    public static McpChatRequest Read(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return McpChatRequest.None;

        foreach (var sentence in Sentences(WithoutFencedCode(message!)))
        {
            var tokens = Tokens(sentence);
            if (!tokens.Contains("mcp")) continue;
            if (!HasScope(tokens) || !HasVerb(tokens)) continue;
            return HasNegation(tokens) ? McpChatRequest.OnlyWhenMentioned : McpChatRequest.EveryTurn;
        }
        return McpChatRequest.None;
    }

    private static bool HasScope(IReadOnlyList<string> tokens)
    {
        foreach (var scope in Scopes)
            if (Contains(tokens, scope)) return true;
        return false;
    }

    private static bool HasVerb(IReadOnlyList<string> tokens) =>
        AnyToken(tokens, Verbs) || (AnyToken(tokens, SwitchVerbs) && AnyToken(tokens, SwitchStates));

    private static bool HasNegation(IReadOnlyList<string> tokens) => AnyToken(tokens, Negations);

    /// <summary>Whether the words of <paramref name="phrase"/> appear in order. Every word is matched by
    /// <see cref="Matches"/>, so "in this chat's terms" and "در این چتم" read as the phrase they contain.</summary>
    private static bool Contains(IReadOnlyList<string> tokens, IReadOnlyList<string> phrase)
    {
        for (var start = 0; start + phrase.Count <= tokens.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < phrase.Count && matched; offset++)
                matched = Matches(tokens[start + offset], phrase[offset]);
            if (matched) return true;
        }
        return false;
    }

    private static bool AnyToken(IReadOnlyList<string> tokens, IReadOnlyList<string> words)
    {
        foreach (var token in tokens)
            foreach (var word in words)
                if (Matches(token, word)) return true;
        return false;
    }

    /// <summary>Persian and Arabic attach their suffixes to the stem and join a compound with a zero-width non-joiner,
    /// so a word in that script is matched as a prefix and whatever follows it is left to be a suffix: "استفاده" has
    /// to read "استفادهاش" and "استفادهکن". Latin words are short and a prefix would reach their neighbours — "use"
    /// sits inside "useful", and "off" inside "offer" — so those are matched whole, with the forms that matter listed
    /// beside them.</summary>
    private static bool Matches(string token, string word) =>
        IsRightToLeft(word)
            ? token.StartsWith(word, StringComparison.Ordinal)
            : string.Equals(token, word, StringComparison.Ordinal);

    private static bool IsRightToLeft(string word)
    {
        foreach (var c in word)
            if (c >= '\u0590' && c <= '\u08FF') return true;
        return false;
    }

    /// <summary>The sentences of a message, so a refusal in one of them is not read as part of a request in another.
    /// Commas are not a sentence end: "in this chat, use MCP" is one instruction with a pause in the middle.</summary>
    private static IEnumerable<string> Sentences(string message) =>
        message.Split(new[] { '.', '!', '?', ';', '\n', '\r', '\u061F', '\u061B' },
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A pasted configuration sample is a quotation, not an instruction. <see cref="McpToolMentions"/> reads
    /// prose the same way for the same reason.</summary>
    private static string WithoutFencedCode(string message)
    {
        var kept = new StringBuilder(message.Length);
        var inFence = false;
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }

    /// <summary>The words of a sentence, lowercased, with apostrophes dropped so "don't" reads as "dont". A
    /// zero-width non-joiner is not a letter, so a Persian compound arrives as the two words it joins. Arabic and
    /// Persian letter variants are folded together: the same keyboard produces "ي" and "ی", and an instruction that
    /// was not understood because of the letter a word was typed with would be the worst kind of miss.</summary>
    private static List<string> Tokens(string sentence)
    {
        var text = sentence.Replace("'", "").Replace("\u2019", "");
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            if (!atEnd && char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;
            tokens.Add(Fold(text.Substring(start, i - start)).ToLowerInvariant());
            start = -1;
        }
        return tokens;
    }

    /// <summary>The Arabic and Persian forms of the same letters, and the harakat Arabic typing inserts: all of them
    /// are the same word to a reader.</summary>
    private static string Fold(string word)
    {
        var folded = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            switch (c)
            {
                case '\u064A': case '\u0649': folded.Append('\u06CC'); break;   // ي / ى → ی
                case '\u0643': folded.Append('\u06A9'); break;                  // ك → ک
                case '\u0629': case '\u06C0': folded.Append('\u0647'); break;   // ة / ۀ → ه
                default:
                    var value = (int)c;
                    if (value >= 0x064B && value <= 0x0652) break;              // harakat are not part of the word
                    folded.Append(c);
                    break;
            }
        }
        return folded.ToString();
    }
}
