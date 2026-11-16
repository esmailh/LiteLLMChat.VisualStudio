using System;

namespace LiteLLM.Core;

/// <summary>Undo is allowed only while the working copy is still the output we recorded.</summary>
public static class CheckpointGuard
{
    public static bool CanRestore(bool expectedExists, string expected, bool currentExists, string current) =>
        expectedExists == currentExists && (!expectedExists || string.Equals(LineText.Normalize(expected), LineText.Normalize(current), StringComparison.Ordinal));
}
