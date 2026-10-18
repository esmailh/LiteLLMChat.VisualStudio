using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class McpImportResult
{
    public List<McpServerConfiguration> Servers { get; } = new List<McpServerConfiguration>();
    public List<string> Warnings { get; } = new List<string>();
}

/// <summary>Reads and writes the <c>{"mcpServers": {…}}</c> document that Claude Desktop, Claude Code and Cursor
/// use, so a server the user already configured elsewhere can be brought in without retyping it.
///
/// Import never connects or launches anything. It parses, validates and reports; the caller decides whether to add
/// the result to the settings. Export leaves secrets out unless the caller explicitly asks for them, because an
/// exported file is usually pasted into a chat, a ticket or a repository.</summary>
public static class McpConfigJson
{
    private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "command", "args", "env", "cwd", "workingDirectory", "url", "serverUrl", "httpUrl", "headers", "type",
        "enabled", "disabled", "exposeToClaude", "protocolVersion", "allowedTools", "disabledTools",
        "autoApproveReadOnly", "timeout", "timeoutSeconds", "connectTimeoutSeconds", "solutionRoot",
        "scopes", "useOAuth"
    };

    public static McpImportResult Import(string? json)
    {
        var result = new McpImportResult();
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Warnings.Add("No JSON was supplied.");
            return result;
        }

        JObject root;
        try { root = JObject.Parse(json!); }
        catch (JsonException ex)
        {
            result.Warnings.Add("The JSON could not be read: " + ex.Message);
            return result;
        }

        JObject declared;
        if (root["mcpServers"] is JObject servers) declared = servers;
        else if (LooksLikeServer(root))
        {
            var name = root["name"]?.ToString();
            declared = new JObject { [string.IsNullOrWhiteSpace(name) ? "Imported server" : name!] = root };
        }
        else
        {
            result.Warnings.Add("The JSON has no mcpServers object.");
            return result;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in declared.Properties())
        {
            if (property.Value is not JObject body)
            {
                result.Warnings.Add("'" + property.Name + "' is not an object and was skipped.");
                continue;
            }
            var server = Read(property.Name, body, result.Warnings);
            if (server == null) continue;
            server.Name = UniqueName(server.Name, names);
            result.Servers.Add(server);
        }

        if (result.Servers.Count == 0 && result.Warnings.Count == 0)
            result.Warnings.Add("The JSON declared no servers.");
        return result;
    }

    /// <summary>The Claude-compatible view of the configured servers. Extension keys are omitted by default, so the
    /// output can be pasted into another client as-is.</summary>
    public static string Export(IEnumerable<McpServerConfiguration> servers, bool includeSecrets = false,
        bool includeExtensionFields = false)
    {
        var map = new JObject();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            var body = new JObject();
            if (server.Kind == McpTransportKind.Stdio)
            {
                body["command"] = server.Command;
                if (server.Args.Count > 0) body["args"] = new JArray(server.Args);
                if (server.Environment.Count > 0) body["env"] = SecretObject(server.Environment, includeSecrets);
                if (!string.IsNullOrWhiteSpace(server.WorkingDirectory)) body["cwd"] = server.WorkingDirectory;
            }
            else
            {
                body["type"] = server.Kind == McpTransportKind.Sse ? "sse" : "http";
                body["url"] = server.Url;
                var headers = SecretObject(server.Headers, includeSecrets);
                var token = McpSecrets.Unprotect(server.ProtectedAccessToken);
                if (!string.IsNullOrEmpty(token) && !headers.ContainsKey("Authorization"))
                    headers["Authorization"] = includeSecrets ? "Bearer " + token : "<set>";
                if (headers.Count > 0) body["headers"] = headers;
            }

            if (includeExtensionFields)
            {
                body["enabled"] = server.Enabled;
                body["exposeToClaude"] = server.ExposeToClaude;
                if (!string.Equals(server.ProtocolVersion, "auto", StringComparison.OrdinalIgnoreCase))
                    body["protocolVersion"] = server.ProtocolVersion;
                if (server.AllowedTools.Count > 0) body["allowedTools"] = new JArray(server.AllowedTools);
                if (server.DisabledTools.Count > 0) body["disabledTools"] = new JArray(server.DisabledTools);
                if (server.AutoApproveReadOnly) body["autoApproveReadOnly"] = true;
                if (server.TimeoutSeconds > 0) body["timeoutSeconds"] = server.TimeoutSeconds;
                if (server.ConnectTimeoutSeconds > 0) body["connectTimeoutSeconds"] = server.ConnectTimeoutSeconds;
                if (!string.IsNullOrWhiteSpace(server.SolutionRoot)) body["solutionRoot"] = server.SolutionRoot;
                if (server.UseOAuth) body["useOAuth"] = true;
                if (server.Scopes.Count > 0) body["scopes"] = new JArray(server.Scopes);
            }

            map[UniqueName(server.Name, names)] = body;
        }
        return new JObject { ["mcpServers"] = map }.ToString(Formatting.Indented);
    }

    private static McpServerConfiguration? Read(string name, JObject body, List<string> warnings)
    {
        var command = Text(body["command"]);
        var url = FirstText(body, "url", "serverUrl", "httpUrl");
        var type = Text(body["type"]);

        string transport;
        if (!string.IsNullOrWhiteSpace(command)) transport = "stdio";
        else if (!string.IsNullOrWhiteSpace(url))
            transport = type.IndexOf("sse", StringComparison.OrdinalIgnoreCase) >= 0 ? "sse" : "http";
        else
        {
            warnings.Add("'" + name + "' has neither a command nor a URL and was skipped.");
            return null;
        }

        var server = new McpServerConfiguration
        {
            Name = name,
            Transport = transport,
            Command = command,
            Url = url,
            WorkingDirectory = Text(body["cwd"] ?? body["workingDirectory"]),
            ProtocolVersion = string.IsNullOrWhiteSpace(Text(body["protocolVersion"])) ? "auto" : Text(body["protocolVersion"])
        };

        foreach (var arg in Strings(body["args"], name, "args", warnings)) server.Args.Add(arg);
        server.Environment = ReadDictionary(body["env"], name, "env", warnings);
        server.Headers = ReadDictionary(body["headers"], name, "headers", warnings);

        if (body["enabled"]?.Type == JTokenType.Boolean) server.Enabled = body["enabled"]!.Value<bool>();
        if (body["disabled"]?.Type == JTokenType.Boolean && body["disabled"]!.Value<bool>()) server.Enabled = false;
        if (body["exposeToClaude"]?.Type == JTokenType.Boolean) server.ExposeToClaude = body["exposeToClaude"]!.Value<bool>();
        if (body["autoApproveReadOnly"]?.Type == JTokenType.Boolean) server.AutoApproveReadOnly = body["autoApproveReadOnly"]!.Value<bool>();
        if (body["useOAuth"]?.Type == JTokenType.Boolean) server.UseOAuth = body["useOAuth"]!.Value<bool>();

        // A scope list is an array in this client's export and a space-separated string in several other clients'
        // documents; both are accepted because an imported file is usually not ours.
        if (body["scopes"]?.Type == JTokenType.String)
        {
            foreach (var scope in body.Value<string>("scopes")!.Split(new[] { ' ', '\t', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
                server.Scopes.Add(scope);
        }
        else
        {
            foreach (var scope in Strings(body["scopes"], name, "scopes", warnings)) server.Scopes.Add(scope);
        }

        foreach (var tool in Strings(body["allowedTools"], name, "allowedTools", warnings)) server.AllowedTools.Add(tool);
        foreach (var tool in Strings(body["disabledTools"], name, "disabledTools", warnings)) server.DisabledTools.Add(tool);

        if (body["timeoutSeconds"]?.Type == JTokenType.Integer) server.TimeoutSeconds = body["timeoutSeconds"]!.Value<int>();
        else if (body["timeout"]?.Type == JTokenType.Integer)
        {
            // The other clients spell this in milliseconds. A number small enough to be seconds is read as seconds
            // rather than as a one-second timeout the user did not ask for.
            var timeout = body["timeout"]!.Value<long>();
            server.TimeoutSeconds = (int)Math.Max(1, timeout >= 1000 ? timeout / 1000 : timeout);
        }
        if (body["connectTimeoutSeconds"]?.Type == JTokenType.Integer)
            server.ConnectTimeoutSeconds = body["connectTimeoutSeconds"]!.Value<int>();
        server.SolutionRoot = Text(body["solutionRoot"]);

        foreach (var property in body.Properties())
            if (!KnownKeys.Contains(property.Name))
                warnings.Add("'" + name + "': unknown key '" + property.Name + "' was ignored.");
        return server;
    }

    private static JObject SecretObject(Dictionary<string, string> values, bool includeSecrets)
    {
        var result = new JObject();
        foreach (var pair in values)
            result[pair.Key] = includeSecrets || !McpSecrets.LooksSecret(pair.Key) ? pair.Value : "<set>";
        return result;
    }

    private static List<string> Strings(JToken? token, string server, string key, List<string> warnings)
    {
        var result = new List<string>();
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token.Type == JTokenType.String)
        {
            result.Add(token.Value<string>() ?? "");
            return result;
        }
        if (token is not JArray array)
        {
            warnings.Add("'" + server + "': '" + key + "' is not an array and was ignored.");
            return result;
        }
        foreach (var item in array)
        {
            if (item.Type == JTokenType.String) result.Add(item.Value<string>() ?? "");
            else if (item.Type == JTokenType.Integer || item.Type == JTokenType.Float ||
                     item.Type == JTokenType.Boolean)
                result.Add(item.ToString());
            else warnings.Add("'" + server + "': a non-string value in '" + key + "' was ignored.");
        }
        return result;
    }

    private static Dictionary<string, string> ReadDictionary(JToken? token, string server, string key,
        List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token is not JObject obj)
        {
            warnings.Add("'" + server + "': '" + key + "' is not an object and was ignored.");
            return result;
        }
        foreach (var property in obj.Properties())
        {
            if (property.Value is JValue value)
                result[property.Name] = value.Type == JTokenType.Null ? "" : value.ToString(CultureInfo.InvariantCulture);
            else
                warnings.Add("'" + server + "': '" + key + "." + property.Name + "' is not a value and was ignored.");
        }
        return result;
    }

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? "" : token.Type == JTokenType.String ? token.Value<string>() ?? "" : token.ToString();

    private static string FirstText(JObject body, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Text(body[name]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static bool LooksLikeServer(JObject body) =>
        body["command"] != null || body["url"] != null || body["serverUrl"] != null || body["httpUrl"] != null;

    private static string UniqueName(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "MCP server" : name.Trim();
        var candidate = baseName;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = baseName + " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
        return candidate;
    }
}
