using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteLLM.Core;

public enum McpTransportKind
{
    Stdio,
    Http,
    Sse
}

/// <summary>One MCP server as the user configured it. The in-memory environment and header values are plaintext;
/// <see cref="McpSecrets.ForStorage"/> is what protects them before they are written and
/// <see cref="McpSecrets.FromStorage"/> is what brings them back, so no caller has to remember either step.</summary>
public sealed class McpServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the Claude backend is given this server. The LiteLLM backend always uses it when enabled.</summary>
    public bool ExposeToClaude { get; set; } = true;

    /// <summary>"stdio", "http" or "sse". Kept as text so a hand-edited settings file and an imported JSON file use
    /// the same spelling as the other clients.</summary>
    public string Transport { get; set; } = "stdio";

    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new List<string>();
    public string WorkingDirectory { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A bearer token the user typed instead of signing in, DPAPI-protected. Empty means none.</summary>
    public string ProtectedAccessToken { get; set; } = "";
    public bool UseOAuth { get; set; }

    /// <summary>Scopes to request when signing in. Empty means the ones the authorization server advertises.</summary>
    public List<string> Scopes { get; set; } = new List<string>();

    /// <summary>"auto", "modern", "2025-11-25" or "legacy". The override exists because era detection is a probe,
    /// and a server that answers the probe badly must still be reachable.</summary>
    public string ProtocolVersion { get; set; } = "auto";

    /// <summary>Tools this server may run without a confirmation card.</summary>
    public List<string> AllowedTools { get; set; } = new List<string>();

    /// <summary>Tools that are never offered, even if the server lists them.</summary>
    public List<string> DisabledTools { get; set; } = new List<string>();

    /// <summary>Whether this server's <c>readOnlyHint</c> tools skip the card. It only has an effect when the global
    /// <see cref="UserSettings.McpAutoApproveReadOnly"/> is also on: the server can narrow the global choice, never
    /// widen it.</summary>
    public bool AutoApproveReadOnly { get; set; }

    /// <summary>0 means the global default.</summary>
    public int TimeoutSeconds { get; set; }
    public int ConnectTimeoutSeconds { get; set; }

    /// <summary>Empty means every solution. A full path scopes the server to one solution, which is what lets the
    /// Dashboard hold both a global filesystem server and a repository-specific one.</summary>
    public string SolutionRoot { get; set; } = "";

    public McpTransportKind Kind
    {
        get
        {
            var value = (Transport ?? "").Trim();
            if (value.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("streamable", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("streamable-http", StringComparison.OrdinalIgnoreCase))
                return McpTransportKind.Http;
            if (value.Equals("sse", StringComparison.OrdinalIgnoreCase)) return McpTransportKind.Sse;
            // A hand-written entry that names a URL and no command is an HTTP server whatever the type says.
            return string.IsNullOrWhiteSpace(Command) && !string.IsNullOrWhiteSpace(Url)
                ? McpTransportKind.Http
                : McpTransportKind.Stdio;
        }
    }

    public bool AppliesTo(string? solutionRoot)
    {
        if (string.IsNullOrWhiteSpace(SolutionRoot)) return true;
        if (string.IsNullOrWhiteSpace(solutionRoot)) return false;
        return string.Equals(Normalize(SolutionRoot), Normalize(solutionRoot!), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when the server can be connected; otherwise the sentence the Dashboard shows.</summary>
    public string? ValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "The server needs a name.";
        if (Kind == McpTransportKind.Stdio)
            return string.IsNullOrWhiteSpace(Command) ? "A stdio server needs a command to launch." : null;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "An HTTP or SSE server needs an absolute http(s) URL.";
        return null;
    }

    /// <summary>True for a plain-HTTP URL that is not loopback. The Dashboard warns; the connection is still allowed,
    /// because many development servers are reached that way and refusing them would only move the proxy.</summary>
    public bool UsesUnencryptedRemoteHttp()
    {
        return Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == "http" && !uri.IsLoopback;
    }

    /// <summary>True when this entry differs from <paramref name="other"/> in a way that requires a new connection.</summary>
    public bool SameConnectionAs(McpServerConfiguration? other)
    {
        if (other == null) return false;
        return string.Equals(Command, other.Command, StringComparison.Ordinal) &&
               Args.SequenceEqual(other.Args, StringComparer.Ordinal) &&
               string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Url, other.Url, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Transport, other.Transport, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(ProtocolVersion, other.ProtocolVersion, StringComparison.OrdinalIgnoreCase) &&
               UseOAuth == other.UseOAuth &&
               SameDictionary(Environment, other.Environment) &&
               SameDictionary(Headers, other.Headers) &&
               string.Equals(ProtectedAccessToken, other.ProtectedAccessToken, StringComparison.Ordinal);
    }

    public McpServerConfiguration Clone()
    {
        return new McpServerConfiguration
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            ExposeToClaude = ExposeToClaude,
            Transport = Transport,
            Command = Command,
            Args = new List<string>(Args ?? new List<string>()),
            WorkingDirectory = WorkingDirectory,
            Environment = new Dictionary<string, string>(Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Url = Url,
            Headers = new Dictionary<string, string>(Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            ProtectedAccessToken = ProtectedAccessToken,
            UseOAuth = UseOAuth,
            Scopes = new List<string>(Scopes ?? new List<string>()),
            ProtocolVersion = ProtocolVersion,
            AllowedTools = new List<string>(AllowedTools ?? new List<string>()),
            DisabledTools = new List<string>(DisabledTools ?? new List<string>()),
            AutoApproveReadOnly = AutoApproveReadOnly,
            TimeoutSeconds = TimeoutSeconds,
            ConnectTimeoutSeconds = ConnectTimeoutSeconds,
            SolutionRoot = SolutionRoot
        };
    }

    private static bool SameDictionary(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return path.Trim(); }
    }
}
