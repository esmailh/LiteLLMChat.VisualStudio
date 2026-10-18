using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpFeatureTests
{
    [Fact]
    public void ResourceInfo_ReadsTheListing()
    {
        var info = McpResourceInfo.From(Server(), new JObject
        {
            ["uri"] = "file:///notes.md",
            ["name"] = "notes",
            ["mimeType"] = "text/markdown",
            ["size"] = 42
        });

        Assert.Equal("file:///notes.md", info.Uri);
        Assert.Equal("notes", info.DisplayName);
        Assert.Equal("text/markdown", info.MimeType);
        Assert.Equal(42L, info.Size!.Value);
    }

    [Fact]
    public void ResourceRead_ParsesTextAndDecodesABlob()
    {
        var read = McpResourceRead.From(new JObject
        {
            ["contents"] = new JArray(
                new JObject { ["uri"] = "file:///a.txt", ["mimeType"] = "text/plain", ["text"] = "alpha" },
                new JObject
                {
                    ["uri"] = "file:///b.bin",
                    ["blob"] = Convert.ToBase64String(new byte[] { 1, 2, 3 })
                })
        });

        Assert.Equal(2, read.Contents.Count);
        Assert.Equal("alpha", read.Contents[0].Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Contents[1].Blob);
    }

    [Fact]
    public void ResourceRead_ReportsABlobThatIsNotBase64()
    {
        var contents = McpResourceContents.From(new JObject { ["uri"] = "file:///x", ["blob"] = "!!!" });

        Assert.Null(contents.Blob);
        Assert.Contains("not valid base64", contents.Problem!, StringComparison.Ordinal);
        Assert.Contains("not valid base64", contents.ToText(null), StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceContents_ToText_SavesABlobAndNamesTheFile()
    {
        var contents = new McpResourceContents
        {
            Uri = "file:///b.bin",
            MimeType = "application/octet-stream",
            Blob = new byte[] { 1, 2, 3, 4 }
        };

        var text = contents.ToText((extension, bytes) => "C:\\media\\b.bin");

        Assert.Contains("file:///b.bin", text, StringComparison.Ordinal);
        Assert.Contains("4 bytes", text, StringComparison.Ordinal);
        Assert.Contains("C:\\media\\b.bin", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_SendsTheUriAndReturnsEveryBody()
    {
        var (session, transport) = await ConnectLegacyAsync();
        using (session)
        {
            var reading = McpResources.ReadAsync(session, "file:///a.txt", CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("resources/read", sent["method"]!.Value<string>());
            Assert.Equal("file:///a.txt", sent["params"]!["uri"]!.Value<string>());

            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject
            {
                ["contents"] = new JArray(new JObject { ["uri"] = "file:///a.txt", ["text"] = "alpha" })
            }));
            var read = await CompleteAsync(reading);
            Assert.Equal("alpha", Assert.Single(read.Contents).Text);
        }
    }

    [Fact]
    public async Task Subscribe_UsesTheListenStreamInTheStatelessEra()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var subscribing = McpResources.SubscribeAsync(session, "file:///a.txt", CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("subscriptions/listen", sent["method"]!.Value<string>());
            Assert.Equal("file:///a.txt",
                sent["params"]!["notifications"]!["resourceSubscriptions"]![0]!.Value<string>());

            var subscription = await CompleteAsync(subscribing);
            Assert.NotNull(subscription);
            subscription!.Dispose();

            var cancel = await transport.NextAsync();
            Assert.Equal("notifications/cancelled", cancel["method"]!.Value<string>());
        }
    }

    [Fact]
    public async Task Subscribe_UsesTheSubscribeRequestInTheSessionEra()
    {
        var (session, transport) = await ConnectLegacyAsync();
        using (session)
        {
            var subscribing = McpResources.SubscribeAsync(session, "file:///a.txt", CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("resources/subscribe", sent["method"]!.Value<string>());
            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject()));

            Assert.Null(await CompleteAsync(subscribing));
        }
    }

    [Fact]
    public void PromptInfo_ReadsItsArguments()
    {
        var prompt = McpPromptInfo.From(Server(), new JObject
        {
            ["name"] = "review",
            ["arguments"] = new JArray(
                new JObject { ["name"] = "path", ["required"] = true },
                new JObject { ["description"] = "no name" })
        });

        Assert.Equal("review", prompt.Name);
        var argument = Assert.Single(prompt.Arguments);
        Assert.Equal("path", argument.Name);
        Assert.True(argument.Required);
    }

    [Fact]
    public async Task Get_SendsTheNameAndArgumentsAndReadsMessages()
    {
        var (session, transport) = await ConnectLegacyAsync();
        using (session)
        {
            var getting = McpPrompts.GetAsync(session, "review",
                new JObject { ["path"] = "Program.cs" }, CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("prompts/get", sent["method"]!.Value<string>());
            Assert.Equal("review", sent["params"]!["name"]!.Value<string>());
            Assert.Equal("Program.cs", sent["params"]!["arguments"]!["path"]!.Value<string>());

            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject
            {
                ["description"] = "A review",
                ["messages"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JObject { ["type"] = "text", ["text"] = "Please review." }
                })
            }));

            var prompt = await CompleteAsync(getting);
            Assert.Equal("A review", prompt.Description);
            var message = Assert.Single(prompt.Messages);
            Assert.Equal("user", message.Role);
            Assert.Equal("Please review.", message.Text);
        }
    }

    [Fact]
    public void PromptResult_NamesANonTextMessageInsteadOfDroppingIt()
    {
        var prompt = McpPromptResult.From(new JObject
        {
            ["messages"] = new JArray(new JObject
            {
                ["role"] = "user",
                ["content"] = new JObject { ["type"] = "image", ["data"] = "..." }
            })
        });

        Assert.Contains("[image content]", Assert.Single(prompt.Messages).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_SendsTheReferenceAndArgument()
    {
        var (session, transport) = await ConnectLegacyAsync();
        using (session)
        {
            var completing = McpCompletion.CompleteAsync(session,
                McpCompletion.PromptReference("review"), "path", "Pro", null, CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("completion/complete", sent["method"]!.Value<string>());
            Assert.Equal("ref/prompt", sent["params"]!["ref"]!["type"]!.Value<string>());
            Assert.Equal("review", sent["params"]!["ref"]!["name"]!.Value<string>());
            Assert.Equal("path", sent["params"]!["argument"]!["name"]!.Value<string>());

            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject
            {
                ["completion"] = new JObject
                {
                    ["values"] = new JArray("Program.cs"),
                    ["hasMore"] = false
                }
            }));

            var completion = await CompleteAsync(completing);
            Assert.Equal("Program.cs", Assert.Single(completion.Values));
        }
    }

    [Fact]
    public void Completion_CapsTheSuggestionList()
    {
        var values = new JArray();
        for (var index = 0; index < McpCompletion.MaxValues + 5; index++) values.Add("value-" + index);

        var completion = McpCompletionResult.From(new JObject
        {
            ["completion"] = new JObject { ["values"] = values }
        });

        Assert.Equal(McpCompletion.MaxValues, completion.Values.Count);
        Assert.True(completion.Truncated);
    }

    private static McpServerConfiguration Server()
    {
        var server = new McpServerConfiguration { Name = "files", Command = "fake", ProtocolVersion = "legacy" };
        return server;
    }

    private static McpSessionOptions Options() => new McpSessionOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(5)
    };

    private static async Task<(McpSession, FakeTransport)> ConnectLegacyAsync()
    {
        var transport = new FakeTransport();
        var session = new McpSession(Server(), transport, Options());
        var connecting = session.ConnectAsync(CancellationToken.None);
        var initialize = await transport.NextAsync();
        transport.Enqueue(McpJsonRpc.Result(initialize["id"]!, new JObject
        {
            ["protocolVersion"] = McpProtocolVersions.Legacy,
            ["capabilities"] = new JObject()
        }));
        await connecting;
        await transport.NextAsync(); // notifications/initialized
        return (session, transport);
    }

    private static async Task<(McpSession, FakeTransport)> ConnectModernAsync()
    {
        var transport = new FakeTransport();
        var server = Server();
        server.ProtocolVersion = "auto";
        var session = new McpSession(server, transport, Options());
        var connecting = session.ConnectAsync(CancellationToken.None);
        var discover = await transport.NextAsync();
        transport.Enqueue(McpJsonRpc.Result(discover["id"]!, new JObject
        {
            ["supportedVersions"] = new JArray(McpProtocolVersions.Modern),
            ["capabilities"] = new JObject()
        }));
        await connecting;
        return (session, transport);
    }

    private static async Task<T> CompleteAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == task, "Timed out waiting for the operation to finish.");
        return await task;
    }

    private sealed class FakeTransport : IMcpTransport
    {
        private readonly SemaphoreSlim _sent = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _incoming = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<JObject> _outbox = new ConcurrentQueue<JObject>();
        private readonly ConcurrentQueue<JObject> _inbox = new ConcurrentQueue<JObject>();
        private volatile bool _disposed;

        public string Describe => "fake";

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAsync(JObject message, CancellationToken cancellationToken)
        {
            if (_disposed) throw new McpException("The transport was disposed.");
            _outbox.Enqueue(message);
            _sent.Release();
            return Task.CompletedTask;
        }

        public async Task<JObject?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_inbox.TryDequeue(out var message)) return message;
                if (_disposed) return null;
                await _incoming.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void SetSession(string? sessionId, string? protocolVersion) { }

        public void Enqueue(JObject message)
        {
            _inbox.Enqueue(message);
            _incoming.Release();
        }

        public async Task<JObject> NextAsync()
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (true)
                {
                    if (_outbox.TryDequeue(out var message)) return message;
                    await _sent.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _incoming.Release();
        }
    }
}
