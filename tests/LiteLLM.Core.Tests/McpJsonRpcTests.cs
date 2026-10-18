using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpJsonRpcTests
{
    [Fact]
    public void Request_CarriesTheIdMethodAndParameters()
    {
        var request = McpJsonRpc.Request("1", "tools/list", new JObject { ["cursor"] = "a" });

        Assert.Equal("2.0", request["jsonrpc"]!.Value<string>());
        Assert.Equal("1", request["id"]!.Value<string>());
        Assert.Equal("tools/list", request["method"]!.Value<string>());
        Assert.Equal("a", request["params"]!["cursor"]!.Value<string>());
    }

    [Fact]
    public void Notification_HasNoId()
    {
        var notification = McpJsonRpc.Notification("notifications/initialized");

        Assert.Null(notification["id"]);
        Assert.True(McpJsonRpc.IsNotification(notification));
        Assert.False(McpJsonRpc.IsRequest(notification));
    }

    [Fact]
    public void ClassifiesRequestsAndResponsesWithoutConfusingThem()
    {
        var request = McpJsonRpc.Request(4, "tools/call");
        var response = McpJsonRpc.Result(4, new JObject());
        var error = McpJsonRpc.Error(4, -32601, "missing");

        Assert.True(McpJsonRpc.IsRequest(request));
        Assert.False(McpJsonRpc.IsResponse(request));
        Assert.True(McpJsonRpc.IsResponse(response));
        Assert.False(McpJsonRpc.IsRequest(response));
        Assert.True(McpJsonRpc.IsResponse(error));
    }

    [Fact]
    public void IdKey_DistinguishesAStringIdFromANumberWithTheSameText()
    {
        Assert.NotEqual(McpJsonRpc.IdKey(new JValue("1")), McpJsonRpc.IdKey(new JValue(1)));
        Assert.Equal(McpJsonRpc.IdKey(new JValue("1")), McpJsonRpc.IdKey(new JValue("1")));
    }

    [Fact]
    public void ResultOf_ThrowsTheServersError()
    {
        var error = Assert.Throws<McpException>(() => McpJsonRpc.ResultOf(McpJsonRpc.Error(1, -32602, "bad params")));

        Assert.Equal(-32602, error.Code);
        Assert.Equal("bad params", error.Message);
    }

    [Fact]
    public void ResultOf_ReturnsTheResultObject()
    {
        var result = McpJsonRpc.ResultOf(McpJsonRpc.Result(1, new JObject { ["tools"] = new JArray() }));

        Assert.NotNull(result["tools"]);
    }
}
