using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpOAuthTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Challenge_ReadsTheResourceMetadataUrlAndScope()
    {
        var challenge = McpOAuthChallenge.Parse(
            "Bearer realm=\"mcp\", resource_metadata=\"https://auth.example/.well-known/oauth-protected-resource\", scope=\"files.read files.write\"");

        Assert.NotNull(challenge);
        Assert.True(challenge!.IsBearer);
        Assert.Equal("https://auth.example/.well-known/oauth-protected-resource", challenge.ResourceMetadata);
        Assert.Equal("files.read files.write", challenge.Scope);
    }

    [Fact]
    public void Challenge_IgnoresASchemeThatIsNotBearer()
    {
        Assert.Null(McpOAuthChallenge.Parse("Basic realm=\"mcp\""));
        Assert.Null(McpOAuthChallenge.Parse(null));
    }

    [Fact]
    public void Pkce_MatchesTheRfc7636Vector()
    {
        var pkce = McpPkce.FromVerifier("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", pkce.Challenge);
        Assert.Equal(McpPkce.Method, "S256");
    }

    [Fact]
    public void Pkce_CreateProducesAUrlSafeVerifierOfTheRequiredLength()
    {
        var pkce = McpPkce.Create();

        Assert.Equal(43, pkce.Verifier.Length);
        Assert.All(pkce.Verifier, character =>
            Assert.True(char.IsLetterOrDigit(character) || character == '-' || character == '_',
                "The verifier must be base64url."));
        Assert.NotEqual(pkce.Verifier, pkce.Challenge);
    }

    [Fact]
    public void ProtectedResource_WellKnownUriKeepsTheServerPath()
    {
        var uris = McpProtectedResourceMetadata.WellKnownUris(new Uri("https://api.example/mcp"));

        Assert.Equal("https://api.example/.well-known/oauth-protected-resource/mcp", uris[0].AbsoluteUri);
        Assert.Equal("https://api.example/.well-known/oauth-protected-resource", uris[1].AbsoluteUri);
    }

    [Fact]
    public void AuthorizationServer_WellKnownUrisCoverRfc8414AndOidc()
    {
        var uris = McpAuthorizationServerMetadata.WellKnownUris(new Uri("https://auth.example/tenant"));

        Assert.Contains(uris, uri => uri.AbsoluteUri ==
                                     "https://auth.example/.well-known/oauth-authorization-server/tenant");
        Assert.Contains(uris, uri => uri.AbsoluteUri ==
                                     "https://auth.example/tenant/.well-known/openid-configuration");
        Assert.Contains(uris, uri => uri.AbsoluteUri ==
                                     "https://auth.example/.well-known/openid-configuration");
    }

    [Fact]
    public void Metadata_ReadsTheEndpointsAndPKCEMethods()
    {
        var metadata = McpAuthorizationServerMetadata.From(JObject.Parse(@"{
            ""issuer"": ""https://auth.example"",
            ""authorization_endpoint"": ""https://auth.example/authorize"",
            ""token_endpoint"": ""https://auth.example/token"",
            ""registration_endpoint"": ""https://auth.example/register"",
            ""scopes_supported"": [""files.read"", ""files.write""],
            ""code_challenge_methods_supported"": [""S256""]
        }"));

        Assert.Equal("https://auth.example/authorize", metadata.AuthorizationEndpoint);
        Assert.Equal("https://auth.example/token", metadata.TokenEndpoint);
        Assert.Equal("https://auth.example/register", metadata.RegistrationEndpoint);
        Assert.Equal(new[] { "files.read", "files.write" }, metadata.ScopesSupported);
        Assert.True(metadata.SupportsS256);
    }

    [Fact]
    public void Metadata_TreatsAnAbsentMethodListAsPKCESupport()
    {
        var metadata = McpAuthorizationServerMetadata.From(new JObject());

        Assert.True(metadata.SupportsS256);
    }

    [Fact]
    public void ClientMetadata_RegistersAPublicClientThatCanRefresh()
    {
        var body = McpOAuthRequests.ClientMetadata("http://127.0.0.1:5000/callback", "files.read");

        Assert.Equal("none", body["token_endpoint_auth_method"]!.Value<string>());
        Assert.Equal("http://127.0.0.1:5000/callback", body["redirect_uris"]![0]!.Value<string>());
        Assert.Contains("refresh_token", body["grant_types"]!.Select(token => token.Value<string>()));
        Assert.Equal("files.read", body["scope"]!.Value<string>());
    }

    [Fact]
    public void AuthorizationUrl_CarriesTheStateVerifierAndResource()
    {
        var metadata = new McpAuthorizationServerMetadata
        {
            AuthorizationEndpoint = "https://auth.example/authorize?tenant=one",
            TokenEndpoint = "https://auth.example/token"
        };

        var url = McpOAuthRequests.AuthorizationUrl(metadata, "client-1", "http://127.0.0.1:5000/callback",
            "state-1", "challenge-1", "https://api.example/mcp", "files.read");

        var query = ParseQuery(url.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-1", query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/callback", query["redirect_uri"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("https://api.example/mcp", query["resource"]);
        Assert.Equal("files.read", query["scope"]);
        Assert.Equal("one", query["tenant"]);
    }

    [Fact]
    public void TokenResponse_ComputesTheExpiryAndKeepsWhatTheServerOmitted()
    {
        var previous = new McpOAuthToken
        {
            AccessToken = "old",
            RefreshToken = "refresh-old",
            Scope = "files.read",
            ClientId = "client-1",
            TokenEndpoint = "https://auth.example/token"
        };

        var token = McpOAuthToken.FromResponse(JObject.Parse(
            @"{""access_token"":""new"",""token_type"":""bearer"",""expires_in"":3600}"), previous, Now,
            "https://api.example/mcp");

        Assert.Equal("new", token.AccessToken);
        Assert.Equal("refresh-old", token.RefreshToken);
        Assert.Equal("files.read", token.Scope);
        Assert.Equal("client-1", token.ClientId);
        Assert.Equal(Now.AddSeconds(3600), token.ExpiresAtUtc);
        Assert.Equal("https://api.example/mcp", token.Resource);
    }

    [Fact]
    public void TokenResponse_IgnoresAnExpiryThatIsNotANumber()
    {
        var token = McpOAuthToken.FromResponse(new JObject
        {
            ["access_token"] = "new",
            ["expires_in"] = "soon"
        }, null, Now, null);

        Assert.Null(token.ExpiresAtUtc);
        Assert.False(token.NeedsRefresh(Now.AddYears(1)));
    }

    [Fact]
    public void NeedsRefresh_UsesTheSixtySecondSkew()
    {
        var token = new McpOAuthToken { ExpiresAtUtc = Now.AddSeconds(120) };

        Assert.True(token.NeedsRefresh(Now.AddSeconds(60)));
        Assert.False(token.NeedsRefresh(Now.AddSeconds(59)));
        Assert.False(new McpOAuthToken().NeedsRefresh(Now));
    }

    [Fact]
    public async Task Discover_ReadsBothWellKnownDocuments()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://api.example/.well-known/oauth-protected-resource")
                return Json(@"{
                    ""resource"": ""https://api.example/mcp"",
                    ""authorization_servers"": [""https://auth.example""],
                    ""scopes_supported"": [""files.read""]
                }");
            if (uri == "https://auth.example/.well-known/oauth-authorization-server")
                return Json(@"{
                    ""issuer"": ""https://auth.example"",
                    ""authorization_endpoint"": ""https://auth.example/authorize"",
                    ""token_endpoint"": ""https://auth.example/token"",
                    ""registration_endpoint"": ""https://auth.example/register""
                }");
            return Json("{}", HttpStatusCode.NotFound);
        });

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var discovery = await client.DiscoverAsync(Server(), null, CancellationToken.None);

            Assert.Equal("https://api.example/mcp", discovery.Resource.AbsoluteUri);
            Assert.Equal("https://auth.example", discovery.AuthorizationServer);
            Assert.Equal("files.read", discovery.Scope);
            Assert.Equal("https://auth.example/token", discovery.Metadata.TokenEndpoint);
        }
    }

    [Fact]
    public async Task Discover_IgnoresAnInsecureMetadataHintAndUsesTheWellKnownPath()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://api.example/.well-known/oauth-protected-resource")
                return Json(@"{""authorization_servers"":[""https://auth.example""]}");
            if (uri == "https://auth.example/.well-known/oauth-authorization-server")
                return Json(@"{
                    ""authorization_endpoint"": ""https://auth.example/authorize"",
                    ""token_endpoint"": ""https://auth.example/token""
                }");
            return Json("{}", HttpStatusCode.NotFound);
        });

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            await client.DiscoverAsync(Server(),
                "Bearer resource_metadata=\"http://evil.example/metadata\"", CancellationToken.None);

            Assert.DoesNotContain(handler.Requests,
                request => request.Uri.IndexOf("evil.example", StringComparison.Ordinal) >= 0);
        }
    }

    [Fact]
    public async Task Discover_FallsBackToTheServerOriginWhenNoResourceMetadataExists()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://api.example/.well-known/oauth-authorization-server")
                return Json(@"{
                    ""issuer"": ""https://api.example"",
                    ""authorization_endpoint"": ""https://api.example/authorize"",
                    ""token_endpoint"": ""https://api.example/token""
                }");
            return Json("{}", HttpStatusCode.NotFound);
        });

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var discovery = await client.DiscoverAsync(Server(), null, CancellationToken.None);

            Assert.Equal("https://api.example", discovery.AuthorizationServer);
            Assert.Equal("https://api.example/authorize", discovery.Metadata.AuthorizationEndpoint);
        }
    }

    [Fact]
    public async Task Begin_RegistersAPublicClientAndBuildsTheAuthorizationUrl()
    {
        var handler = new FakeHandler(request =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://auth.example/register"
                ? Json(@"{""client_id"":""client-new""}")
                : Json("{}", HttpStatusCode.NotFound));

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var authorization = await client.BeginAsync(Discovery(), "http://127.0.0.1:5000/callback", null,
                CancellationToken.None);

            Assert.True(authorization.RegisteredDynamically);
            Assert.Equal("client-new", authorization.ClientId);
            Assert.Equal(43, authorization.Pkce.Verifier.Length);
            var query = ParseQuery(authorization.AuthorizationUrl.Query);
            Assert.Equal("client-new", query["client_id"]);
            Assert.Equal(authorization.State, query["state"]);
            Assert.Equal(authorization.Pkce.Challenge, query["code_challenge"]);
            Assert.Equal("https://api.example/mcp", query["resource"]);
        }
    }

    [Fact]
    public async Task Begin_RefusesAServerThatDoesNotOfferS256()
    {
        var discovery = Discovery();
        discovery.Metadata.CodeChallengeMethodsSupported = new[] { "plain" };

        using (var client = new McpOAuthClient(new HttpClient(new FakeHandler(_ => Json("{}"))), () => Now))
        {
            var error = await Assert.ThrowsAsync<McpException>(
                () => client.BeginAsync(discovery, "http://127.0.0.1:5000/callback", "client-1",
                    CancellationToken.None));

            Assert.True(error.AuthRequired);
            Assert.Contains("S256", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Exchange_PostsTheCodeAndVerifierAndKeepsTheClient()
    {
        string? form = null;
        var handler = new FakeHandler(request =>
        {
            form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(@"{""access_token"":""access-1"",""refresh_token"":""refresh-1"",
                          ""expires_in"":3600,""token_type"":""Bearer""}");
        });

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var authorization = new McpOAuthAuthorization
            {
                ClientId = "client-1",
                RedirectUri = "http://127.0.0.1:5000/callback",
                State = "state-1",
                Pkce = McpPkce.FromVerifier("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
                TokenEndpoint = "https://auth.example/token",
                Resource = "https://api.example/mcp",
                AuthorizationServer = "https://auth.example"
            };

            var token = await client.ExchangeAsync(authorization, "the-code", CancellationToken.None);

            Assert.Equal("access-1", token.AccessToken);
            Assert.Equal("refresh-1", token.RefreshToken);
            Assert.Equal("client-1", token.ClientId);
            Assert.Equal("https://auth.example/token", token.TokenEndpoint);
            Assert.Equal(Now.AddSeconds(3600), token.ExpiresAtUtc);

            var posted = ParseQuery("?" + form);
            Assert.Equal("authorization_code", posted["grant_type"]);
            Assert.Equal("the-code", posted["code"]);
            Assert.Equal(authorization.Pkce.Verifier, posted["code_verifier"]);
            Assert.Equal("client-1", posted["client_id"]);
            Assert.Equal("https://api.example/mcp", posted["resource"]);
        }
    }

    [Fact]
    public async Task Refresh_PostsTheGrantAndKeepsTheStoredRefreshToken()
    {
        string? form = null;
        var handler = new FakeHandler(request =>
        {
            form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(@"{""access_token"":""access-2"",""expires_in"":1800}");
        });

        var previous = new McpOAuthToken
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ClientId = "client-1",
            TokenEndpoint = "https://auth.example/token",
            Resource = "https://api.example/mcp",
            Scope = "files.read"
        };

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var refreshed = await client.RefreshAsync(previous, CancellationToken.None);

            Assert.Equal("access-2", refreshed.AccessToken);
            Assert.Equal("refresh-1", refreshed.RefreshToken);
            Assert.Equal("client-1", refreshed.ClientId);
            var posted = ParseQuery("?" + form);
            Assert.Equal("refresh_token", posted["grant_type"]);
            Assert.Equal("refresh-1", posted["refresh_token"]);
        }
    }

    [Fact]
    public async Task Refresh_MarksAnInvalidGrant()
    {
        var handler = new FakeHandler(_ => Json(
            @"{""error"":""invalid_grant"",""error_description"":""the grant expired""}", HttpStatusCode.BadRequest));

        var previous = new McpOAuthToken
        {
            RefreshToken = "refresh-1",
            ClientId = "client-1",
            TokenEndpoint = "https://auth.example/token"
        };

        using (var client = new McpOAuthClient(new HttpClient(handler), () => Now))
        {
            var error = await Assert.ThrowsAsync<McpException>(
                () => client.RefreshAsync(previous, CancellationToken.None));

            Assert.True(error.AuthRequired);
            Assert.True(error.InvalidGrant);
            Assert.Contains("the grant expired", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Refresh_RejectsATokenWithNothingToRefreshWith()
    {
        using (var client = new McpOAuthClient(new HttpClient(new FakeHandler(_ => Json("{}"))), () => Now))
        {
            var error = await Assert.ThrowsAsync<McpException>(
                () => client.RefreshAsync(new McpOAuthToken(), CancellationToken.None));

            Assert.True(error.AuthRequired);
        }
    }

    [Fact]
    public void TokenStore_RoundTripsThroughDiskWithoutPlaintextSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-mcp-token-tests", Guid.NewGuid().ToString("N"),
            "mcp-tokens.json");
        try
        {
            var token = new McpOAuthToken
            {
                AccessToken = "access-secret",
                RefreshToken = "refresh-secret",
                ExpiresAtUtc = Now.AddHours(1),
                ClientId = "client-1",
                TokenEndpoint = "https://auth.example/token",
                Resource = "https://api.example/mcp",
                Scope = "files.read"
            };

            var store = new McpTokenStore(path);
            store.Set("https://API.example/MCP", token);

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("access-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-secret", text, StringComparison.Ordinal);

            var reloaded = new McpTokenStore(path).Get("https://api.example");
            Assert.NotNull(reloaded);
            Assert.Equal("access-secret", reloaded!.AccessToken);
            Assert.Equal("refresh-secret", reloaded.RefreshToken);
            Assert.Equal("client-1", reloaded.ClientId);
            Assert.Equal(Now.AddHours(1), reloaded.ExpiresAtUtc);
        }
        finally
        {
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TokenStore_RemoveDropsTheEntryAndSaysWhetherItDid()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-mcp-token-tests", Guid.NewGuid().ToString("N"),
            "mcp-tokens.json");
        try
        {
            var store = new McpTokenStore(path);
            store.Set("https://api.example", new McpOAuthToken { AccessToken = "access-1" });

            Assert.True(store.Remove("https://api.example"));
            Assert.False(store.Remove("https://api.example"));
            Assert.Null(store.Get("https://api.example"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TokenStore_TreatsADamagedFileAsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "litellm-mcp-token-tests", Guid.NewGuid().ToString("N"),
            "mcp-tokens.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");

            var store = new McpTokenStore(path);

            Assert.Empty(store.Keys);
            Assert.Null(store.Get("https://api.example"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void KeyOf_IsTheOriginAndIgnoresThePath()
    {
        Assert.Equal("https://api.example:8443",
            McpTokenStore.KeyOf("https://API.example:8443/mcp/v2?x=1"));
        Assert.Equal("http://127.0.0.1:5000", McpTokenStore.KeyOf("http://127.0.0.1:5000/mcp"));
    }

    [Fact]
    public async Task Loopback_DeliversTheCodeForTheMatchingState()
    {
        using (var loopback = new McpLoopback())
        {
            var state = McpOAuthClient.NewState();
            var waiting = loopback.WaitAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
            await SendAsync(loopback.Port, "/callback?code=the-code&state=" + state);

            var callback = await waiting;
            Assert.True(callback.IsSuccess);
            Assert.Equal("the-code", callback.Code);
            Assert.Equal(state, callback.State);
        }
    }

    [Fact]
    public async Task Loopback_IgnoresARequestWithAnotherState()
    {
        using (var loopback = new McpLoopback())
        {
            var state = McpOAuthClient.NewState();
            var waiting = loopback.WaitAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
            await SendAsync(loopback.Port, "/callback?code=attacker&state=another");
            await SendAsync(loopback.Port, "/callback?code=the-code&state=" + state);

            Assert.Equal("the-code", (await waiting).Code);
        }
    }

    [Fact]
    public async Task Loopback_ReturnsAnErrorCallbackInsteadOfATimeout()
    {
        using (var loopback = new McpLoopback())
        {
            var state = McpOAuthClient.NewState();
            var waiting = loopback.WaitAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
            await SendAsync(loopback.Port,
                "/callback?error=access_denied&error_description=no&state=" + state);

            var callback = await waiting;
            Assert.False(callback.IsSuccess);
            Assert.Equal("access_denied", callback.Error);
        }
    }

    [Fact]
    public async Task Loopback_TimesOutWithAnInstruction()
    {
        using (var loopback = new McpLoopback())
        {
            var error = await Assert.ThrowsAsync<McpException>(() =>
                loopback.WaitAsync("state", TimeSpan.FromMilliseconds(150), CancellationToken.None));

            Assert.True(error.AuthRequired);
            Assert.Contains("did not finish", error.Message, StringComparison.Ordinal);
        }
    }

    private static McpServerConfiguration Server() => new McpServerConfiguration
    {
        Name = "files",
        Url = "https://api.example/mcp",
        Transport = "http"
    };

    private static McpOAuthDiscovery Discovery() => new McpOAuthDiscovery
    {
        Resource = new Uri("https://api.example/mcp"),
        AuthorizationServer = "https://auth.example",
        Scope = "files.read",
        Metadata = new McpAuthorizationServerMetadata
        {
            Issuer = "https://auth.example",
            AuthorizationEndpoint = "https://auth.example/authorize",
            TokenEndpoint = "https://auth.example/token",
            RegistrationEndpoint = "https://auth.example/register",
            ScopesSupported = new[] { "files.read" }
        }
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair.Substring(0, equals));
            var value = equals < 0 ? "" : Uri.UnescapeDataString(pair.Substring(equals + 1));
            result[name] = value;
        }
        return result;
    }

    private static async Task SendAsync(int port, string target)
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            using (var stream = client.GetStream())
            {
                var request = Encoding.ASCII.GetBytes(
                    "GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, 0, request.Length);
                var buffer = new byte[4096];
                while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0) { }
            }
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        internal readonly List<(string Method, string Uri)> Requests = new List<(string Method, string Uri)>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri?.AbsoluteUri ?? ""));
            return Task.FromResult(_respond(request));
        }
    }
}
