using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One access token, with everything the refresh needs beside it. Expiry is kept as an instant rather than a
/// lifetime so a token loaded from disk an hour later is still judged against the clock, not against the moment it
/// was written. <see cref="RefreshToken"/> is only replaced when the server rotates it: a response without one is
/// the normal case and does not mean the old one is gone.</summary>
public sealed class McpOAuthToken
{
    /// <summary>Refresh this long before the server's expiry, so a request is not sent with a token that dies in
    /// flight. The plan fixes the skew at one minute.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    public string AccessToken { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string? Scope { get; set; }

    /// <summary>The registered public client and the endpoint it refreshes against. They are part of the token
    /// because the refresh form needs them, and a token read from disk is the only thing a later launch has.</summary>
    public string? ClientId { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? Resource { get; set; }
    public string? AuthorizationServer { get; set; }

    public bool NeedsRefresh(DateTimeOffset now) =>
        ExpiresAtUtc.HasValue && ExpiresAtUtc.Value - RefreshSkew <= now;

    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken) &&
                              !string.IsNullOrEmpty(ClientId) &&
                              !string.IsNullOrEmpty(TokenEndpoint);

    public McpOAuthToken Clone() => new McpOAuthToken
    {
        AccessToken = AccessToken,
        TokenType = TokenType,
        RefreshToken = RefreshToken,
        ExpiresAtUtc = ExpiresAtUtc,
        Scope = Scope,
        ClientId = ClientId,
        TokenEndpoint = TokenEndpoint,
        Resource = Resource,
        AuthorizationServer = AuthorizationServer
    };

    /// <summary>Reads a token endpoint's answer. An <c>expires_in</c> that is missing or nonsensical becomes no
    /// expiry at all: a token that a bad number claimed was dead must not be thrown away, and one it claimed lives
    /// for years must not be trusted either.</summary>
    public static McpOAuthToken FromResponse(JObject response, McpOAuthToken? previous, DateTimeOffset now,
        string? resource)
    {
        var access = response["access_token"]?.Type == JTokenType.String
            ? response.Value<string>("access_token")
            : null;
        if (string.IsNullOrWhiteSpace(access))
            throw new McpException("The authorization server answered without an access token.", authRequired: true);

        var refresh = response["refresh_token"]?.Type == JTokenType.String
            ? response.Value<string>("refresh_token")
            : null;
        var scope = response["scope"]?.Type == JTokenType.String ? response.Value<string>("scope") : null;

        return new McpOAuthToken
        {
            AccessToken = access!,
            TokenType = response["token_type"]?.Type == JTokenType.String
                ? response.Value<string>("token_type")!
                : "Bearer",
            RefreshToken = string.IsNullOrWhiteSpace(refresh) ? previous?.RefreshToken : refresh,
            ExpiresAtUtc = ExpiryOf(response, now),
            Scope = string.IsNullOrWhiteSpace(scope) ? previous?.Scope : scope,
            ClientId = previous?.ClientId,
            TokenEndpoint = previous?.TokenEndpoint,
            Resource = string.IsNullOrWhiteSpace(resource) ? previous?.Resource : resource,
            AuthorizationServer = previous?.AuthorizationServer
        };
    }

    private static DateTimeOffset? ExpiryOf(JObject response, DateTimeOffset now)
    {
        var token = response["expires_in"];
        if (token == null || token.Type == JTokenType.Null) return null;

        double seconds;
        switch (token.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                seconds = token.Value<double>();
                break;
            case JTokenType.String:
                if (!double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out seconds))
                    return null;
                break;
            default:
                return null;
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
        // Ten years is far beyond any real grant and keeps AddSeconds inside DateTimeOffset's range.
        seconds = Math.Max(0, Math.Min(seconds, 10 * 365 * 24 * 60 * 60));
        return now.AddSeconds(seconds);
    }
}

