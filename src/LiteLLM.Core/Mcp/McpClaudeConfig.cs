using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>The document written for one Claude Code run: only the servers the Dashboard shows, with their secrets
/// resolved in memory. It is never written into the repository and never logged; the caller deletes it when the run
/// ends, and <see cref="McpClaudeConfig.SweepStale"/> removes a file a crashed run left behind.</summary>
public sealed class McpClaudeRun
{
    internal McpClaudeRun(JObject document, List<string> allowedTools, bool strict, int servers,
        List<string> notes)
    {
        Document = document;
        AllowedTools = allowedTools;
        Strict = strict;
        Servers = servers;
        Notes = notes;
    }

    public JObject Document { get; }
    /// <summary>The MCP entries merged into <c>--allowedTools</c>. The CLI runs with <c>dontAsk</c>, so anything not
    /// listed here is denied without a prompt — which is why only tools the policy resolves to Allow appear.</summary>
    public IReadOnlyList<string> AllowedTools { get; }
    public bool Strict { get; }
    public int Servers { get; }
    /// <summary>What could not be handed to the CLI, as a sentence for the transcript. Nothing is skipped silently.</summary>
    public IReadOnlyList<string> Notes { get; }
    public bool HasServers => ((JObject?)Document["mcpServers"])?.Count > 0;
}

/// <summary>One run's file. Disposal is the only cleanup the caller has to remember; it is best effort and idempotent
/// because a locked or already-deleted file must not turn a finished turn into a failed one.</summary>
public sealed class McpClaudeLease : IDisposable
{
    private int _disposed;

    internal McpClaudeLease(string path, IReadOnlyList<string> allowedTools, bool strict, int servers)
    {
        Path = path;
        AllowedTools = allowedTools;
        Strict = strict;
        Servers = servers;
    }

    public string Path { get; }
    public IReadOnlyList<string> AllowedTools { get; }
    public bool Strict { get; }
    public int Servers { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // A file that cannot be deleted now is swept on the next launch; saying so would only add noise.
        }
    }
}

/// <summary>Hands the configured servers to the Claude Code CLI without touching its harness.
///
/// The CLI takes the same <c>{"mcpServers": {…}}</c> document other clients use, so the work here is filtering,
/// naming and secrets: only enabled, in-scope, exposed servers are written; a key is sanitized because the CLI builds
/// <c>mcp__key__tool</c> from it; and environment/header placeholders are expanded so the child sees values rather
/// than <c>${TOKEN}</c>. The file lives under <c>%LOCALAPPDATA%</c>, never in the solution.</summary>
public static class McpClaudeConfig
{
    private const int MaxKeyLength = 24;

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiteLLM.VisualStudio", "mcp");

    /// <summary>Builds the document and the allow-list. <paramref name="knownTools"/> is the client's current
    /// snapshot; when a server has not been connected, only its explicit allow-list can contribute entries, and the
    /// CLI will report every other tool of that server as denied.</summary>
    public static McpClaudeRun Prepare(UserSettings settings, string? solutionRoot,
        IEnumerable<McpServerConfiguration> servers, IReadOnlyList<McpToolInfo>? knownTools = null)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var known = knownTools ?? Array.Empty<McpToolInfo>();
        var allowAll = string.Equals(settings.PermissionMode, "allowAll", StringComparison.OrdinalIgnoreCase);
        var autoApprove = settings.McpAutoApproveReadOnly;

