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
public sealed class LiteLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_ReportsTheUsageTheServerSent()
    {
        var handler = new FakeHandler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"hi\"}}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}"));
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var message = await client.CompleteAsync(Server(), "test-model", User("hi"), Settings(), null, CancellationToken.None);
            Assert.Equal("hi", message.Content);
            Assert.NotNull(client.LastUsage);
            Assert.Equal(7, client.LastUsage!.PromptTokens);
            Assert.Equal(3, client.LastUsage.CompletionTokens);
            Assert.Equal(10, client.LastUsage.TotalTokens);
            // A non-streamed call already carries usage; asking for stream usage there is meaningless.
            Assert.Null(JObject.Parse(handler.Body!)["stream_options"]);
        }
    }

    [Fact]
    public async Task StreamAsync_AsksForTheUsageChunkAndReadsIt()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
                + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":2,\"total_tokens\":22}}\n\n"
                + "data: [DONE]\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse(sse) });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal("hi", text.ToString());
            Assert.NotNull(client.LastUsage);
            Assert.Equal(22, client.LastUsage!.TotalTokens);
            var sent = JObject.Parse(handler.Body!);
            Assert.True((bool)sent["stream_options"]!["include_usage"]!);
        }
    }

    [Fact]
    public async Task StreamAsync_LeavesUsageNullWhenTheServerSendsNoUsageChunk()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null, _ => Task.CompletedTask, CancellationToken.None);
            Assert.Null(client.LastUsage);
        }
    }

    [Fact]
    public async Task StreamAsync_DoesNotSendStreamOptionsToADirectAzureConnection()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var server = Server();
            server.Provider = "Azure OpenAI";
            server.Deployment = "deployment";
            server.ApiVersion = "2024-06-01";
            await client.StreamAsync(server, "test-model", User("hi"), Settings(), null, _ => Task.CompletedTask, CancellationToken.None);
            Assert.Null(JObject.Parse(handler.Body!)["stream_options"]);
        }
    }

    [Fact]
    public async Task StreamAsync_ReadsUsageFromTheJsonFallbackWhenTheServerIgnoresStream()
    {
        var handler = new FakeHandler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"hi\"}}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}"));
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal("hi", text.ToString());
            Assert.NotNull(client.LastUsage);
            Assert.Equal(6, client.LastUsage!.TotalTokens);
        }
    }

    [Fact]
    public async Task StreamAsync_RetriesWithoutStreamOptionsWhenTheServerRejectsTheField()
    {
        // A server that does not know the field refuses the whole request. Losing the usage line is acceptable;
        // losing the answer is not, so the client must send the same turn again without the field.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Json("{\"error\":{\"message\":\"Unrecognized request argument supplied: stream_options\"}}", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal("hi", text.ToString());
            Assert.Equal(2, calls);
            Assert.True((bool)JObject.Parse(handler.Bodies[0]!)["stream_options"]!["include_usage"]!);
            Assert.Null(JObject.Parse(handler.Bodies[1]!)["stream_options"]);
            // Nothing was reported, so nothing is claimed.
            Assert.Null(client.LastUsage);
        }
    }

    private static ServerConfiguration Server() => new ServerConfiguration { Name = "Test", BaseUrl = "http://localhost:4000" };
    private static ChatMessage[] User(string text) => new[] { new ChatMessage { Role = "user", Content = text } };
    private static UserSettings Settings() => new UserSettings { EnableToolCalling = false };
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpContent Sse(string body) => new StringContent(body, Encoding.UTF8, "text/event-stream");

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        internal string? Body;
        internal readonly List<string?> Bodies = new List<string?>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            Bodies.Add(Body);
            return _respond(request);
        }
    }
}
