using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class SseParserTests
{
    private static async Task<List<(string? Event, string Data)>> ReadAsync(string text)
    {
        var events = new List<(string?, string)>();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            await SseParser.ReadAsync(stream, (data, name) =>
            {
                events.Add((name, data));
                return Task.CompletedTask;
            }, CancellationToken.None);
        return events;
    }

    [Fact]
    public async Task ReportsTheEventNameAlongsideTheData()
    {
        var events = await ReadAsync("event: endpoint\ndata: /message?session=1\n\n" +
                                     "event: message\ndata: {\"jsonrpc\":\"2.0\"}\n\n");

        Assert.Equal(2, events.Count);
        Assert.Equal("endpoint", events[0].Event);
        Assert.Equal("/message?session=1", events[0].Data);
        Assert.Equal("message", events[1].Event);
        Assert.Equal("{\"jsonrpc\":\"2.0\"}", events[1].Data);
    }

    [Fact]
    public async Task AnEventWithoutANameHasANullName()
    {
        var events = await ReadAsync("data: one\n\n");

        Assert.Single(events);
        Assert.Null(events[0].Event);
        Assert.Equal("one", events[0].Data);
    }

    [Fact]
    public async Task JoinsTheDataLinesOfOneEvent()
    {
        var events = await ReadAsync("event: message\ndata: line one\ndata: line two\n\n");

        Assert.Single(events);
        Assert.Equal("line one\nline two", events[0].Data);
    }

    [Fact]
    public async Task ResetsTheEventNameAfterDispatch()
    {
        var events = await ReadAsync("event: endpoint\ndata: /a\n\ndata: /b\n\n");

        Assert.Equal("endpoint", events[0].Event);
        Assert.Null(events[1].Event);
    }

    [Fact]
    public async Task IgnoresCommentsAndUnknownFields()
    {
        var events = await ReadAsync(": keep-alive\nid: 4\nretry: 100\ndata: payload\n\n");

        Assert.Single(events);
        Assert.Equal("payload", events[0].Data);
    }

    [Fact]
    public async Task TheDataOnlyOverloadStillSeesEveryEvent()
    {
        var seen = new List<string>();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("event: endpoint\ndata: /a\n\ndata: b\n\n")))
            await SseParser.ReadAsync(stream, data => { seen.Add(data); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(new[] { "/a", "b" }, seen);
    }
}
