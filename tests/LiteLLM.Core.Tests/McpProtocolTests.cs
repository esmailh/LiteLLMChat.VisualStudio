using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpProtocolTests
{
    [Fact]
    public void Negotiate_KeepsAKnownVersion()
    {
        Assert.Equal("2025-06-18", McpProtocolVersions.Negotiate("2025-06-18"));
        Assert.Equal(McpProtocolVersions.Modern, McpProtocolVersions.Negotiate(McpProtocolVersions.Modern));
    }

    [Fact]
    public void Negotiate_TreatsAnUnknownNewerVersionAsModern()
    {
        Assert.Equal(McpProtocolVersions.Modern, McpProtocolVersions.Negotiate("2026-09-01"));
        Assert.True(McpProtocolVersions.IsModern(McpProtocolVersions.Negotiate("2026-09-01")));
    }

    [Fact]
    public void Negotiate_FallsBackToLegacyForAnUnreadableOrTooOldVersion()
    {
        Assert.Equal(McpProtocolVersions.Legacy, McpProtocolVersions.Negotiate(""));
        Assert.Equal(McpProtocolVersions.Legacy, McpProtocolVersions.Negotiate("not-a-date"));
        Assert.Equal(McpProtocolVersions.Legacy, McpProtocolVersions.Negotiate("2023-01-01"));
    }

    [Fact]
    public void Negotiate_KeepsAnOldVersionTheClientStillUnderstands()
    {
        Assert.Equal("2024-11-05", McpProtocolVersions.Negotiate("2024-11-05"));
    }

    [Fact]
    public void Capabilities_OnlyAdvertiseTheFeaturesTheUserEnabled()
    {
        var enabled = McpProtocolVersions.Capabilities(roots: true, sampling: true, elicitation: true);
        var limited = McpProtocolVersions.Capabilities(roots: false, sampling: false, elicitation: false);

        Assert.NotNull(enabled["roots"]);
        Assert.NotNull(enabled["sampling"]);
        Assert.NotNull(enabled["elicitation"]);
        Assert.Null(limited["roots"]);
        Assert.Null(limited["sampling"]);
        Assert.Null(limited["elicitation"]);
        Assert.NotNull(limited["tools"]);
    }

    [Fact]
    public void Meta_CarriesTheVersionIdentityAndCapabilities()
    {
        var meta = McpProtocolVersions.Meta(McpProtocolVersions.Capabilities(true, false, true));

        Assert.Equal(McpProtocolVersions.Modern, meta["io.modelcontextprotocol/protocolVersion"]!.Value<string>());
        Assert.Equal("LiteLLM.VisualStudio",
            meta["io.modelcontextprotocol/clientInfo"]!["name"]!.Value<string>());
        Assert.NotNull(meta["io.modelcontextprotocol/clientCapabilities"]!["tools"]);
    }

    [Fact]
    public void Error_FromAServerIsReadableAndClassified()
    {
        var error = McpException.FromError(new JObject
        {
            ["code"] = -32601,
            ["message"] = "No such method",
            ["data"] = new JObject { ["method"] = "server/discover" }
        });

        Assert.Equal(-32601, error.Code);
        Assert.Equal("No such method", error.Message);
        Assert.NotNull(error.Data);

        Assert.False(McpException.FromError(new JObject { ["code"] = -32602, ["message"] = "bad" }).Retryable);
        Assert.True(McpException.FromError(new JObject { ["code"] = -32603, ["message"] = "boom" }).Retryable);
    }
}
