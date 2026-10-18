using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpHttpTransportTests
{
    [Fact]
    public async Task Send_PostsJsonAndQueuesTheResponse()
    {
        var handler = new FakeHandler(request =>
            request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}"));

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None);

            var response = await ReceiveAsync(transport);
            Assert.Equal("ping", handler.PostedBodies()[0]["method"]!.Value<string>());
            Assert.True((bool)response["result"]!["ok"]!);
            Assert.Contains("application/json", handler.LastAccept, StringComparison.Ordinal);
            Assert.Contains("text/event-stream", handler.LastAccept, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Send_ReadsAMessageFromAnEventStreamResponse()
    {
        var handler = new FakeHandler(_ => Sse(
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"from\":\"sse\"}}\n\n"));

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Request(2, "tools/list"), CancellationToken.None);

            var response = await ReceiveAsync(transport);
            Assert.Equal("sse", response["result"]!["from"]!.Value<string>());
        }
    }

    [Fact]
    public async Task Send_AcceptsAnEmptyAcceptedResponseForANotification()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Notification("notifications/initialized"), CancellationToken.None);
            Assert.Equal("notifications/initialized",
                JObject.Parse(handler.Bodies[0]!)["method"]!.Value<string>());
        }
    }

    [Fact]
    public async Task Send_ReportsThatTheServerNeedsAuthentication()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<McpException>(() =>
                transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None));
            Assert.True(error.AuthRequired);
        }
    }

    [Fact]
    public async Task Send_SuggestsTheLegacyTransportWhenTheEndpointIsNotStreamable()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await Assert.ThrowsAsync<McpException>(() =>
                transport.SendAsync(McpJsonRpc.Request(1, "initialize"), CancellationToken.None));
            Assert.True(transport.SuggestsSseFallback);
        }
    }

    [Fact]
    public async Task SendsTheCapturedSessionAndNegotiatedVersionOnLaterRequests()
    {
        var posts = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var response = Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
            if (++posts == 1) response.Headers.TryAddWithoutValidation("MCP-Session-Id", "session-7");
            return response;
        });

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Request(1, "initialize"), CancellationToken.None);
            await ReceiveAsync(transport);

            transport.SetSession(null, "2025-06-18");
            await transport.SendAsync(McpJsonRpc.Request(2, "tools/list"), CancellationToken.None);
            await ReceiveAsync(transport);

            var second = handler.LastPost();
            Assert.Equal("session-7", Header(second, "MCP-Session-Id"));
            Assert.Equal("2025-06-18", Header(second, "MCP-Protocol-Version"));
        }
    }

    [Fact]
    public async Task SendsTheConfiguredHeadersAndTheBearerToken()
    {
        var handler = new FakeHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
        var server = Server();
        server.Headers["X-Client"] = "lite";
        server.ProtectedAccessToken = McpSecrets.Protect("secret-token");

        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(server, http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None);

            var request = handler.Requests[0];
            Assert.Equal("lite", Header(request, "X-Client"));
            Assert.Equal("Bearer secret-token", Header(request, "Authorization"));
        }
    }

    [Fact]
    public async Task Send_RetriesATransientServerErrorOnce()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}"));
        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            await transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None);

            Assert.Equal(2, handler.PostCount);
            Assert.True((bool)(await ReceiveAsync(transport))["result"]!["ok"]!);
        }
    }

    [Fact]
    public async Task ModernRequests_NameTheMethodAndToolInHeaders()
    {
        var handler = new FakeHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            transport.SetSession(null, McpProtocolVersions.Modern);
            await transport.SendAsync(McpJsonRpc.Request(1, "tools/call", new JObject { ["name"] = "read_file" }),
                CancellationToken.None);

            var request = handler.LastPost();
            Assert.Equal("tools/call", Header(request, "Mcp-Method"));
            Assert.Equal("read_file", Header(request, "Mcp-Name"));
        }
    }

    [Fact]
    public async Task LegacyRequests_DoNotCarryTheStatelessHeaders()
    {
        var handler = new FakeHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            transport.SetSession(null, "2025-11-25");
            await transport.SendAsync(McpJsonRpc.Request(1, "tools/call", new JObject { ["name"] = "read_file" }),
                CancellationToken.None);

            Assert.Null(Header(handler.LastPost(), "Mcp-Method"));
        }
    }

    [Fact]
    public async Task ACancelledStreamIsClosedWhenItsResponseArrives()
    {
        var handler = new FakeHandler(_ => Sse(
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":9,\"result\":{\"late\":true}}\n\n"));
        using (var client = new HttpClient(handler))
        using (var transport = new McpHttpTransport(Server(), http: client))
        {
            await transport.StartAsync(CancellationToken.None);
            transport.SetSession(null, McpProtocolVersions.Modern);
            transport.CancelStream(new JValue(9));
            await transport.SendAsync(McpJsonRpc.Request(9, "subscriptions/listen"), CancellationToken.None);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    transport.ReceiveAsync(timeout.Token));
        }
    }

    private static async Task<JObject> ReceiveAsync(McpHttpTransport transport)
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            return (JObject)(await transport.ReceiveAsync(timeout.Token))!;
    }

    private static McpServerConfiguration Server() => new McpServerConfiguration
    {
        Name = "remote",
        Transport = "http",
        Url = "https://example.test/mcp"
    };

    private static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string body) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private static string? Header(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values) return value;
        }
        return null;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        internal readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();
        internal readonly List<string> Bodies = new List<string>();
        internal string LastAccept = "";
        internal int PostCount;

        internal JObject[] PostedBodies()
        {
            var result = new List<JObject>();
            foreach (var body in Bodies) result.Add(JObject.Parse(body));
            return result.ToArray();
        }

        internal HttpRequestMessage LastPost()
        {
            for (var i = Requests.Count - 1; i >= 0; i--)
                if (Requests[i].Method == HttpMethod.Post) return Requests[i];
            throw new InvalidOperationException("No POST was recorded.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastAccept = string.Join(", ", request.Headers.Accept);
            if (request.Method == HttpMethod.Post) PostCount++;
            if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync());
            else Bodies.Add("");
            return _respond(request);
        }
    }
}