/// <summary>The <c>WWW-Authenticate</c> challenge a 401 carries. The resource-metadata URL names where the server
/// published its authorization server; without it the client falls back to the well-known location.</summary>
public sealed class McpOAuthChallenge
{
    public string Scheme { get; set; } = "";
    public string? ResourceMetadata { get; set; }
    public string? Scope { get; set; }
    public string? Error { get; set; }
    public bool IsBearer => Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase);

    public static McpOAuthChallenge? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var index = header!.IndexOf("Bearer", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        // A second scheme before Bearer would make this a substring of a parameter value; that is harmless here
        // because the parameters we read are validated as URLs or scopes before use.
        var parameters = ParseParameters(header.Substring(index + "Bearer".Length));
        parameters.TryGetValue("resource_metadata", out var metadata);
        parameters.TryGetValue("scope", out var scope);
        parameters.TryGetValue("error", out var error);
        return new McpOAuthChallenge
        {
            Scheme = "Bearer",
            ResourceMetadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata,
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope,
            Error = string.IsNullOrWhiteSpace(error) ? null : error
        };
    }

    private static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && (text[index] == ',' || char.IsWhiteSpace(text[index]))) index++;
            var start = index;
            while (index < text.Length && text[index] != '=' && text[index] != ',') index++;
            if (index >= text.Length) break;
            if (text[index] == ',') continue;

            var name = text.Substring(start, index - start).Trim();
            index++; // the '='
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

            string value;
            if (index < text.Length && text[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        builder.Append(text[index + 1]);
                        index += 2;
                    }
                    else builder.Append(text[index++]);
                }
                if (index < text.Length) index++;
                value = builder.ToString();
            }
            else
            {
                var valueStart = index;
                while (index < text.Length && text[index] != ',' && !char.IsWhiteSpace(text[index])) index++;
                value = text.Substring(valueStart, index - valueStart);
            }

            if (name.Length > 0) result[name] = value;
        }
        return result;
    }
}

/// <summary>RFC 9728 protected-resource metadata. The first authorization server is the one used; the rest are
/// alternatives the user may configure later, not a rotation this client performs.</summary>
public sealed class McpProtectedResourceMetadata
{
    public string? Resource { get; set; }
    public IReadOnlyList<string> AuthorizationServers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopesSupported { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> BearerMethodsSupported { get; set; } = Array.Empty<string>();

    public static McpProtectedResourceMetadata From(JObject body) => new McpProtectedResourceMetadata
    {
        Resource = StringOf(body, "resource"),
        AuthorizationServers = StringsOf(body, "authorization_servers"),
        ScopesSupported = StringsOf(body, "scopes_supported"),
        BearerMethodsSupported = StringsOf(body, "bearer_methods_supported")
    };

    /// <summary>The resource's own path first, then the origin-only document. RFC 9728 inserts the path after the
    /// well-known segment, so <c>https://host/mcp</c> publishes at
    /// <c>https://host/.well-known/oauth-protected-resource/mcp</c>.</summary>
    public static IReadOnlyList<Uri> WellKnownUris(Uri resource)
    {
        var result = new List<Uri>();
        var authority = resource.GetLeftPart(UriPartial.Authority);
        var path = resource.AbsolutePath.Trim('/');
        Add(result, authority + "/.well-known/oauth-protected-resource" + (path.Length > 0 ? "/" + path : ""));
        Add(result, authority + "/.well-known/oauth-protected-resource");
        return result;
    }

    internal static void Add(List<Uri> result, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return;
        if (result.Any(existing => string.Equals(existing.AbsoluteUri, uri.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase)))
            return;
        result.Add(uri);
    }

    internal static string? StringOf(JObject body, string name) =>
        body[name]?.Type == JTokenType.String ? body.Value<string>(name) : null;

    internal static IReadOnlyList<string> StringsOf(JObject body, string name)
    {
        if (!(body[name] is JArray array)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var token in array)
            if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()))
                result.Add(token.Value<string>()!);
        return result;
    }
}

