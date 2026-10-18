using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpSseTransportTests
{
    [Fact]
    public async Task Start_WaitsForTheEndpointAndPostsThere()
    {
        var feed = new FeedStream();
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? EventStream(feed)
            : new HttpResponseMessage(HttpStatusCode.Accepted));

        using (var client = new HttpClient(handler))
        using (var transport = new McpSseTransport(Server(), http: client))
        {
            var starting = transport.StartAsync(CancellationToken.None);
            feed.Write("event: endpoint\ndata: /message?session=1\n\n");
            await starting;

            await transport.SendAsync(McpJsonRpc.Request(1, "initialize"), CancellationToken.None);

            Assert.Equal("https://example.test/message?session=1", handler.LastPostUri);
            Assert.Equal("initialize", handler.LastPostBody!["method"]!.Value<string>());
        }
    }

    [Fact]
    public async Task MessagesArriveOnTheStream()
    {
        var feed = new FeedStream();
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? EventStream(feed)
            : new HttpResponseMessage(HttpStatusCode.Accepted));

        using (var client = new HttpClient(handler))
        using (var transport = new McpSseTransport(Server(), http: client))
        {
            var starting = transport.StartAsync(CancellationToken.None);
            feed.Write("event: endpoint\ndata: /message\n\n");
            await starting;

            feed.Write("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"from\":\"stream\"}}\n\n");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var message = await transport.ReceiveAsync(timeout.Token);
                Assert.Equal("stream", message!["result"]!["from"]!.Value<string>());
            }
        }
    }

    [Fact]
    public async Task PostsCarryTheNegotiatedProtocolVersion()
    {
        var feed = new FeedStream();
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? EventStream(feed)
            : new HttpResponseMessage(HttpStatusCode.Accepted));

        using (var client = new HttpClient(handler))
        using (var transport = new McpSseTransport(Server(), http: client))
        {
            var starting = transport.StartAsync(CancellationToken.None);
            feed.Write("event: endpoint\ndata: /message\n\n");
            await starting;

            transport.SetSession(null, "2025-03-26");
            await transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None);

            Assert.Equal("2025-03-26", handler.LastHeader("MCP-Protocol-Version"));
        }
    }

    [Fact]
    public async Task CredentialsAreNotSentToAnEndpointOnAnotherOrigin()
    {
        var feed = new FeedStream();
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? EventStream(feed)
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var server = Server();
        server.ProtectedAccessToken = McpSecrets.Protect("secret-token");

        using (var client = new HttpClient(handler))
        using (var transport = new McpSseTransport(server, http: client))
        {
            var starting = transport.StartAsync(CancellationToken.None);
            feed.Write("event: endpoint\ndata: https://elsewhere.test/message\n\n");
            await starting;

            await transport.SendAsync(McpJsonRpc.Request(1, "ping"), CancellationToken.None);

            Assert.Null(handler.LastHeader("Authorization"));
        }
    }

    [Fact]
    public async Task Start_FailsWhenTheStreamEndsWithoutAnEndpoint()
    {
        var feed = new FeedStream();
        feed.End();
        var handler = new FakeHandler(_ => EventStream(feed));

        using (var client = new HttpClient(handler))
        using (var transport = new McpSseTransport(Server(), http: client))
        {
            var error = await Assert.ThrowsAsync<McpException>(() => transport.StartAsync(CancellationToken.None));
            Assert.Contains("before naming a message endpoint", error.Message, StringComparison.Ordinal);
        }
    }

    private static McpServerConfiguration Server() => new McpServerConfiguration
    {
        Name = "legacy",
        Transport = "sse",
        Url = "https://example.test/sse"
    };

    private static HttpResponseMessage EventStream(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>A stream the test writes to. The reader blocks until the next chunk arrives, which is what a real
    /// event stream does between notifications.</summary>
    private sealed class FeedStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new BlockingCollection<byte[]>();
        private readonly object _gate = new object();
        private byte[] _current = Array.Empty<byte>();
        private int _offset;

        public void Write(string text)
        {
            try { _chunks.Add(Encoding.UTF8.GetBytes(text)); }
            catch (InvalidOperationException) { }
        }

        public void End()
        {
            try { if (!_chunks.IsAddingCompleted) _chunks.CompleteAdding(); }
            catch (ObjectDisposedException) { }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                while (true)
                {
                    if (_offset < _current.Length)
                    {
                        var length = Math.Min(count, _current.Length - _offset);
                        Array.Copy(_current, _offset, buffer, offset, length);
                        _offset += length;
                        return length;
                    }
                    try
                    {
                        if (!_chunks.TryTake(out _current, Timeout.Infinite)) return 0;
                    }
                    catch (ObjectDisposedException) { return 0; }
                    _offset = 0;
                }
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            End();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        internal string? LastPostUri;
        internal JObject? LastPostBody;
        private HttpRequestMessage? _lastPost;

        internal string? LastHeader(string name)
        {
            if (_lastPost != null && _lastPost.Headers.TryGetValues(name, out var values))
                foreach (var value in values) return value;
            return null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                _lastPost = request;
                LastPostUri = request.RequestUri!.ToString();
                if (request.Content != null) LastPostBody = JObject.Parse(await request.Content.ReadAsStringAsync());
            }
            return _respond(request);
        }
    }
}
