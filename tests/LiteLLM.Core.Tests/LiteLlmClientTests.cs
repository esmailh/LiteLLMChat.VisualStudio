using System;
using System.Collections.Generic;
using System.IO;
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

    [Fact]
    public async Task StreamAsync_ContinuesTheAnswerWhenTheConnectionIsResetMidStream()
    {
        // A proxy that times out, or a worker that restarts, resets the socket mid-answer. What arrived is not
        // a wrong answer, only an unfinished one, so the same turn is asked to go on from it.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"content\":\"The quick brown \"}}]}\n\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"fox jumps.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal("The quick brown fox jumps.", text.ToString());
            Assert.Equal(2, calls);
            var sent = (JArray)JObject.Parse(handler.Bodies[1]!)["messages"]!;
            Assert.Equal(3, sent.Count);
            Assert.Equal("assistant", (string?)sent[1]!["role"]);
            Assert.Equal("The quick brown ", (string?)sent[1]!["content"]);
            Assert.Equal("user", (string?)sent[2]!["role"]);
            Assert.Equal(StreamContinuation.Instruction, (string?)sent[2]!["content"]);
            // The copied response is the whole answer, with the dropped connection visible where it happened.
            Assert.Contains("The quick brown", client.LastExchange!.ResponseBody);
            Assert.Contains("fox jumps.", client.LastExchange.ResponseBody);
            Assert.Contains("connection dropped", client.LastExchange.ResponseBody);
        }
    }

    [Fact]
    public async Task StreamAsync_MarksTheFirstChunkOfAContinuation()
    {
        // The window draws a row when the answer is picked up again, so the seam is announced once — on the
        // first chunk that belongs to the continuation, not on every chunk after it.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"content\":\"one \"}}]}\n\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" three\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var resumed = new List<bool>();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { resumed.Add(update.Resumed); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal(new[] { false, true, false }, resumed);
        }
    }

    [Fact]
    public async Task StreamAsync_DropsTheJunctionTheContinuationRepeats()
    {
        // A model that cannot see its own beginning tends to start again from the last sentence. The repeat was
        // already drawn on screen, so it has to be removed as it arrives rather than afterwards.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"content\":\"The quick brown fox jumps over the lazy dog.\"}}]}\n\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"the lazy dog. And then it sleeps.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal("The quick brown fox jumps over the lazy dog. And then it sleeps.", text.ToString());
        }
    }

    [Fact]
    public async Task StreamAsync_SendsTheSameRequestAgainWhenNothingArrived()
    {
        // A connection that dropped before the model produced anything has nothing to carry over, so the
        // request is simply repeated. A continuation would be a different turn.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal("hi", text.ToString());
            Assert.Equal(2, calls);
            Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        }
    }

    [Fact]
    public async Task StreamAsync_ContinuesAStreamThatEndedWithoutAFinishReason()
    {
        // Some drops are a clean end of the socket rather than a reset; the missing finish reason is what says
        // the answer was cut, not the exception type.
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"one \"}}]}\n\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"two\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            await client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null,
                update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal("one two", text.ToString());
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task StreamAsync_DoesNotContinueHalfOfAToolCall()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var error = await Assert.ThrowsAsync<LiteLlmException>(() =>
                client.StreamAsync(Server(), "test-model", User("hi"), Settings(), null, _ => Task.CompletedTask, CancellationToken.None));

            Assert.Contains("tool", error.Message);
            Assert.Equal(1, calls);          // half a call is not a call, so there is nothing to continue
            Assert.NotNull(client.LastExchange);
        }
    }

    [Fact]
    public async Task StreamAsync_KeepsThePartialAnswerWhenTheContinuationsRunOut()
    {
        var settings = Settings();
        settings.StreamResumeAttempts = 1;
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"content\":\"half\"}}]}\n\n")
            : Broken("data: {\"choices\":[{\"delta\":{\"content\":\" more\"}}]}\n\n"));
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var text = new StringBuilder();
            var error = await Assert.ThrowsAsync<LiteLlmException>(() =>
                client.StreamAsync(Server(), "test-model", User("hi"), settings, null,
                    update => { text.Append(update.ContentDelta); return Task.CompletedTask; }, CancellationToken.None));

            Assert.Contains("continued", error.Message);
            Assert.Equal(2, calls);
            // What arrived is kept, both on screen and in the exchange the transcript copies from.
            Assert.Equal("half more", text.ToString());
            Assert.Contains("half", client.LastExchange!.ResponseBody);
            Assert.Contains(" more", client.LastExchange.ResponseBody);
        }
    }

    [Fact]
    public async Task StreamAsync_DoesNotContinueWhenTheSettingIsZero()
    {
        var settings = Settings();
        settings.StreamResumeAttempts = 0;
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? Broken("data: {\"choices\":[{\"delta\":{\"content\":\"half\"}}]}\n\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Sse("data: [DONE]\n\n") });
        using (var client = new LiteLlmClient(new HttpClient(handler)))
        {
            var error = await Assert.ThrowsAsync<LiteLlmException>(() =>
                client.StreamAsync(Server(), "test-model", User("hi"), settings, null, _ => Task.CompletedTask, CancellationToken.None));

            Assert.Contains("before the answer was finished", error.Message);
            Assert.Equal(1, calls);
        }
    }

    private static ServerConfiguration Server() => new ServerConfiguration { Name = "Test", BaseUrl = "http://localhost:4000" };
    private static ChatMessage[] User(string text) => new[] { new ChatMessage { Role = "user", Content = text } };
    private static UserSettings Settings() => new UserSettings { EnableToolCalling = false };
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpContent Sse(string body) => new StringContent(body, Encoding.UTF8, "text/event-stream");
    private static HttpResponseMessage Broken(string sse) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenSseContent(sse) };

    /// <summary>A response whose body delivers <paramref name="sse"/> and then resets, the way a proxy that
    /// timed out does.</summary>
    private sealed class BrokenSseContent : HttpContent
    {
        private readonly byte[] _data;
        internal BrokenSseContent(string sse)
        {
            _data = Encoding.UTF8.GetBytes(sse);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BrokenStream(_data));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        private sealed class BrokenStream : Stream
        {
            private readonly byte[] _data;
            private int _offset;
            internal BrokenStream(byte[] data) => _data = data;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_offset >= _data.Length) throw new IOException("The connection was forcibly closed by the remote host.");
                var length = Math.Min(count, _data.Length - _offset);
                Array.Copy(_data, _offset, buffer, offset, length);
                _offset += length;
                return length;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer, offset, count));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

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