/// <summary>RFC 8414 authorization-server metadata, with the OIDC document accepted as a fallback. A server that
/// advertises an empty <c>code_challenge_methods_supported</c> is treated as supporting S256: the field is optional
/// and older documents omit it, while a server that does not support PKCE will reject the request explicitly.</summary>
public sealed class McpAuthorizationServerMetadata
{
    public string? Issuer { get; set; }
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string? RegistrationEndpoint { get; set; }
    public IReadOnlyList<string> ScopesSupported { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();

    public bool SupportsS256 =>
        CodeChallengeMethodsSupported.Count == 0 ||
        CodeChallengeMethodsSupported.Any(method => method.Equals("S256", StringComparison.OrdinalIgnoreCase));

    public static McpAuthorizationServerMetadata From(JObject body) => new McpAuthorizationServerMetadata
    {
        Issuer = McpProtectedResourceMetadata.StringOf(body, "issuer"),
        AuthorizationEndpoint = McpProtectedResourceMetadata.StringOf(body, "authorization_endpoint") ?? "",
        TokenEndpoint = McpProtectedResourceMetadata.StringOf(body, "token_endpoint") ?? "",
        RegistrationEndpoint = McpProtectedResourceMetadata.StringOf(body, "registration_endpoint"),
        ScopesSupported = McpProtectedResourceMetadata.StringsOf(body, "scopes_supported"),
        CodeChallengeMethodsSupported =
            McpProtectedResourceMetadata.StringsOf(body, "code_challenge_methods_supported"),
        TokenEndpointAuthMethodsSupported =
            McpProtectedResourceMetadata.StringsOf(body, "token_endpoint_auth_methods_supported")
    };

    /// <summary>The issuer's own path first (RFC 8414 inserts it after the well-known segment, OIDC appends it),
    /// then the origin-only documents. Every candidate is tried because a server may publish only one shape.</summary>
    public static IReadOnlyList<Uri> WellKnownUris(Uri issuer)
    {
        var result = new List<Uri>();
        var authority = issuer.GetLeftPart(UriPartial.Authority);
        var path = issuer.AbsolutePath.Trim('/');
        var suffix = path.Length > 0 ? "/" + path : "";
        McpProtectedResourceMetadata.Add(result, authority + "/.well-known/oauth-authorization-server" + suffix);
        McpProtectedResourceMetadata.Add(result, authority + suffix + "/.well-known/openid-configuration");
        McpProtectedResourceMetadata.Add(result, authority + "/.well-known/oauth-authorization-server");
        McpProtectedResourceMetadata.Add(result, authority + "/.well-known/openid-configuration");
        return result;
    }
}

/// <summary>PKCE (RFC 7636). The verifier is 43 base64url characters; the challenge is its SHA-256, also base64url.
/// S256 is the only method used: a server that offers only <c>plain</c> does not get a token from this client.</summary>
public sealed class McpPkce
{
    public const string Method = "S256";

    public string Verifier { get; }
    public string Challenge { get; }

    private McpPkce(string verifier)
    {
        Verifier = verifier;
        Challenge = ChallengeOf(verifier);
    }

    public static McpPkce Create()
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return new McpPkce(Base64Url(bytes));
    }

    /// <summary>Rebuilds the pair from a known verifier. Used by tests and by a resumed flow; the verifier itself is
    /// never persisted, so a refresh cannot reuse one.</summary>
    public static McpPkce FromVerifier(string verifier) => new McpPkce(verifier);

