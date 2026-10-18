using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>OAuth tokens at rest, one entry per server origin. The access and refresh tokens are DPAPI-protected with
/// the same scope and entropy as the settings; the client id and endpoints are kept in the clear because they are
/// not secrets and the Dashboard has to show which client signed in.
///
/// The store is keyed by origin rather than by server id: two entries pointing at the same host share a grant, and
/// renaming or re-importing a server does not lose it. A token file that cannot be read is treated as empty — a
/// damaged cache must cost a sign-in, not the extension.</summary>
public sealed class McpTokenStore
{
    private const int FormatVersion = 1;

    private readonly object _gate = new object();
    private readonly string _path;
    private Dictionary<string, McpOAuthToken>? _tokens;

    public McpTokenStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiteLLM.VisualStudio", "mcp-tokens.json");

    /// <summary>The origin a server's token belongs to: scheme, host and port, lowercased. A path is deliberately
    /// left out, so moving a server from <c>/mcp</c> to <c>/v2/mcp</c> does not silently sign the user out.</summary>
    public static string KeyOf(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return (url ?? "").Trim().ToLowerInvariant();
    }

    public static string KeyOf(McpServerConfiguration server) => KeyOf(server.Url);

    public McpOAuthToken? Get(string key)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _tokens!.TryGetValue(Normalize(key), out var token) ? token.Clone() : null;
        }
    }

    public void Set(string key, McpOAuthToken token)
    {
        if (token == null) throw new ArgumentNullException(nameof(token));
        lock (_gate)
        {
            EnsureLoadedLocked();
            _tokens![Normalize(key)] = token.Clone();
            SaveLocked();
        }
    }

    /// <summary>True when an entry was removed. The caller uses it to tell "signed out" from "there was nothing to
    /// remove", which is what a stale refresh failure should not claim to be.</summary>
    public bool Remove(string key)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_tokens!.Remove(Normalize(key))) return false;
            SaveLocked();
            return true;
        }
    }

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return new List<string>(_tokens!.Keys);
            }
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _tokens = null;
            EnsureLoadedLocked();
        }
    }

    private static string Normalize(string key) => KeyOf(key);

    private void EnsureLoadedLocked()
    {
        if (_tokens != null) return;
        _tokens = new Dictionary<string, McpOAuthToken>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path)) return;
            var root = ParseObject(File.ReadAllText(_path));
            if (!(root?["tokens"] is JObject tokens)) return;
            foreach (var property in tokens.Properties())
            {
                if (!(property.Value is JObject body)) continue;
                var access = McpSecrets.Unprotect(StringOf(body, "accessToken"));
                if (access.Length == 0) continue;
                _tokens[property.Name] = new McpOAuthToken
                {
                    AccessToken = access,
                    TokenType = StringOf(body, "tokenType") is { Length: > 0 } type ? type : "Bearer",
                    RefreshToken = NullIfEmpty(McpSecrets.Unprotect(StringOf(body, "refreshToken"))),
                    ExpiresAtUtc = InstantOf(StringOf(body, "expiresAt")),
                    Scope = NullIfEmpty(StringOf(body, "scope")),
                    ClientId = NullIfEmpty(StringOf(body, "clientId")),
                    TokenEndpoint = NullIfEmpty(StringOf(body, "tokenEndpoint")),
                    Resource = NullIfEmpty(StringOf(body, "resource")),
                    AuthorizationServer = NullIfEmpty(StringOf(body, "authorizationServer"))
                };
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            _tokens = new Dictionary<string, McpOAuthToken>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveLocked()
    {
        var tokens = new JObject();
        foreach (var pair in _tokens!)
        {
            var body = new JObject
            {
                ["accessToken"] = McpSecrets.Protect(pair.Value.AccessToken),
                ["tokenType"] = pair.Value.TokenType
            };
            if (!string.IsNullOrEmpty(pair.Value.RefreshToken))
                body["refreshToken"] = McpSecrets.Protect(pair.Value.RefreshToken!);
            if (pair.Value.ExpiresAtUtc.HasValue)
                body["expiresAt"] = pair.Value.ExpiresAtUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(pair.Value.Scope)) body["scope"] = pair.Value.Scope;
            if (!string.IsNullOrEmpty(pair.Value.ClientId)) body["clientId"] = pair.Value.ClientId;
            if (!string.IsNullOrEmpty(pair.Value.TokenEndpoint)) body["tokenEndpoint"] = pair.Value.TokenEndpoint;
            if (!string.IsNullOrEmpty(pair.Value.Resource)) body["resource"] = pair.Value.Resource;
            if (!string.IsNullOrEmpty(pair.Value.AuthorizationServer))
                body["authorizationServer"] = pair.Value.AuthorizationServer;
            tokens[pair.Key] = body;
        }

        var document = new JObject { ["version"] = FormatVersion, ["tokens"] = tokens };
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, document.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(_path)) File.Replace(temporary, _path, null);
        else File.Move(temporary, _path);
    }

    /// <summary>Newtonsoft turns an ISO-8601-looking string into a date token by default. The expiry is read back
    /// as the exact text this store wrote, so date parsing is switched off for the whole document.</summary>
    private static JObject? ParseObject(string text)
    {
        try
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }
        catch (JsonException) { return null; }
    }

    private static string StringOf(JObject body, string name) =>
        body[name]?.Type == JTokenType.String ? body.Value<string>(name) ?? "" : "";

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static DateTimeOffset? InstantOf(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            ? instant
            : (DateTimeOffset?)null;
}
