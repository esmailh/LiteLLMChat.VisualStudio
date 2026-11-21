using System.Collections.Generic;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Saying it once. A message that asks the chat — not the turn — for MCP is remembered with the
/// conversation, and one that only mentions MCP is not. Both directions matter: a chat that cannot be told to stop
/// is a trap, and a chat that changes state from a passing word is unpredictable.</summary>
public sealed class McpChatInstructionTests
{
    public static IEnumerable<object[]> Requests => new[]
    {
        new object[] { "از این به بعد توی این چت کلا از MCP استفاده کن" },
        new object[] { "mcp رو در این چت همیشه استفاده کن" },
        new object[] { "لطفا از این به بعد در این چت از mcp استفاده کن" },
        new object[] { "از mcp هر بار استفاده کن" },
        new object[] { "Use MCP in this chat from now on." },
        new object[] { "always use mcp here" },
        new object[] { "please use mcp for every turn of this conversation" },
        new object[] { "keep using MCP in this chat" },
        new object[] { "mcp باید در این چت همیشه در دسترس باشه" },
        new object[] { "این چت رو با mcp ادامه بده" }
    };

    public static IEnumerable<object[]> Refusals => new[]
    {
        new object[] { "از این به بعد در این چت از mcp استفاده نکن" },
        new object[] { "دیگه از mcp استفاده نکن" },
        new object[] { "mcp رو در این چت خاموش کن" },
        new object[] { "این چت رو بدون mcp ادامه بده" },
        new object[] { "Don't use MCP in this chat anymore." },
        new object[] { "never use mcp in this conversation" },
        new object[] { "turn MCP off every turn in this chat" }
    };

    public static IEnumerable<object[]> MentionsOnly => new[]
    {
        new object[] { "mcp چیست؟" },
        new object[] { "the mcp server is slow today" },
        new object[] { "what tools does mcp offer?" },
        new object[] { "use mcp for every file in this folder" },
        new object[] { "این چت کند است" },
        new object[] { "mcp در این چت کند است" },
        new object[] { "MCP tools are not always needed" },
        new object[] { "کد رو با mcp-atlassian بررسی کن" },
        new object[] { "" }
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void AMessageThatAsksForTheChatIsRemembered(string message) =>
        Assert.Equal(McpChatRequest.EveryTurn, McpChatInstruction.Read(message));

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AMessageThatAsksForTheOppositeClearsIt(string message) =>
        Assert.Equal(McpChatRequest.OnlyWhenMentioned, McpChatInstruction.Read(message));

    [Theory]
    [MemberData(nameof(MentionsOnly))]
    public void AMessageThatOnlyMentionsMcpDecidesNothing(string message) =>
        Assert.Equal(McpChatRequest.None, McpChatInstruction.Read(message));

    [Fact]
    public void APastedConfigurationIsAQuotationRatherThanAnInstruction()
    {
        // A sample of an MCP configuration says "always" and names MCP on nearly every line; it is pasted to be
        // looked at, not obeyed.
        var message = "این کانفیگ درسته؟\n\n```json\n{ \"mcp\": { \"always\": true, \"use\": true } }\n```\n";

        Assert.Equal(McpChatRequest.None, McpChatInstruction.Read(message));
    }

    [Fact]
    public void ARefusalInAnotherSentenceDoesNotCancelTheRequest()
    {
        // The negation is read in the sentence that carries the verb, so an earlier "don't open anything" cannot
        // turn a later request into its opposite.
        Assert.Equal(McpChatRequest.EveryTurn,
            McpChatInstruction.Read("فایلها رو دست نکن. از این به بعد در این چت از mcp استفاده کن"));
    }

    [Fact]
    public void AWrongTypedWordIsAnsweredPlainly()
    {
        Assert.Equal(McpChatRequest.EveryTurn, McpChatInstruction.Read("از اين به بعد در اين چت از MCP استفاده كن"));
    }
}
