using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class McpClientTests
{
    private static McpServerConfiguration Server(string name, string id = "")
    {
        return new McpServerConfiguration
        {
            Id = id.Length > 0 ? id : name,
            Name = name,
            Enabled = true,
            Transport = "http",
            Url = "https://" + name + ".example.invalid/mcp",
            ProtocolVersion = "2025-11-25"
        };
    }

    private static UserSettings Settings(params McpServerConfiguration[] servers)
    {
        return new UserSettings
        {
            EnableMcp = true,
            McpConnectTimeoutSeconds = 5,
            McpToolTimeoutSeconds = 5,
            McpServers = servers.ToList()
        };
    }

    private static JObject Tool(string name, JObject? annotations = null)
    {
        var tool = new JObject
        {
            ["name"] = name,
            ["description"] = name + " description",
            ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject() }
        };
        if (annotations != null) tool["annotations"] = annotations;
        return tool;
    }

    [Fact]
    public async Task Prepare_OffersTheToolsOfEveryEnabledServer()
    {
        var first = Server("files");
        var second = Server("db");
        var transports = new Dictionary<string, FakeTransport>
        {
            [first.Name] = new FakeTransport { Tools = { Tool("read"), Tool("write") } },
            [second.Name] = new FakeTransport { Tools = { Tool("query") } }
        };
        using (var client = new McpClient(transportFactory: (server, log) => transports[server.Name]))
        {
            var report = await client.PrepareTurnAsync(Settings(first, second), null, CancellationToken.None);

            Assert.Empty(report.Rows);
            Assert.Equal(3, client.ToolDefinitions.Count);
            Assert.Contains(client.Tools, tool => tool.ExposedName == McpToolNaming.Exposed("files", "read"));
            Assert.True(client.TryResolveTool(McpToolNaming.Exposed("db", "query"), out var resolved));
            Assert.Equal("query", resolved.Name);
            Assert.Equal("db", resolved.Server.Name);
        }
    }

    [Fact]
    public async Task Prepare_DrawsARowForAnUnreachableServerAndKeepsTheOthers()
    {
        var good = Server("files");
        var bad = Server("broken");
        var transports = new Dictionary<string, FakeTransport>
        {
            [good.Name] = new FakeTransport { Tools = { Tool("read") } }
        };
        using (var client = new McpClient(transportFactory: (server, log) =>
        {
            if (server.Name == bad.Name) throw new McpException("the launch failed");
            return transports[server.Name];
        }))
        {
            var report = await client.PrepareTurnAsync(Settings(good, bad), null, CancellationToken.None);

            var row = Assert.Single(report.Rows);
            Assert.False(row.Ok);
            Assert.Contains("broken", row.Summary);
            Assert.Contains("the launch failed", row.Summary);
            Assert.Single(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task Prepare_SaysWhenAServerNeedsAuthentication()
    {
        var server = Server("secure");
        using (var client = new McpClient(transportFactory: (configuration, log) =>
                   throw new McpException("HTTP 401", authRequired: true)))
        {
            var report = await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);

            var row = Assert.Single(report.Rows);
            Assert.Contains("sign in from the MCP dashboard", row.Summary);
            Assert.Empty(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task Prepare_ReusesASessionThatWasAuthenticatedFromTheTokenStore()
    {
        // The session's connection settings are the settings entry plus the bearer header the client hydrates from
        // the token store. Comparing the hydrated clone with the entry never matched, so a signed-in server was
        // disconnected and started again on every turn.
        var server = Server("oauth");
        server.UseOAuth = true;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mcp-tokens-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var tokens = new McpTokenStore(path);
            var created = 0;
            using (var client = new McpClient(tokens, (configuration, log) =>
            {
                created++;
                return new FakeTransport { Tools = { Tool("read") } };
            }))
            {
                client.StoreSignIn(server, new McpOAuthToken
                {
                    AccessToken = "access",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                });
                var settings = Settings(server);

                await client.PrepareTurnAsync(settings, null, CancellationToken.None);
                await client.PrepareTurnAsync(settings, null, CancellationToken.None);

                Assert.Equal(1, created);
                Assert.Single(client.Tools);
            }
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Prepare_NothingIsConnectedWhenTheFeatureIsOff()
    {
        var server = Server("files");
        var created = false;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
        {
            created = true;
            return new FakeTransport();
        }))
        {
            var settings = Settings(server);
            settings.EnableMcp = false;
            await client.PrepareTurnAsync(settings, null, CancellationToken.None);

            Assert.False(created);
            Assert.Empty(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task Prepare_LeavesOutAServerScopedToAnotherSolution()
    {
        var server = Server("local");
        server.SolutionRoot = @"C:\work\Other";
        var created = false;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
        {
            created = true;
            return new FakeTransport();
        }))
        {
            await client.PrepareTurnAsync(Settings(server), @"C:\work\Mine", CancellationToken.None);

            Assert.False(created);
            Assert.Empty(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task Prepare_SaysHowManyToolsTheCapLeftOut()
    {
        var server = Server("files");
        var transport = new FakeTransport { Tools = { Tool("one"), Tool("two"), Tool("three") } };
        using (var client = new McpClient(transportFactory: (configuration, log) => transport))
        {
            var settings = Settings(server);
            settings.McpMaxToolsPerTurn = 2;
            var report = await client.PrepareTurnAsync(settings, null, CancellationToken.None);

            Assert.Equal(2, client.ToolDefinitions.Count);
            var row = Assert.Single(report.Rows);
            Assert.Contains("1 more", row.Summary);
            Assert.False(row.Ok);
        }
    }

    [Fact]
    public async Task Prepare_CarriesTheServersInstructionsForThePrompt()
    {
        var server = Server("files");
        var transport = new FakeTransport { Instructions = "Prefer reading whole files." };
        using (var client = new McpClient(transportFactory: (configuration, log) => transport))
        {
            var report = await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);

            Assert.NotNull(report.Instructions);
            Assert.Contains("[files]", report.Instructions!);
            Assert.Contains("Prefer reading whole files.", report.Instructions!);
        }
    }

    [Fact]
    public async Task CallTool_SendsTheServersOwnNameAndFormatsTheResult()
    {
        var server = Server("files");
        var transport = new FakeTransport { Tools = { Tool("read") } };
        using (var client = new McpClient(transportFactory: (configuration, log) => transport))
        {
            await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);
            Assert.True(client.TryResolveTool(McpToolNaming.Exposed("files", "read"), out var tool));

            var result = await client.CallToolAsync(tool, new JObject { ["path"] = "a.cs" }, null,
                CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Contains("hello from read", result.Content);
            var call = transport.Sent.Single(message => message.Value<string>("method") == "tools/call");
            Assert.Equal("read", call["params"]!.Value<string>("name"));
            Assert.Equal("a.cs", call["params"]!["arguments"]!.Value<string>("path"));
        }
    }

    [Fact]
    public async Task CallTool_ForwardsProgressUpdates()
    {
        var server = Server("files");
        var transport = new FakeTransport
        {
            Tools = { Tool("read") },
            Progress = new JObject { ["progress"] = 2, ["total"] = 4, ["message"] = "half" }
        };
        using (var client = new McpClient(transportFactory: (configuration, log) => transport))
        {
            await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);
            Assert.True(client.TryResolveTool(McpToolNaming.Exposed("files", "read"), out var tool));

            var seen = new TaskCompletionSource<McpProgressUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = await client.CallToolAsync(tool, new JObject(), update => seen.TrySetResult(update),
                CancellationToken.None);

            var update = await seen.Task;
            Assert.Equal(2, update.Progress);
            Assert.Equal(4, update.Total);
            Assert.Equal("half", update.Message);
            Assert.True(result.Ok);

            var call = transport.Sent.Single(message => message.Value<string>("method") == "tools/call");
            Assert.NotNull(call["params"]!["_meta"]?["progressToken"]);
        }
    }

    [Fact]
    public async Task CallTool_ReportsAServerErrorAsAFailedResult()
    {
        var server = Server("files");
        var transport = new FakeTransport { Tools = { Tool("read") }, CallIsError = true };
        using (var client = new McpClient(transportFactory: (configuration, log) => transport))
        {
            await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);
            Assert.True(client.TryResolveTool(McpToolNaming.Exposed("files", "read"), out var tool));

            var result = await client.CallToolAsync(tool, null, null, CancellationToken.None);

            // The server's own text is kept — it is often the only explanation of what failed — and the flag is
            // what tells the transcript the call did not succeed.
            Assert.False(result.Ok);
            Assert.Contains("hello from read", result.Content);
            Assert.Contains("failed", result.Summary);
        }
    }

    [Fact]
    public async Task CallTool_OnADisconnectedServerFailsWithAnExplanation()
    {
        var server = Server("files");
        var transport = new FakeTransport { Tools = { Tool("read") } };
        var client = new McpClient(transportFactory: (configuration, log) => transport);
        await client.PrepareTurnAsync(Settings(server), null, CancellationToken.None);
        Assert.True(client.TryResolveTool(McpToolNaming.Exposed("files", "read"), out var tool));

        client.Disconnect(server.Id);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync(tool, null, null, CancellationToken.None));
        Assert.Contains("not connected", ex.Message);
        client.Dispose();
    }

    [Fact]
    public async Task Prepare_ReusesASessionWhenNothingAboutTheConnectionChanged()
    {
        var server = Server("files");
        var created = 0;
        FakeTransport transport = null!;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created++;
                   transport = new FakeTransport { Tools = { Tool("read") } };
                   return transport;
               }))
        {
            var settings = Settings(server);
            await client.PrepareTurnAsync(settings, null, CancellationToken.None);
            await client.PrepareTurnAsync(settings, null, CancellationToken.None);

            Assert.Equal(1, created);
            Assert.False(transport.Disposed);
        }
    }

    [Fact]
    public async Task ConnectEnabled_ConnectsOnlyEnabledServers()
    {
        var files = Server("files");
        var database = Server("db");
        database.Enabled = false;
        var created = new List<string>();
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created.Add(configuration.Name);
                   return new FakeTransport { Tools = { Tool("read") } };
               }))
        {
            var report = await client.ConnectEnabledAsync(Settings(files, database), null, CancellationToken.None);

            Assert.Empty(report.Rows);
            Assert.Equal(new[] { "files" }, created);
            Assert.Contains(client.Tools, tool => tool.Server.Name == "files");
            Assert.DoesNotContain(client.Tools, tool => tool.Server.Name == "db");
        }
    }

    [Fact]
    public async Task ConnectEnabled_DropsAServerThatWasDisabledAfterItConnected()
    {
        var server = Server("files");
        var transport = new FakeTransport { Tools = { Tool("read") } };
        var created = 0;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created++;
                   return transport;
               }))
        {
            var settings = Settings(server);
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);
            Assert.Single(client.Tools);

            server.Enabled = false;
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);

            Assert.Empty(client.Tools);
            Assert.True(transport.Disposed);
            Assert.Equal(1, created);
        }
    }

    [Fact]
    public async Task ConnectEnabled_DrawsARowForAnUnreachableServerAndKeepsTheOthers()
    {
        var good = Server("files");
        var bad = Server("broken");
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   if (configuration.Name == bad.Name) throw new McpException("the launch failed");
                   return new FakeTransport { Tools = { Tool("read") } };
               }))
        {
            var report = await client.ConnectEnabledAsync(Settings(good, bad), null, CancellationToken.None);

            var row = Assert.Single(report.Rows);
            Assert.False(row.Ok);
            Assert.Contains("broken", row.Summary);
            Assert.Contains("the launch failed", row.Summary);
            Assert.Single(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task ConnectEnabled_ReusesALiveSession()
    {
        var server = Server("files");
        var created = 0;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created++;
                   return new FakeTransport { Tools = { Tool("read") } };
               }))
        {
            var settings = Settings(server);
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);

            Assert.Equal(1, created);
            Assert.Single(client.Tools);
        }
    }

    [Fact]
    public async Task ConnectEnabled_ConnectsNothingWhenTheFeatureIsOff()
    {
        var server = Server("files");
        var created = false;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created = true;
                   return new FakeTransport();
               }))
        {
            var settings = Settings(server);
            settings.EnableMcp = false;
            var report = await client.ConnectEnabledAsync(settings, null, CancellationToken.None);

            Assert.False(created);
            Assert.Empty(report.Rows);
            Assert.Empty(client.ToolDefinitions);
        }
    }

    [Fact]
    public async Task Reconnect_StartsAFreshTransportAndReplacesTheTools()
    {
        var server = Server("files");
        var created = 0;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created++;
                   return new FakeTransport { Tools = { Tool(created == 1 ? "old" : "new") } };
               }))
        {
            var settings = Settings(server);
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);
            Assert.Contains(client.Tools, tool => tool.Name == "old");

            var status = await client.ReconnectAsync(server, settings, null, CancellationToken.None);

            // Two transports: the refresh restarts the connection rather than asking the live one again.
            Assert.True(status.Connected);
            Assert.Equal(2, created);
            Assert.Equal(1, status.Tools);
            Assert.DoesNotContain(client.Tools, tool => tool.Name == "old");
            Assert.Contains(client.Tools, tool => tool.Name == "new");
        }
    }

    [Fact]
    public async Task DisconnectingAndConnectingAgainStillConnectsAfterSeveralRounds()
    {
        var server = Server("files");
        var transports = new List<FakeTransport>();
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   var transport = new FakeTransport { Tools = { Tool("read") } };
                   transports.Add(transport);
                   return transport;
               }))
        {
            var settings = Settings(server);
            for (var round = 0; round < 5; round++)
            {
                await client.ConnectEnabledAsync(settings, null, CancellationToken.None);
                Assert.Single(client.Tools);

                client.Disconnect(server.Id);
                Assert.Null(client.StatusOf(server.Id));
                Assert.Empty(client.Tools);
            }

            // Every round started a session of its own and ended the previous one: a stale close must not leave a
            // later session unregistered, which is what made the server look unreachable after a few toggles.
            Assert.Equal(5, transports.Count);
            Assert.All(transports, transport => Assert.True(transport.Disposed));
        }
    }

    [Fact]
    public async Task DisconnectDuringAConnectDiscardsTheSessionAndTheNextAttemptConnects()
    {
        var server = Server("files");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport? first = null;
        var created = 0;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
               {
                   created++;
                   if (created == 1) return first = new FakeTransport { Tools = { Tool("read") }, StartGate = gate };
                   return new FakeTransport { Tools = { Tool("read") } };
               }))
        {
            var settings = Settings(server);
            var closed = new List<string>();
            client.ServerClosed += (configuration, reason) => closed.Add(reason);

            var connecting = client.ConnectEnabledAsync(settings, null, CancellationToken.None);
            await WaitForAsync(() => created == 1);
            // The user unticks the row while the handshake is still running.
            client.Disconnect(server.Id);
            gate.TrySetResult(true);
            await connecting;

            Assert.NotNull(first);
            Assert.True(first!.Disposed);
            Assert.Null(client.StatusOf(server.Id));
            Assert.Empty(client.Tools);
            Assert.Empty(closed);

            // The client is not wedged: the next attempt starts a fresh session and registers it.
            await client.ConnectEnabledAsync(settings, null, CancellationToken.None);
            Assert.Single(client.Tools);
            Assert.NotNull(client.StatusOf(server.Id));
            Assert.Equal(2, created);
        }
    }

    [Fact]
    public async Task DisconnectDoesNotReportAServerClosed()
    {
        var server = Server("files");
        using (var client = new McpClient(transportFactory: (configuration, log) =>
                   new FakeTransport { Tools = { Tool("read") } }))
        {
            var closed = new List<string>();
            client.ServerClosed += (configuration, reason) => closed.Add(reason);
            await client.ConnectEnabledAsync(Settings(server), null, CancellationToken.None);

            client.Disconnect(server.Id);

            // A disconnect the user asked for is not a server ending, and the transcript must not say it is.
            Assert.Empty(closed);
            Assert.Null(client.StatusOf(server.Id));
            Assert.Empty(client.Tools);
        }
    }

    [Fact]
    public async Task AServerThatEndsOnItsOwnIsReportedAndLeavesTheSnapshot()
    {
        var server = Server("files");
        FakeTransport? transport = null;
        using (var client = new McpClient(transportFactory: (configuration, log) =>
                   transport = new FakeTransport { Tools = { Tool("read") } }))
        {
            var closed = new List<string>();
            client.ServerClosed += (configuration, reason) => closed.Add(configuration.Name + ": " + reason);
            await client.ConnectEnabledAsync(Settings(server), null, CancellationToken.None);

            transport!.Dispose();

            await WaitForAsync(() => closed.Count == 1);
            Assert.Contains("files", closed[0]);
            Assert.Null(client.StatusOf(server.Id));
            Assert.Empty(client.Tools);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "The condition was not reached in time.");
    }

    /// <summary>A transport that answers the handshake and list calls itself. It is a real <see cref="IMcpTransport"/>,
    /// so the session, the request map and the client are exercised rather than stubbed.</summary>
    private sealed class FakeTransport : IMcpTransport
    {
        private readonly SemaphoreSlim _incoming = new SemaphoreSlim(0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<JObject> _inbox =
            new System.Collections.Concurrent.ConcurrentQueue<JObject>();
        private readonly object _sentGate = new object();
        private readonly List<JObject> _sent = new List<JObject>();
        private volatile bool _disposed;

        public List<JObject> Sent { get { lock (_sentGate) return _sent.ToList(); } }
        public List<JObject> Tools { get; } = new List<JObject>();
        public string? Instructions { get; set; }
        public bool CallIsError { get; set; }
        public JObject? Progress { get; set; }
        /// <summary>When set, <see cref="StartAsync"/> waits for it — the window in which the user can turn a server
        /// off before its handshake has finished.</summary>
        public TaskCompletionSource<bool>? StartGate { get; set; }
        public bool Disposed => _disposed;
        public string Describe => "fake";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (StartGate != null) await StartGate.Task.ConfigureAwait(false);
        }

        public Task SendAsync(JObject message, CancellationToken cancellationToken)
        {
            if (_disposed) throw new McpException("The transport was disposed.");
            lock (_sentGate) _sent.Add((JObject)message.DeepClone());

            var id = message["id"];
            var method = message["method"]?.Value<string>();
            if (id == null || method == null) return Task.CompletedTask;

            if (method == "tools/call" && Progress != null)
            {
                var token = (message["params"] as JObject)?["_meta"]?["progressToken"]?.ToString();
                Enqueue(McpJsonRpc.Notification("notifications/progress", new JObject
                {
                    ["progressToken"] = token,
                    ["progress"] = Progress["progress"],
                    ["total"] = Progress["total"],
                    ["message"] = Progress["message"]
                }));
            }

            var response = Answer(id, method);
            if (response != null) Enqueue(response);
            return Task.CompletedTask;
        }

        private JObject? Answer(JToken id, string method)
        {
            switch (method)
            {
                case "initialize":
                    var result = new JObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JObject
                        {
                            ["tools"] = new JObject(),
                            ["resources"] = new JObject(),
                            ["prompts"] = new JObject()
                        },
                        ["serverInfo"] = new JObject { ["name"] = "fake" }
                    };
                    if (Instructions != null) result["instructions"] = Instructions;
                    return McpJsonRpc.Result(id, result);

                case "tools/list":
                    return McpJsonRpc.Result(id, new JObject { ["tools"] = new JArray(Tools.Select(t => t.DeepClone())) });

                case "resources/list":
                    return McpJsonRpc.Result(id, new JObject { ["resources"] = new JArray() });

                case "resources/templates/list":
                    return McpJsonRpc.Result(id, new JObject { ["resourceTemplates"] = new JArray() });

                case "prompts/list":
                    return McpJsonRpc.Result(id, new JObject { ["prompts"] = new JArray() });

                case "logging/setLevel":
                    return McpJsonRpc.Result(id, new JObject());

                case "tools/call":
                    return McpJsonRpc.Result(id, new JObject
                    {
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "hello from read" }),
                        ["isError"] = CallIsError
                    });

                default:
                    return McpJsonRpc.Error(id, McpErrorCodes.MethodNotFound, "not in this fake");
            }
        }

        private void Enqueue(JObject message)
        {
            _inbox.Enqueue(message);
            _incoming.Release();
        }

        public async Task<JObject?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _incoming.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _inbox.TryDequeue(out var message) ? message : null;
        }

        public void SetSession(string? sessionId, string? protocolVersion) { }

        public void Dispose()
        {
            _disposed = true;
            StartGate?.TrySetResult(false);
            try { _incoming.Release(); } catch (ObjectDisposedException) { }
        }
    }
}
