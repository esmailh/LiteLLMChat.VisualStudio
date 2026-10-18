using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LiteLLM.Core;

/// <summary>Secret handling for MCP configuration: DPAPI at rest, environment/workspace expansion on the way out,
/// and redaction on the way to a log or a copied request.
///
/// The in-memory configuration is deliberately plaintext. Protecting and unprotecting live here rather than in the
/// dialog, the session or the exporter, because a secret that one of those forgets to protect is written to
/// <c>settings.json</c> in the clear and nothing reports it.</summary>
public static class McpSecrets
{
    private static readonly Regex Placeholder = new Regex(@"\$\{([^}]+)\}", RegexOptions.Compiled);
    // Deliberately broad: hiding a harmless value costs a line of diagnostics, while showing a token costs the token.
    private static readonly Regex SecretName = new Regex(
        @"token|key|secret|password|passwd|auth|cookie|credential|session|bearer",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Protect(string value) => new ApiKeyProtector().Protect(value ?? "");

    /// <summary>Unprotects a value written by <see cref="Protect"/>. A value that is not valid DPAPI data is returned
    /// unchanged: a hand-edited settings file, or a file copied from another Windows account, must make the setting
    /// readable as text rather than throw while the whole settings document is loading.</summary>
    public static string Unprotect(string? value)
    {
        if (value == null || value.Length == 0) return "";
        try { return new ApiKeyProtector().Unprotect(value); }
        catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is ArgumentException) { return value; }
    }

    public static Dictionary<string, string> ProtectAll(Dictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;
        foreach (var pair in values) result[pair.Key] = Protect(pair.Value);
        return result;
    }

    public static Dictionary<string, string> UnprotectAll(Dictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;
        foreach (var pair in values) result[pair.Key] = Unprotect(pair.Value);
        return result;
    }

    /// <summary>The copy written to settings.json: environment and header values protected, the bearer token left as
    /// it already is. Nothing else about the server changes, and the live object is not mutated.</summary>
    public static McpServerConfiguration ForStorage(McpServerConfiguration server)
    {
        var copy = server.Clone();
        copy.Environment = ProtectAll(server.Environment);
        copy.Headers = ProtectAll(server.Headers);
        return copy;
    }

    /// <summary>Reverses <see cref="ForStorage"/> in place on a freshly deserialized object. A collection that came
    /// back as JSON null is repaired here as well, so no caller has to carry a null check for a list the dialog and
    /// the session both assume exists.</summary>
    public static void FromStorage(McpServerConfiguration server)
    {
        server.Args ??= new List<string>();
        server.AllowedTools ??= new List<string>();
        server.DisabledTools ??= new List<string>();
        server.Environment = UnprotectAll(server.Environment);
        server.Headers = UnprotectAll(server.Headers);
    }

    /// <summary>Resolves <c>${VAR}</c> from the process environment and <c>${workspaceFolder}</c> from the solution
    /// root. An unresolved name becomes an empty string rather than the literal placeholder, so a server is never
    /// handed the text <c>${GITHUB_TOKEN}</c> as if it were a token.</summary>
    public static string Expand(string? value, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Placeholder.Replace(value, match =>
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Equals("workspaceFolder", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("workspaceRoot", StringComparison.OrdinalIgnoreCase))
                return workspaceRoot ?? "";
            return Environment.GetEnvironmentVariable(name) ?? "";
        });
    }

    /// <summary>What a log or a row may show for one value. The name decides: an environment variable called
    /// <c>PATH</c> is worth reading, one called <c>GITHUB_TOKEN</c> is not.</summary>
    public static string RedactValue(string? key, string? value)
    {
        if (value == null || value.Length == 0) return "";
        return LooksSecret(key) ? "<redacted>" : value;
    }

    public static bool LooksSecret(string? key) => SecretName.IsMatch(key ?? "");

    public static string Describe(McpServerConfiguration server)
    {
        if (server.Kind == McpTransportKind.Stdio)
            return server.Command + (server.Args.Count > 0 ? " " + string.Join(" ", server.Args) : "");
        return server.Url;
    }
}