    public static string ChallengeOf(string verifier)
    {
        using (var sha = SHA256.Create())
            return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A discovered resource: who issues its tokens, where, and which scopes to ask for.</summary>
public sealed class McpOAuthDiscovery
{
    public Uri Resource { get; set; } = new Uri("https://localhost/");
    public string AuthorizationServer { get; set; } = "";
    public McpAuthorizationServerMetadata Metadata { get; set; } = new McpAuthorizationServerMetadata();
    public string? Scope { get; set; }
    public McpProtectedResourceMetadata? ProtectedResource { get; set; }
}

/// <summary>A flow that has been started: the URL to open, and everything the callback's code is exchanged with.
/// The verifier and state live only in this object, which is discarded once the exchange finishes.</summary>
public sealed class McpOAuthAuthorization
{
    public Uri AuthorizationUrl { get; set; } = new Uri("https://localhost/");
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
    public McpPkce Pkce { get; set; } = null!;
    public string AuthorizationServer { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string? Resource { get; set; }
    public string? Scope { get; set; }
    public bool RegisteredDynamically { get; set; }
}

/// <summary>OAuth 2.1 discovery, registration, authorization and refresh. Only the endpoints and the small amount of
/// JSON around them live here; opening the browser and waiting on the loopback socket belong to the caller, so this
/// class needs no UI and is testable against an injected <see cref="HttpClient"/>.
///
/// A 401 is never answered by retrying silently: discovery produces the sign-in URL, and the token that comes back
/// is what a later request uses. Manual bearer headers remain the fallback for servers that do not speak OAuth.</summary>
public sealed class McpOAuthClient : IDisposable
{
    public const string ClientName = "LiteLLM for Visual Studio";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<DateTimeOffset> _clock;

    public McpOAuthClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _ownsClient = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Finds the authorization server for <paramref name="server"/>. The metadata URL from the challenge
    /// wins; the well-known path is tried next. When no resource metadata is published the server's own origin is
    /// used as the issuer, which is what a single-purpose OAuth server expects.</summary>
    public async Task<McpOAuthDiscovery> DiscoverAsync(McpServerConfiguration server, string? wwwAuthenticate,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var resource))
            throw new McpException("MCP server '" + server.Name + "' needs an absolute URL before it can sign in.");
        if (resource.Scheme != Uri.UriSchemeHttp && resource.Scheme != Uri.UriSchemeHttps)
            throw new McpException("MCP server '" + server.Name + "' needs an HTTP URL before it can sign in.");

        var challenge = McpOAuthChallenge.Parse(wwwAuthenticate);
        McpProtectedResourceMetadata? published = null;

        var hinted = SafeHttpUri(challenge?.ResourceMetadata);
        if (hinted != null) published = await ReadProtectedResourceAsync(hinted, cancellationToken).ConfigureAwait(false);
        if (published == null)
        {
            foreach (var candidate in McpProtectedResourceMetadata.WellKnownUris(resource))
            {
                published = await ReadProtectedResourceAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (published != null) break;
            }
        }

        var issuerText = published?.AuthorizationServers.FirstOrDefault();
        var issuerUri = SafeHttpUri(issuerText) ?? new Uri(resource.GetLeftPart(UriPartial.Authority));

        McpAuthorizationServerMetadata? metadata = null;
        foreach (var candidate in McpAuthorizationServerMetadata.WellKnownUris(issuerUri))
        {
            var body = await TryGetJsonAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (body == null) continue;
            var candidateMetadata = McpAuthorizationServerMetadata.From(body);
            if (candidateMetadata.AuthorizationEndpoint.Length == 0 || candidateMetadata.TokenEndpoint.Length == 0)
                continue;
            metadata = candidateMetadata;
            break;
        }

        if (metadata == null)
            throw new McpException("MCP server '" + server.Name +
                                   "' did not publish OAuth authorization-server metadata. A manual bearer token can " +
                                   "still be configured.", authRequired: true);

        var configured = server.Scopes ?? new List<string>();
        var scope = configured.Count > 0
            ? string.Join(" ", configured)
            : published != null && published.ScopesSupported.Count > 0
                ? string.Join(" ", published.ScopesSupported)
                : metadata.ScopesSupported.Count > 0 ? string.Join(" ", metadata.ScopesSupported) : null;

        return new McpOAuthDiscovery
        {
            Resource = Uri.TryCreate(published?.Resource, UriKind.Absolute, out var declared) ? declared : resource,
            AuthorizationServer = metadata.Issuer ?? issuerUri.GetLeftPart(UriPartial.Authority),
            Metadata = metadata,
            Scope = scope,
            ProtectedResource = published
        };
    }

    /// <summary>Registers a public client when none is configured, then builds the URL the browser opens. The
    /// registration is repeated on every sign-in: a dynamic client is cheap to create and keeping its id would turn
    /// a deleted registration into a failure the user cannot clear.</summary>
    public async Task<McpOAuthAuthorization> BeginAsync(McpOAuthDiscovery discovery, string redirectUri,
        string? clientId, CancellationToken cancellationToken)
    {
        if (!discovery.Metadata.SupportsS256)
            throw new McpException("The authorization server for '" + discovery.Resource +
                                   "' does not offer PKCE S256, which this client requires.", authRequired: true);

        var id = clientId?.Trim() ?? "";
        var registered = false;
        if (id.Length == 0)
        {
            var endpoint = SafeHttpUri(discovery.Metadata.RegistrationEndpoint);
            if (endpoint == null)
                throw new McpException("The authorization server for '" + discovery.Resource +
                                       "' offers no dynamic client registration, and no client id is configured.",
                    authRequired: true);

            var registration = McpOAuthRequests.ClientMetadata(redirectUri, discovery.Scope);
            var response = await PostJsonAsync(endpoint, registration, cancellationToken).ConfigureAwait(false);
            id = response["client_id"]?.Type == JTokenType.String ? response.Value<string>("client_id")! : "";
            if (id.Length == 0)
                throw new McpException("The authorization server registered the client but returned no client id.",
                    authRequired: true);
            registered = true;
        }

        var pkce = McpPkce.Create();
        var state = NewState();
        var url = McpOAuthRequests.AuthorizationUrl(discovery.Metadata, id, redirectUri, state, pkce.Challenge,
            discovery.Resource.AbsoluteUri, discovery.Scope);

        return new McpOAuthAuthorization
        {
            AuthorizationUrl = url,
            ClientId = id,
            RedirectUri = redirectUri,
            State = state,
            Pkce = pkce,
            AuthorizationServer = discovery.AuthorizationServer,
            TokenEndpoint = discovery.Metadata.TokenEndpoint,
            Resource = discovery.Resource.AbsoluteUri,
            Scope = discovery.Scope,
            RegisteredDynamically = registered
        };
    }

    public async Task<McpOAuthToken> ExchangeAsync(McpOAuthAuthorization authorization, string code,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(authorization.TokenEndpoint, UriKind.Absolute, out var endpoint))
            throw new McpException("The authorization server did not publish a usable token endpoint.");

        var response = await PostFormAsync(endpoint, McpOAuthRequests.CodeForm(code, authorization.RedirectUri,
            authorization.ClientId, authorization.Pkce.Verifier, authorization.Resource), cancellationToken)
            .ConfigureAwait(false);

        var token = McpOAuthToken.FromResponse(response, null, _clock(), authorization.Resource);
        token.ClientId = authorization.ClientId;
        token.TokenEndpoint = authorization.TokenEndpoint;
        token.AuthorizationServer = authorization.AuthorizationServer;
        return token;
    }

