using System;
using System.Globalization;

namespace LiteLLM.Core;

/// <summary>How the extension writes a token count to a person. Kept in Core so the dashboard row, its tooltip and
/// the tests all use the same thresholds; a second formatter in the XAML code-behind would drift from the one the
/// tests pin.</summary>
public static class UsageFormat
{
    /// <summary>The exact count, grouped the invariant way. An invariant separator is deliberate: the number is
    /// quoted in English sentences beside it, and a comma that becomes a decimal comma under a local culture would
    /// turn one million and a half into a number that reads as one point five.</summary>
    public static string Count(long tokens) => Math.Max(0, tokens).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A count short enough for a list row: 845, 1.5K, 12.3K, 1.2M. One decimal is enough to compare two
    /// models at a glance, and the exact number is in the row's tooltip.</summary>
    public static string Short(long tokens)
    {
        if (tokens <= 0) return "0";
        // The upper bounds are just below the next unit's rounding point, so 999,950 reads as 1M rather than as
        // 1000K: a row that says "1000K tokens" is a row nobody trusts to be doing arithmetic.
        if (tokens < 1_000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 999_950) return OneDecimal(tokens / 1_000d) + "K";
        if (tokens < 999_950_000) return OneDecimal(tokens / 1_000_000d) + "M";
        return OneDecimal(tokens / 1_000_000_000d) + "B";
    }

    private static string OneDecimal(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
