using System.Collections.Generic;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpToolMentionsTests
{
    [Fact]
    public void Parse_FindsAServerQualifiedTokenAtAWordStart()
    {
        var mentions = McpToolMentions.Parse("Please use #orders.search_customers for this.");

        var mention = Assert.Single(mentions);
        Assert.Equal("orders.search_customers", mention.Text);
        Assert.Equal(McpMentionKind.Selection, mention.Kind);
        Assert.Equal("Please use ".Length, mention.Start);
        Assert.Equal("orders.search_customers".Length + 1, mention.Length);
    }

    [Fact]
    public void Parse_KeepsDotsAndHyphensInsideTheToken()
    {
        var mention = Assert.Single(McpToolMentions.Parse("#my-server.read.file"));

        Assert.Equal("my-server.read.file", mention.Text);
    }

    [Fact]
    public void Parse_IgnoresAHashInsideAWord()
    {
        Assert.Empty(McpToolMentions.Parse("This is C# code."));
        Assert.Empty(McpToolMentions.Parse("foo#bar"));
    }

    [Fact]
    public void Parse_IgnoresAHashFollowedByWhitespace()
    {
        Assert.Empty(McpToolMentions.Parse("# A heading"));
        Assert.Empty(McpToolMentions.Parse("#"));
    }

    [Fact]
    public void Parse_IgnoresCSharpDirectivesAtTheStartOfALine()
    {
        Assert.Empty(McpToolMentions.Parse("#region Tools\n#if DEBUG\n#endregion"));
        // A real tool by that name stays reachable through the qualified form.
        var mention = Assert.Single(McpToolMentions.Parse("#Files.region"));
        Assert.Equal("Files.region", mention.Text);
    }

    [Fact]
    public void Parse_IgnoresTokensInsideFencedCode()
    {
        var mentions = McpToolMentions.Parse("Look:\n```text\n#Files.read\n```\nThen #Files.search.");

        var mention = Assert.Single(mentions);
        Assert.Equal("Files.search", mention.Text);
    }

    [Fact]
    public void Parse_TreatsABacktickedNameAsAHintOnly()
    {
        var mentions = McpToolMentions.Parse("Use the `search_customers` tool, then `mcp__orders__search`.");

        Assert.Equal(2, mentions.Count);
        Assert.All(mentions, mention => Assert.Equal(McpMentionKind.Hint, mention.Kind));
        Assert.Equal("search_customers", mentions[0].Text);
        Assert.Equal("mcp__orders__search", mentions[1].Text);
    }

    [Fact]
    public void Parse_TreatsABareExposedNameAsASelection()
    {
        var mention = Assert.Single(McpToolMentions.Parse("Call mcp__orders__search now."));

        Assert.Equal(McpMentionKind.Selection, mention.Kind);
        Assert.Equal("mcp__orders__search", mention.Text);
    }

    [Fact]
    public void Parse_IgnoresTheBareWordMcpWithoutAnExposedShape()
    {
        Assert.Empty(McpToolMentions.Parse("the mcp__ prefix is documented elsewhere"));
    }

    [Fact]
    public void Parse_TrimsASentencePeriodFromTheToken()
    {
        var mention = Assert.Single(McpToolMentions.Parse("Use #Files.read."));

        Assert.Equal("Files.read", mention.Text);
    }

    [Fact]
    public void Parse_DoesNotLookInsideInlineCodeForASelection()
    {
        var mention = Assert.Single(McpToolMentions.Parse("Type `#Files.read` here."));

        Assert.Equal(McpMentionKind.Hint, mention.Kind);
        Assert.Equal("#Files.read", mention.Text);
    }

    [Fact]
    public void TryGetFilter_FindsTheHashAtAWordStart()
    {
        const string text = "#Files.re";

        Assert.True(McpToolMentions.TryGetFilter(text, text.Length, out var start, out var length, out var filter));
        Assert.Equal(0, start);
        Assert.Equal(text.Length, length);
        Assert.Equal("Files.re", filter);
    }

    [Fact]
    public void TryGetFilter_RejectsAHashInsideAWord()
    {
        Assert.False(McpToolMentions.TryGetFilter("C#", 2, out _, out _, out _));
        Assert.False(McpToolMentions.TryGetFilter("foo#bar", 7, out _, out _, out _));
        Assert.False(McpToolMentions.TryGetFilter("(#foo", 4, out _, out _, out _));
    }

    [Fact]
    public void TryGetFilter_RejectsABareHashFollowedBySpace()
    {
        Assert.False(McpToolMentions.TryGetFilter("# foo", 5, out _, out _, out _));
        Assert.False(McpToolMentions.TryGetFilter(" #", 1, out _, out _, out _));
    }

    [Fact]
    public void TryGetFilter_RejectsACaretOutsideTheText()
    {
        Assert.False(McpToolMentions.TryGetFilter("", 0, out _, out _, out _));
        Assert.False(McpToolMentions.TryGetFilter("#a", 0, out _, out _, out _));
        Assert.False(McpToolMentions.TryGetFilter("#a", 3, out _, out _, out _));
    }

    [Fact]
    public void TokenFor_PrefersTheServerQualifiedForm()
    {
        var tool = Tool("search_customers", "Orders");

        Assert.Equal("#Orders.search_customers", McpToolMentions.TokenFor(tool));
    }

    [Fact]
    public void TokenFor_UsesTheSlugForAServerNameWithSpaces()
    {
        var tool = Tool("search_customers", "My Orders");

        Assert.Equal("#My_Orders.search_customers", McpToolMentions.TokenFor(tool));
    }

    [Fact]
    public void TokenFor_FallsBackToTheExposedNameForANameThatIsNotOneToken()
    {
        var snapshot = McpToolSnapshot.Build(new[]
        {
            new McpToolListing
            {
                Server = new McpServerConfiguration { Name = "Orders", Command = "fake" },
                Tools = new List<JObject>
                {
                    new JObject { ["name"] = "search customers", ["description"] = "" }
                }
            }
        });
        var tool = snapshot.Tools[0];

        Assert.Equal("#" + tool.ExposedName, McpToolMentions.TokenFor(tool));
    }

    private static McpToolInfo Tool(string name, string server) => new McpToolInfo
    {
        Server = new McpServerConfiguration { Name = server, Command = "fake" },
        Name = name
    };
}