    /// <summary>Exchanges a refresh token. An <c>invalid_grant</c> answer means the grant is gone for good, and the
    /// caller is expected to delete the stored token rather than retry.</summary>
    public async Task<McpOAuthToken> RefreshAsync(McpOAuthToken previous, CancellationToken cancellationToken)
    {
        if (!previous.CanRefresh)
            throw new McpException("The stored sign-in cannot be refreshed; sign in again.", authRequired: true);
        if (!Uri.TryCreate(previous.TokenEndpoint, UriKind.Absolute, out var endpoint))
            throw new McpException("The stored token names no usable token endpoint; sign in again.", authRequired: true);

        var response = await PostFormAsync(endpoint, McpOAuthRequests.RefreshForm(previous.RefreshToken!,
            previous.ClientId!, previous.Resource, previous.Scope), cancellationToken).ConfigureAwait(false);

        var token = McpOAuthToken.FromResponse(response, previous, _clock(), previous.Resource);
        token.ClientId = previous.ClientId;
        token.TokenEndpoint = previous.TokenEndpoint;
        token.AuthorizationServer = previous.AuthorizationServer;
        return token;
    }

    /// <summary>48 bytes of randomness, base64url: enough that an attacker cannot guess a state value inside the
    /// sign-in window.</summary>
    public static string NewState() => RandomValue(24);

    public static string RandomValue(int bytes)
    {
        var buffer = new byte[Math.Max(1, bytes)];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<McpProtectedResourceMetadata?> ReadProtectedResourceAsync(Uri uri,
        CancellationToken cancellationToken)
    {
        var body = await TryGetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (body == null) return null;
        var metadata = McpProtectedResourceMetadata.From(body);
        return metadata.AuthorizationServers.Count > 0 ? metadata : null;
    }

    private async Task<JObject?> TryGetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using (var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JObject.Parse(text);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is HttpRequestException ||
                                   ex is JsonException || ex is NotSupportedException)
        {
            // A well-known document that is unreachable is the same as one that does not exist: try the next shape.
            return null;
        }
    }

    private async Task<JObject> PostJsonAsync(Uri uri, JObject body, CancellationToken cancellationToken)
    {
        using (var content = new StringContent(body.ToString(Formatting.None), new UTF8Encoding(false),
                   "application/json"))
        using (var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var parsed = ParseOrEmpty(text);
            if (!response.IsSuccessStatusCode)
                throw new McpException("The authorization server refused client registration (" +
                                       (int)response.StatusCode + "): " + Describe(parsed, text),
                    McpErrorCodes.AuthRequired, parsed, authRequired: true);
            return parsed;
        }
    }