        var document = new JObject { ["mcpServers"] = new JObject() };
        var map = (JObject)document["mcpServers"]!;
        var allowed = new List<string>();
        var allowedNames = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var server in servers ?? Enumerable.Empty<McpServerConfiguration>())
        {
            if (server == null || !server.Enabled || !server.ExposeToClaude || !server.AppliesTo(solutionRoot))
                continue;
            if (!seenIds.Add(server.Id)) continue;

            var error = server.ValidationError();
            if (error != null)
            {
                notes.Add("MCP · " + (string.IsNullOrWhiteSpace(server.Name) ? "a server" : server.Name) +
                          " was not given to Claude: " + error);
                continue;
            }

            var key = UniqueKey(server.Name, usedKeys);
            map[key] = Body(server, solutionRoot);
            count++;

            foreach (var tool in known.Where(tool => string.Equals(tool.Server.Id, server.Id, StringComparison.Ordinal)))
            {
                if (McpPolicy.Decide(tool, allowAll, autoApprove) != McpDecision.Allow) continue;
                if (!IsAllowListName(tool.Name))
                {
                    Refuse(notes, refused, server, tool.Name);
                    continue;
                }
                Add(allowed, allowedNames, "mcp__" + key + "__" + tool.Name);
            }

            foreach (var name in server.AllowedTools ?? new List<string>())
            {
                if (McpPolicy.Decide(new McpToolInfo { Server = server, Name = name }, allowAll, autoApprove) !=
                    McpDecision.Allow) continue;
                if (!IsAllowListName(name))
                {
                    Refuse(notes, refused, server, name);
                    continue;
                }
                Add(allowed, allowedNames, "mcp__" + key + "__" + name);
            }

            // Under allow-all the CLI's server-wide pattern is the only way to permit tools this client has never
            // listed. A server with denied tools keeps its explicit list instead: a wildcard would overrule them,
            // and the CLI cannot express "everything except these" in one allow entry.
            if (allowAll && (server.DisabledTools == null || server.DisabledTools.Count == 0))
                Add(allowed, allowedNames, "mcp__" + key + "__*");
        }

        return new McpClaudeRun(document, allowed, settings.McpStrictClaudeConfig, count, notes);
    }

    /// <summary>Writes the run file. A document with no servers is refused rather than written empty: with
    /// <c>--strict-mcp-config</c> an empty file would silently remove the servers the user configured elsewhere.</summary>
    public static McpClaudeLease Write(McpClaudeRun run, string? directory = null)
    {
        if (run == null) throw new ArgumentNullException(nameof(run));
        if (!run.HasServers) throw new InvalidOperationException("There are no MCP servers to give Claude.");
        var path = System.IO.Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory,
            "run-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, run.Document.ToString(Formatting.Indented), new UTF8Encoding(false));
        return new McpClaudeLease(path, run.AllowedTools, run.Strict, run.Servers);
    }

    /// <summary>Deletes run files older than <paramref name="age"/>. Called at startup so a crash between writing
    /// and disposing does not leave a file with resolved secrets on disk indefinitely.</summary>
    public static int SweepStale(TimeSpan age, string? directory = null, DateTimeOffset? now = null)
    {
        var path = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
        if (!Directory.Exists(path)) return 0;
        var cutoff = (now ?? DateTimeOffset.UtcNow) - age;
        var deleted = 0;
        foreach (var file in Directory.GetFiles(path, "run-*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Another instance may be running it; it will be swept later.
            }
        }
        return deleted;
    }

    private static JObject Body(McpServerConfiguration server, string? workspaceRoot)
    {
        var body = new JObject();
        if (server.Kind == McpTransportKind.Stdio)
        {
            body["command"] = Expand(server.Command, workspaceRoot);
            if (server.Args != null && server.Args.Count > 0)
                body["args"] = new JArray(server.Args.Select(arg => Expand(arg, workspaceRoot)));
            if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
                body["cwd"] = Expand(server.WorkingDirectory, workspaceRoot);
            if (server.Environment != null && server.Environment.Count > 0)
                body["env"] = Values(server.Environment, workspaceRoot);
        }
        else
        {
            body["type"] = server.Kind == McpTransportKind.Sse ? "sse" : "http";
            body["url"] = Expand(server.Url, workspaceRoot);
            var headers = server.Headers == null
                ? new JObject()
                : Values(server.Headers, workspaceRoot);
            var token = McpSecrets.Unprotect(server.ProtectedAccessToken);
            if (token.Length > 0 && headers["Authorization"] == null)
                headers["Authorization"] = "Bearer " + token;
            if (headers.Count > 0) body["headers"] = headers;
        }
        return body;
    }

    private static JObject Values(Dictionary<string, string> values, string? workspaceRoot)
    {
        var result = new JObject();
        foreach (var pair in values)
            result[pair.Key] = Expand(pair.Value, workspaceRoot);
        return result;
    }

    /// <summary>Unprotects a stored value before expanding it: a file copied from another account keeps a value that
    /// is not valid DPAPI data, which must reach the child as text rather than fail the run.</summary>
    private static string Expand(string? value, string? workspaceRoot) =>
        McpSecrets.Expand(McpSecrets.Unprotect(value), workspaceRoot);

    private static string UniqueKey(string? name, HashSet<string> used)
    {
        var baseKey = McpToolNaming.Slug(name);
        if (baseKey.Length > MaxKeyLength) baseKey = baseKey.Substring(0, MaxKeyLength);
        var candidate = baseKey;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = baseKey + "_" + (suffix++).ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static void Add(List<string> allowed, HashSet<string> seen, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) return;
        allowed.Add(value);
    }

    /// <summary>Whether a server-provided tool name can be one entry of <c>--allowedTools</c>. That option is a
    /// single comma-separated list with no escaping, so a name carrying a comma, a space, a wildcard or a quote
    /// would add or redefine entries the user never chose. Such a tool is left out, and the run says so.</summary>
    private static bool IsAllowListName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > 128) return false;
        foreach (var c in name)
            if (!((c < 128 && char.IsLetterOrDigit(c)) || c == '_' || c == '-' || c == '.')) return false;
        return true;
    }

    private static void Refuse(List<string> notes, HashSet<string> seen, McpServerConfiguration server, string? name)
    {
        var shown = new string((name ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray());
        if (!seen.Add(server.Id + "\n" + shown)) return;
        notes.Add("MCP · " + server.Name + ": tool \"" + shown + "\" was not allow-listed because its name cannot " +
                  "be written in the CLI's comma-separated tool list.");
    }
}
