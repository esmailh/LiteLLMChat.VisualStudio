using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpSessionTests
{
    [Fact]
    public async Task Connect_InitializesTheSessionAndAnnouncesThatItIsReady()
    {
        var transport = new FakeTransport();
        using (var session = new McpSession(Server(), transport, Options()))
        {
            var connecting = session.ConnectAsync(CancellationToken.None);
            var initialize = await transport.NextAsync();
            Assert.Equal("initialize", initialize["method"]!.Value<string>());
            Assert.Equal(McpProtocolVersions.Legacy, initialize["params"]!["protocolVersion"]!.Value<string>());
            Assert.Equal("LiteLLM.VisualStudio", initialize["params"]!["clientInfo"]!["name"]!.Value<string>());
            Assert.NotNull(initialize["params"]!["capabilities"]!["tools"]);

            transport.Enqueue(McpJsonRpc.Result(initialize["id"]!, new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                ["serverInfo"] = new JObject { ["name"] = "fake", ["version"] = "1.0" },
                ["instructions"] = "Read the manual."
            }));
            await connecting;

            var initialized = await transport.NextAsync();
            Assert.Equal("notifications/initialized", initialized["method"]!.Value<string>());

            Assert.Equal("2025-06-18", session.NegotiatedVersion);
            Assert.Equal(McpEra.Legacy, session.Era);
            Assert.Equal("fake", session.ServerInfo["name"]!.Value<string>());
            Assert.Equal("Read the manual.", session.Instructions);
            Assert.Equal("2025-06-18", transport.SessionVersion);
        }
    }

    [Fact]
    public async Task Connect_TreatsAnUnknownNewerVersionAsTheStatelessEra()
    {
        var transport = new FakeTransport();
        using (var session = new McpSession(Server(), transport, Options()))
        {
            var connecting = session.ConnectAsync(CancellationToken.None);
            var initialize = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(initialize["id"]!,
                new JObject { ["protocolVersion"] = "2026-09-01" }));
            await connecting;
            await transport.NextAsync(); // notifications/initialized

            Assert.Equal(McpProtocolVersions.Modern, session.NegotiatedVersion);
            Assert.Equal(McpEra.Modern, session.Era);
        }
    }

    [Fact]
    public async Task Requests_AreAnsweredByIdWhateverOrderTheResponsesArriveIn()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            var first = session.RequestAsync("tools/list", null, TimeSpan.FromSeconds(5), CancellationToken.None);
            var firstMessage = await transport.NextAsync();
            var second = session.RequestAsync("resources/list", null, TimeSpan.FromSeconds(5), CancellationToken.None);
            var secondMessage = await transport.NextAsync();

            transport.Enqueue(McpJsonRpc.Result(secondMessage["id"]!, new JObject { ["which"] = "second" }));
            transport.Enqueue(McpJsonRpc.Result(firstMessage["id"]!, new JObject { ["which"] = "first" }));

            Assert.Equal("first", (await CompleteAsync(first))["which"]!.Value<string>());
            Assert.Equal("second", (await CompleteAsync(second))["which"]!.Value<string>());
        }
    }

    [Fact]
    public async Task Notifications_AreRaisedWithTheirMethodAndParameters()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            var raised = new TaskCompletionSource<(string Method, JObject? Parameters)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.Notification += (method, parameters) => raised.TrySetResult((method, parameters));

            transport.Enqueue(McpJsonRpc.Notification("notifications/tools/list_changed"));
            var (method, parameters) = await CompleteAsync(raised.Task);

            Assert.Equal("notifications/tools/list_changed", method);
            Assert.Null(parameters);
        }
    }

    [Fact]
    public async Task CancellingARequest_TellsTheServerAndLeavesTheSessionUsable()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        using (var cancel = new CancellationTokenSource())
        {
            var request = session.RequestAsync("tools/call", null, TimeSpan.FromSeconds(30), cancel.Token);
            var sent = await transport.NextAsync();
            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

            var notice = await transport.NextAsync();
            Assert.Equal("notifications/cancelled", notice["method"]!.Value<string>());
            Assert.Equal(sent["id"]!.Value<int>(), notice["params"]!["requestId"]!.Value<int>());

            var next = session.RequestAsync("ping", null, TimeSpan.FromSeconds(5), CancellationToken.None);
            var ping = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(ping["id"]!, new JObject()));
            Assert.NotNull(await CompleteAsync(next));
        }
    }

    [Fact]
    public async Task Connect_ThatNeverStarts_IsBoundedByTheConnectTimeout()
    {
        var transport = new FakeTransport { Start = token => Task.Delay(Timeout.Infinite, token) };
        var options = Options();
        options.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        using (var session = new McpSession(Server(), transport, options))
        {
            var error = await Assert.ThrowsAsync<McpException>(() =>
                session.ConnectAsync(CancellationToken.None));

            Assert.Contains("did not connect", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASendThatNeverFinishes_TimesOutLikeAnUnansweredRequest()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            // The server accepted the message but the transport never finished writing it. Only the request
            // itself hangs; the cancellation notice still goes through.
            transport.SendHook = (message, token) =>
                message["method"]?.Value<string>() == "tools/call"
                    ? Task.Delay(Timeout.Infinite, token)
                    : null;

            var request = session.RequestAsync("tools/call", null, TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<McpException>(() => request);

            Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARequestThatIsNotAnswered_ReportsATimeoutAndIsCancelled()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            var request = session.RequestAsync("tools/call", null, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            await transport.NextAsync(); // the request itself, which stays unanswered
            var error = await Assert.ThrowsAsync<McpException>(() => request);

            Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
            Assert.Equal("notifications/cancelled", (await transport.NextAsync())["method"]!.Value<string>());
        }
    }

    [Fact]
    public async Task AClosedTransport_FailsEveryWaitingRequest()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            var request = session.RequestAsync("tools/call", null, TimeSpan.FromSeconds(30), CancellationToken.None);
            await transport.NextAsync();

            transport.Close();
            var error = await Assert.ThrowsAsync<McpException>(() => request);

            Assert.Contains("no longer connected", error.Message, StringComparison.Ordinal);
            Assert.True(session.IsClosed);
        }
    }

    [Fact]
    public async Task AServerRequest_IsAnsweredByTheHandler()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            session.ServerRequest = (request, token) => Task.FromResult<JObject?>(new JObject
            {
                ["roots"] = new JArray(new JObject { ["uri"] = "file:///c:/work", ["name"] = "work" })
            });

            transport.Enqueue(McpJsonRpc.Request("srv-1", "roots/list"));
            var response = await transport.NextAsync();

            Assert.Equal("srv-1", response["id"]!.Value<string>());
            Assert.Equal("file:///c:/work", response["result"]!["roots"]![0]!["uri"]!.Value<string>());
        }
    }

    [Fact]
    public async Task AnUnsupportedServerRequest_IsAnsweredWithMethodNotFound()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            transport.Enqueue(McpJsonRpc.Request(7, "sampling/createMessage"));
            var response = await transport.NextAsync();

            Assert.Equal(7, response["id"]!.Value<int>());
            Assert.Equal(McpErrorCodes.MethodNotFound, response["error"]!["code"]!.Value<int>());
        }
    }

    [Fact]
    public async Task ARequestAfterTheSessionClosed_ThrowsInsteadOfWaiting()
    {
        var (session, transport) = await ConnectAsync();
        using (session)
        {
            transport.Close();
            await WaitForAsync(() => session.IsClosed);

            var error = await Assert.ThrowsAsync<McpException>(() =>
                session.RequestAsync("ping", null, TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.Contains("not connected", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Connect_AutoProbesWithDiscoverAndAdoptsTheStatelessEra()
    {
        var transport = new FakeTransport();
        var server = Server();
        server.ProtocolVersion = "auto";
        using (var session = new McpSession(server, transport, Options()))
        {
            var connecting = session.ConnectAsync(CancellationToken.None);
            var discover = await transport.NextAsync();
            Assert.Equal("server/discover", discover["method"]!.Value<string>());

            var meta = discover["params"]!["_meta"]!;
            Assert.Equal(McpProtocolVersions.Modern,
                meta["io.modelcontextprotocol/protocolVersion"]!.Value<string>());
            Assert.Equal("LiteLLM.VisualStudio",
                meta["io.modelcontextprotocol/clientInfo"]!["name"]!.Value<string>());
            Assert.NotNull(meta["io.modelcontextprotocol/clientCapabilities"]!["tools"]);

            transport.Enqueue(McpJsonRpc.Result(discover["id"]!, new JObject
            {
                ["resultType"] = "complete",
                ["supportedVersions"] = new JArray(McpProtocolVersions.Modern),
                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/serverInfo"] = new JObject { ["name"] = "modern", ["version"] = "2.0" }
                },
                ["instructions"] = "Be careful."
            }));
            await connecting;

            Assert.Equal(McpEra.Modern, session.Era);
            Assert.Equal(McpProtocolVersions.Modern, session.NegotiatedVersion);
            Assert.Equal("modern", session.ServerInfo["name"]!.Value<string>());
            Assert.Equal("Be careful.", session.Instructions);
            // No handshake to complete in the stateless era.
            Assert.Null(await transport.TryNextAsync(TimeSpan.FromMilliseconds(100)));
        }
    }

    [Fact]
    public async Task Connect_FallsBackToInitializeWhenDiscoverIsNotUnderstood()
    {
        var transport = new FakeTransport();
        var server = Server();
        server.ProtocolVersion = "auto";
        using (var session = new McpSession(server, transport, Options()))
        {
            var connecting = session.ConnectAsync(CancellationToken.None);
            var discover = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Error(discover["id"]!, McpErrorCodes.MethodNotFound,
                "Method not found"));

            var initialize = await transport.NextAsync();
            Assert.Equal("initialize", initialize["method"]!.Value<string>());
            transport.Enqueue(McpJsonRpc.Result(initialize["id"]!, new JObject
            {
                ["protocolVersion"] = McpProtocolVersions.Legacy,
                ["capabilities"] = new JObject()
            }));
            await connecting;

            Assert.Equal("notifications/initialized", (await transport.NextAsync())["method"]!.Value<string>());
            Assert.Equal(McpEra.Legacy, session.Era);
            Assert.Equal(McpProtocolVersions.Legacy, transport.SessionVersion);
        }
    }

    [Fact]
    public async Task ModernRequests_CarryThePerRequestMetadata()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var request = session.RequestAsync("tools/list", new JObject { ["cursor"] = "page-2" },
                TimeSpan.FromSeconds(5), CancellationToken.None);
            var sent = await transport.NextAsync();
            var parameters = sent["params"]!;
            Assert.Equal("page-2", parameters["cursor"]!.Value<string>());
            Assert.Equal(McpProtocolVersions.Modern,
                parameters["_meta"]!["io.modelcontextprotocol/protocolVersion"]!.Value<string>());
            Assert.Equal("LiteLLM.VisualStudio",
                parameters["_meta"]!["io.modelcontextprotocol/clientInfo"]!["name"]!.Value<string>());

            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject { ["tools"] = new JArray() }));
            await CompleteAsync(request);
        }
    }

    [Fact]
    public async Task ModernPing_UsesDiscoverBecausePingWasRemoved()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var ping = session.PingAsync(CancellationToken.None);
            var sent = await transport.NextAsync();
            Assert.Equal("server/discover", sent["method"]!.Value<string>());
            Assert.NotNull(sent["params"]!["_meta"]);

            transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject { ["unique"] = "srv" }));
            Assert.Equal("srv", (await CompleteAsync(ping))["unique"]!.Value<string>());
        }
    }

    [Fact]
    public async Task SetLogLevel_IsSkippedInTheStatelessEra()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            await session.SetLogLevelAsync("debug", CancellationToken.None);
            Assert.Null(await transport.TryNextAsync(TimeSpan.FromMilliseconds(100)));
        }
    }

    [Fact]
    public async Task InputRequired_IsAnsweredAndRetriedWithANewId()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            session.ServerRequest = (request, token) =>
            {
                Assert.Equal("elicitation/create", request["method"]!.Value<string>());
                Assert.Equal("Pick one?", request["params"]!["message"]!.Value<string>());
                return Task.FromResult<JObject?>(new JObject { ["action"] = "accept" });
            };

            var call = session.RequestAsync("tools/call", new JObject
            {
                ["name"] = "echo",
                ["arguments"] = new JObject { ["text"] = "hi" }
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
            var first = await transport.NextAsync();

            transport.Enqueue(McpJsonRpc.Result(first["id"]!, new JObject
            {
                ["resultType"] = "input_required",
                ["inputRequests"] = new JObject
                {
                    ["confirm"] = new JObject
                    {
                        ["method"] = "elicitation/create",
                        ["params"] = new JObject { ["message"] = "Pick one?" }
                    }
                },
                ["requestState"] = "state-7"
            }));

            var retry = await transport.NextAsync();
            Assert.Equal("tools/call", retry["method"]!.Value<string>());
            Assert.NotEqual(first["id"]!.Value<int>(), retry["id"]!.Value<int>());
            Assert.Equal("echo", retry["params"]!["name"]!.Value<string>());
            Assert.Equal("accept", retry["params"]!["inputResponses"]!["confirm"]!["action"]!.Value<string>());
            Assert.Equal("state-7", retry["params"]!["requestState"]!.Value<string>());
            Assert.NotNull(retry["params"]!["_meta"]);

            transport.Enqueue(McpJsonRpc.Result(retry["id"]!, new JObject
            {
                ["resultType"] = "complete",
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "done" })
            }));
            var result = await CompleteAsync(call);
            Assert.Equal("done", result["content"]![0]!["text"]!.Value<string>());
        }
    }

    [Fact]
    public async Task InputRequired_WithoutAHandler_FailsTheCallWithoutRetrying()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var call = session.RequestAsync("tools/call", null, TimeSpan.FromSeconds(5), CancellationToken.None);
            var first = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(first["id"]!, new JObject
            {
                ["resultType"] = "input_required",
                ["inputRequests"] = new JObject
                {
                    ["confirm"] = new JObject { ["method"] = "sampling/createMessage" }
                }
            }));

            var error = await Assert.ThrowsAsync<McpException>(() => call);
            Assert.Equal(McpErrorCodes.MethodNotFound, error.Code);
            Assert.Contains("no handler", error.Message, StringComparison.Ordinal);
            Assert.Null(await transport.TryNextAsync(TimeSpan.FromMilliseconds(100)));
        }
    }

    [Fact]
    public async Task InputRequired_StopsAfterTheRoundCap()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            session.ServerRequest = (request, token) => Task.FromResult<JObject?>(new JObject());
            var call = session.RequestAsync("tools/call", null, TimeSpan.FromSeconds(5), CancellationToken.None);

            for (var round = 0; round < 6; round++)
            {
                var sent = await transport.NextAsync();
                transport.Enqueue(McpJsonRpc.Result(sent["id"]!, new JObject
                {
                    ["resultType"] = "input_required",
                    ["inputRequests"] = new JObject { ["again"] = new JObject { ["method"] = "roots/list" } }
                }));
            }

            var error = await Assert.ThrowsAsync<McpException>(() => call);
            Assert.Contains("kept asking", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Listening_RoutesTaggedNotificationsAndEndsOnTheCompleteResult()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var listening = session.ListenAsync(new JObject { ["toolsListChanged"] = true },
                CancellationToken.None);
            var open = await transport.NextAsync();
            Assert.Equal("subscriptions/listen", open["method"]!.Value<string>());
            Assert.True(open["params"]!["notifications"]!["toolsListChanged"]!.Value<bool>());
            Assert.NotNull(open["params"]!["_meta"]);

            var subscription = await listening;
            var meta = new JObject
            {
                ["io.modelcontextprotocol/subscriptionId"] = open["id"]!.DeepClone()
            };

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            subscription.Received += (method, parameters) =>
            {
                if (method != "notifications/subscriptions/acknowledged") received.TrySetResult(method);
            };

            transport.Enqueue(McpJsonRpc.Notification("notifications/subscriptions/acknowledged",
                new JObject { ["_meta"] = meta.DeepClone() }));
            Assert.True(await CompleteAsync(subscription.Acknowledged));

            transport.Enqueue(McpJsonRpc.Notification("notifications/tools/list_changed",
                new JObject { ["_meta"] = meta.DeepClone() }));
            Assert.Equal("notifications/tools/list_changed", await CompleteAsync(received.Task));

            transport.Enqueue(McpJsonRpc.Result(subscription.Id.DeepClone(),
                new JObject { ["resultType"] = "complete" }));
            var final = await CompleteAsync(subscription.Completion);
            Assert.Equal("complete", final["resultType"]!.Value<string>());
            Assert.True(subscription.IsEnded);

            // A finished subscription is not cancelled.
            subscription.Dispose();
            Assert.Null(await transport.TryNextAsync(TimeSpan.FromMilliseconds(100)));
        }
    }

    [Fact]
    public async Task DisposingASubscription_TellsTheServerToCancelIt()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var listening = session.ListenAsync(new JObject(), CancellationToken.None);
            var open = await transport.NextAsync();
            var subscription = await listening;
            subscription.Dispose();

            var cancel = await transport.NextAsync();
            Assert.Equal("notifications/cancelled", cancel["method"]!.Value<string>());
            Assert.Equal(open["id"]!.Value<int>(), cancel["params"]!["requestId"]!.Value<int>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.Completion);
        }
    }

    [Fact]
    public async Task ListAll_FollowsNextCursorAndReportsACap()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var listing = session.ListAllAsync("tools/list", null, "tools", maxItems: 3,
                cancellationToken: CancellationToken.None);
            var first = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(first["id"]!, new JObject
            {
                ["tools"] = new JArray(Item("a"), Item("b")),
                ["nextCursor"] = "page-2"
            }));

            var second = await transport.NextAsync();
            Assert.Equal("page-2", second["params"]!["cursor"]!.Value<string>());
            transport.Enqueue(McpJsonRpc.Result(second["id"]!, new JObject
            {
                ["tools"] = new JArray(Item("c"), Item("d")),
                ["nextCursor"] = "page-3"
            }));

            var page = await CompleteAsync(listing);
            Assert.Equal(3, page.Items.Count);
            Assert.True(page.Truncated);
            Assert.Contains("3 items", page.Note!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ListAll_ReadsEveryPageWhenNothingCutsItShort()
    {
        var (session, transport) = await ConnectModernAsync();
        using (session)
        {
            var listing = session.ListAllAsync("resources/list", null, "resources",
                cancellationToken: CancellationToken.None);
            var first = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(first["id"]!, new JObject
            {
                ["resources"] = new JArray(Item("a")),
                ["nextCursor"] = "more"
            }));
            var second = await transport.NextAsync();
            transport.Enqueue(McpJsonRpc.Result(second["id"]!, new JObject
            {
                ["resources"] = new JArray(Item("b"))
            }));

            var page = await CompleteAsync(listing);
            Assert.False(page.Truncated);
            Assert.Equal(2, page.Items.Count);
            Assert.Equal(2, page.Pages);
        }
    }

    private static JObject Item(string name) => new JObject { ["name"] = name };

    private static async Task<(McpSession Session, FakeTransport Transport)> ConnectModernAsync()
    {
        var transport = new FakeTransport();
        var server = Server();
        server.ProtocolVersion = "auto";
        var session = new McpSession(server, transport, Options());
        var connecting = session.ConnectAsync(CancellationToken.None);
        var discover = await transport.NextAsync();
        transport.Enqueue(McpJsonRpc.Result(discover["id"]!, new JObject
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = new JArray(McpProtocolVersions.Modern),
            ["capabilities"] = new JObject()
        }));
        await connecting;
        return (session, transport);
    }

    private static McpServerConfiguration Server() => new McpServerConfiguration
    {
        Name = "test",
        Command = "fake",
        // The existing tests exercise the session-oriented handshake; the probe has its own tests below.
        ProtocolVersion = "legacy"
    };

    private static McpSessionOptions Options() => new McpSessionOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(5)
    };

    private static async Task<(McpSession Session, FakeTransport Transport)> ConnectAsync()
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
        var initialized = await transport.NextAsync();
        Assert.Equal("notifications/initialized", initialized["method"]!.Value<string>());
        return (session, transport);
    }

    private static async Task<T> CompleteAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == task, "Timed out waiting for the operation to finish.");
        return await task;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "The condition was not reached in time.");
    }

    private sealed class FakeTransport : IMcpTransport
    {
        private readonly SemaphoreSlim _sent = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _incoming = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<JObject> _outbox = new ConcurrentQueue<JObject>();
        private readonly ConcurrentQueue<JObject> _inbox = new ConcurrentQueue<JObject>();
        private volatile bool _closed;
        private volatile bool _disposed;

        public string? SessionVersion { get; private set; }
        public string Describe => "fake";
        public Func<CancellationToken, Task>? Start { get; set; }
        /// <summary>Lets a test hold one send open. Null means the message goes out normally.</summary>
        public Func<JObject, CancellationToken, Task?>? SendHook { get; set; }

        public Task StartAsync(CancellationToken cancellationToken) =>
            Start != null ? Start(cancellationToken) : Task.CompletedTask;

        public Task SendAsync(JObject message, CancellationToken cancellationToken)
        {
            if (_disposed) throw new McpException("The transport was disposed.");
            var hooked = SendHook?.Invoke(message, cancellationToken);
            if (hooked != null) return hooked;
            _outbox.Enqueue(message);
            _sent.Release();
            return Task.CompletedTask;
        }

        public async Task<JObject?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_inbox.TryDequeue(out var message)) return message;
                if (_closed || _disposed) return null;
                await _incoming.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void SetSession(string? sessionId, string? protocolVersion) => SessionVersion = protocolVersion;

        public void Enqueue(JObject message)
        {
            _inbox.Enqueue(message);
            _incoming.Release();
        }

        public void Close()
        {
            _closed = true;
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

        public async Task<JObject?> TryNextAsync(TimeSpan wait)
        {
            if (_outbox.TryDequeue(out var message)) return message;
            using (var timeout = new CancellationTokenSource(wait))
            {
                try { await _sent.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            return _outbox.TryDequeue(out message) ? message : null;
        }

        public void Dispose()
        {
            _disposed = true;
            _incoming.Release();
        }
    }
}