    private async Task<JObject> PostFormAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        using (var content = new FormUrlEncodedContent(form))
        using (var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var parsed = ParseOrEmpty(text);
            if (!response.IsSuccessStatusCode || parsed["error"] != null)
            {
                var error = McpProtectedResourceMetadata.StringOf(parsed, "error");
                var description = McpProtectedResourceMetadata.StringOf(parsed, "error_description");
                var invalidGrant = string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
                var message = error == null
                    ? "The authorization server did not return a token (" + (int)response.StatusCode + "): " +
                      Describe(parsed, text)
                    : "The authorization server reported '" + error + "'" +
                      (string.IsNullOrWhiteSpace(description) ? "." : ": " + description);
                throw new McpException(message, McpErrorCodes.AuthRequired, parsed,
                    authRequired: true, invalidGrant: invalidGrant);
            }
            return parsed;
        }
    }

    private static JObject ParseOrEmpty(string text)
    {
        try { return JObject.Parse(text); }
        catch (JsonException) { return new JObject(); }
    }

    private static string Describe(JObject body, string raw)
    {
        var error = McpProtectedResourceMetadata.StringOf(body, "error");
        var description = McpProtectedResourceMetadata.StringOf(body, "error_description");
        if (!string.IsNullOrWhiteSpace(description)) return description!;
        if (!string.IsNullOrWhiteSpace(error)) return error!;
        var clipped = (raw ?? "").Trim();
        if (clipped.Length == 0) return "no explanation was given";
        return clipped.Length <= 300 ? clipped : clipped.Substring(0, 300) + "…";
    }

    /// <summary>An OAuth endpoint is reached over HTTPS; plain HTTP is allowed only for a loopback development
    /// server, because a token sent over the network in the clear is a token someone else now has.</summary>
    private static Uri? SafeHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri;
        return null;
    }

    /// <summary>Disposes the <see cref="HttpClient"/> only when this instance created it; an injected one belongs to
    /// the caller, who may be sharing it with another client.</summary>
    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

/// <summary>The wire bodies of OAuth 2.1. Kept apart from <see cref="McpOAuthClient"/> so the exact shape the server
/// sees can be asserted without an HTTP conversation.</summary>
public static class McpOAuthRequests
{
    public static JObject ClientMetadata(string redirectUri, string? scope)
    {
        var body = new JObject
        {
            ["client_name"] = McpOAuthClient.ClientName,
            ["redirect_uris"] = new JArray(redirectUri),
            ["grant_types"] = new JArray("authorization_code", "refresh_token"),
            ["response_types"] = new JArray("code"),
            ["token_endpoint_auth_method"] = "none"
        };
        if (!string.IsNullOrWhiteSpace(scope)) body["scope"] = scope;
        return body;
    }

    public static Uri AuthorizationUrl(McpAuthorizationServerMetadata metadata, string clientId, string redirectUri,
        string state, string challenge, string? resource, string? scope)
    {
        if (!Uri.TryCreate(metadata.AuthorizationEndpoint, UriKind.Absolute, out var endpoint))
            throw new McpException("The authorization server's authorization endpoint is not an absolute URL.");

        var parts = new List<string>
        {
            "response_type=code",
            "client_id=" + Escape(clientId),
            "redirect_uri=" + Escape(redirectUri),
            "code_challenge=" + Escape(challenge),
            "code_challenge_method=" + McpPkce.Method,
            "state=" + Escape(state)
        };
        if (!string.IsNullOrWhiteSpace(resource)) parts.Add("resource=" + Escape(resource!));
        if (!string.IsNullOrWhiteSpace(scope)) parts.Add("scope=" + Escape(scope!));

        return new Uri(endpoint.AbsoluteUri + (endpoint.Query.Length > 0 ? "&" : "?") + string.Join("&", parts));
    }

    public static Dictionary<string, string> CodeForm(string code, string redirectUri, string clientId,
        string verifier, string? resource)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(resource)) form["resource"] = resource!;
        return form;
    }

    public static Dictionary<string, string> RefreshForm(string refreshToken, string clientId, string? resource,
        string? scope)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId
        };
        if (!string.IsNullOrWhiteSpace(resource)) form["resource"] = resource!;
        if (!string.IsNullOrWhiteSpace(scope)) form["scope"] = scope!;
        return form;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
