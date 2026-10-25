using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class McpDisabledMentionsTests
{
    private static McpServerConfiguration Server(string name, bool enabled = true, string solutionRoot = "")
    {
        return new McpServerConfiguration
        {
            Id = name,
            Name = name,
            Enabled = enabled,
            SolutionRoot = solutionRoot
        };
    }

    private static UserSettings Settings(params McpServerConfiguration[] servers)
    {
        return new UserSettings
        {
            EnableMcp = true,
            EnableToolCalling = true,
            McpServers = servers.ToList()
        };
    }

    [Fact]
    public void FindsAServerQualifiedMentionOfATurnedOffServer()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files", enabled: false)), null, "Use #files.read please.");

        var mention = Assert.Single(found);
        Assert.Equal("files", mention.Server.Name);
        Assert.Equal(McpInactiveReason.ServerOff, mention.Reason);
        Assert.Contains("turned off", mention.Summary);
        Assert.Contains("files.read", mention.Tokens);
        Assert.Contains("do not retry", mention.Hint);
    }

    [Fact]
    public void FindsTheExposedNameForm()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files", enabled: false)), null,
            "Run mcp__files__read for me.");

        Assert.Single(found);
    }

    [Fact]
    public void FindsTheSlugOfAServerNameWithSpaces()
    {
        var found = McpDisabledMentions.Find(Settings(Server("My Files", enabled: false)), null,
            "Use #My_Files.read.");

        Assert.Single(found);
        Assert.Equal("My Files", found[0].Server.Name);
    }

    [Fact]
    public void ReportsTheFeatureSwitchWhenMCPIsOff()
    {
        var settings = Settings(Server("files"));
        settings.EnableMcp = false;

        var found = McpDisabledMentions.Find(settings, null, "Use #files.read.");

        Assert.Equal(McpInactiveReason.FeatureOff, Assert.Single(found).Reason);
    }

    [Fact]
    public void ReportsAServerScopedToAnotherSolution()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files", solutionRoot: @"C:\other")),
            @"C:\solution", "Use #files.read.");

        var mention = Assert.Single(found);
        Assert.Equal(McpInactiveReason.OtherSolution, mention.Reason);
        Assert.Contains(@"C:\other", mention.Detail);
    }

    [Fact]
    public void IgnoresAnEnabledServer()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files")), null, "Use #files.read.");

        Assert.Empty(found);
    }

    [Fact]
    public void IgnoresProseThatNamesNoServer()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files", enabled: false)), null,
            "Please use the files server to read this.");

        Assert.Empty(found);
    }

    [Fact]
    public void OneServerDrawnOnceForEveryTokenItExplains()
    {
        var found = McpDisabledMentions.Find(Settings(Server("files", enabled: false)), null,
            "Use #files.read and #files.write.");

        var mention = Assert.Single(found);
        Assert.Equal(new[] { "files.read", "files.write" }, mention.Tokens);
    }

    [Fact]
    public void AnEnabledServerWithTheSameNameAnswersTheToken()
    {
        // Two entries share a name; only one is active. The selection path resolves the token to the active server,
        // so this check must not also report the other one as turned off.
        var found = McpDisabledMentions.Find(
            Settings(Server("files"), Server("files", enabled: false)), null, "Use #files.read.");

        Assert.Empty(found);
    }

    [Fact]
    public void Combine_KeepsTheSelectionHintAndAddsTheUnavailableServer()
    {
        var mention = Assert.Single(
            McpDisabledMentions.Find(Settings(Server("files", enabled: false)), null, "#files.read"));

        var hint = McpDisabledMentions.Combine("Selected tools for this turn.", new[] { mention });

        Assert.StartsWith("Selected tools for this turn.", hint);
        Assert.Contains("Unavailable MCP servers", hint);
        Assert.Contains("files", hint);
    }

    [Fact]
    public void Combine_ReturnsTheSelectionHintUnchangedWhenNothingIsUnavailable()
    {
        var hint = McpDisabledMentions.Combine("Selected tools for this turn.", new List<McpDisabledMention>());

        Assert.Equal("Selected tools for this turn.", hint);
    }
}
