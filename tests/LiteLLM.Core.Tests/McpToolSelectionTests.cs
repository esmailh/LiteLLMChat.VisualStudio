using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpToolSelectionTests
{
    [Fact]
    public void Resolve_SelectsAnExactExposedName()
    {
        var snapshot = Snapshot();
        var exposed = snapshot.Tools.First(tool => tool.Name == "search_customers").ExposedName;

        var selection = McpToolSelection.Resolve(snapshot, "Use " + exposed + " for this.");

        Assert.True(selection.Narrows);
        Assert.Equal("search_customers", Assert.Single(selection.Selected).Name);
    }

    [Fact]
    public void Resolve_SelectsAServerQualifiedNameCaseInsensitively()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "Use #orders.SEARCH_CUSTOMERS.");

        Assert.True(selection.Narrows);
        Assert.Equal("Orders", selection.Selected[0].Server.Name);
    }

    [Fact]
    public void Resolve_MatchesTheSlugOfAServerNameWithSpaces()
    {
        var snapshot = Snapshot(("My Orders", new[] { "search_customers" }));

        var selection = McpToolSelection.Resolve(snapshot, "#My_Orders.search_customers");

        Assert.True(selection.Narrows);
        Assert.Equal("My Orders", selection.Selected[0].Server.Name);
    }

    [Fact]
    public void Resolve_SelectsARawNameThatOnlyOneServerOffers()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#read_file");

        Assert.True(selection.Narrows);
        Assert.Equal("Files", selection.Selected[0].Server.Name);
    }

    [Fact]
    public void Resolve_LeavesAnAmbiguousRawNameUnselectedAndDrawsARow()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "Run #search.");

        Assert.False(selection.Narrows);
        Assert.Empty(selection.Selected);
        var row = Assert.Single(selection.Rows);
        Assert.False(row.Ok);
        Assert.Contains("matches 2 tools", row.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DisambiguatesWithTheServerPrefix()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#Files.search");

        Assert.True(selection.Narrows);
        var tool = Assert.Single(selection.Selected);
        Assert.Equal("Files", tool.Server.Name);
        Assert.Equal("search", tool.Name);
    }

    [Fact]
    public void Resolve_AcceptsASlashBetweenTheServerAndTheTool()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#Files/search");

        Assert.True(selection.Narrows);
        Assert.Equal("search", Assert.Single(selection.Selected).Name);
    }

    [Fact]
    public void Resolve_ReportsAnUnknownNameWithoutNarrowing()
    {
        var snapshot = Snapshot();

        var selection = McpToolSelection.Resolve(snapshot, "#nope");

        Assert.False(selection.Narrows);
        Assert.Empty(selection.Definitions);
        Assert.Contains(selection.Rows, row => !row.Ok &&
            row.Summary.IndexOf("#nope", System.StringComparison.Ordinal) >= 0);
        Assert.All(snapshot.Tools, tool => Assert.True(selection.Allows(tool.ExposedName)));
    }

    [Fact]
    public void Resolve_DefinitionsContainOnlyTheSelectedToolsInSnapshotOrder()
    {
        var snapshot = Snapshot();
        // Announce the later tool first; the request still gets the snapshot's order.
        var selection = McpToolSelection.Resolve(snapshot, "#Files.search then #Orders.search_customers");

        Assert.Equal(2, selection.Definitions.Count);
        var names = selection.Definitions.Select(definition =>
            definition.Function["name"]!.Value<string>()!).ToList();
        Assert.Equal(new[] { "mcp__Orders__search_customers", "mcp__Files__search" }, names);
    }

    [Fact]
    public void Resolve_SelectsADuplicateMentionOnce()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#Files.search and #Files.search again.");

        Assert.Single(selection.Selected);
    }

    [Fact]
    public void Resolve_CapsTheSelectionAndSaysSo()
    {
        var snapshot = Snapshot(("Orders", new[] { "a", "b", "c", "d", "e" }));

        var selection = McpToolSelection.Resolve(snapshot, "#Orders.a #Orders.b #Orders.c #Orders.d #Orders.e",
            maxTools: 2);

        Assert.Equal(2, selection.Selected.Count);
        Assert.Contains(selection.Rows, row => row.Summary.IndexOf("only the first 2",
            System.StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Resolve_ABacktickedNameHintsWithoutNarrowing()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "Use the `search_customers` tool.");

        Assert.False(selection.Narrows);
        Assert.Empty(selection.Selected);
        Assert.Empty(selection.Rows);
        Assert.Contains("search_customers", selection.Hint!, System.StringComparison.Ordinal);
        Assert.Contains("hints, not a selection", selection.Hint!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnAmbiguousBacktickedNameDrawsARowWithoutNarrowing()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "Call `search`, I think.");

        Assert.False(selection.Narrows);
        var row = Assert.Single(selection.Rows);
        Assert.False(row.Ok);
        Assert.Contains("matches 2 tools", row.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnUnknownBacktickedNameIsIgnored()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "The `whatever` variable is set.");

        Assert.False(selection.Narrows);
        Assert.Empty(selection.Rows);
        Assert.Null(selection.Hint);
    }

    [Fact]
    public void Resolve_AllowsOnlySelectedToolsWhenNarrowed()
    {
        var snapshot = Snapshot();
        var files = snapshot.Tools.First(tool => tool.Server.Name == "Files" && tool.Name == "search");
        var orders = snapshot.Tools.First(tool => tool.Server.Name == "Orders" && tool.Name == "search_customers");

        var selection = McpToolSelection.Resolve(snapshot, "#Files.search");

        Assert.True(selection.Allows(files.ExposedName));
        Assert.False(selection.Allows(orders.ExposedName));
    }

    [Fact]
    public void Resolve_AllowsEverythingWhenItIsAutomatic()
    {
        var snapshot = Snapshot();

        var selection = McpToolSelection.Resolve(snapshot, "Summarize the readme.");

        Assert.False(selection.Narrows);
        Assert.Null(selection.Hint);
        Assert.Empty(selection.Rows);
        Assert.All(snapshot.Tools, tool => Assert.True(selection.Allows(tool.ExposedName)));
    }

    [Fact]
    public void Resolve_HintSaysSelectionIsNotApproval()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#Files.search");

        Assert.Contains("Selection is not approval", selection.Hint!, System.StringComparison.Ordinal);
        Assert.Contains("only proof that a call ran", selection.Hint!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_HintSaysAnUnavailableNameMustNotBeSubstituted()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#missing_tool");

        Assert.Contains("unavailable", selection.Hint!, System.StringComparison.Ordinal);
        Assert.Contains("do not substitute", selection.Hint!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_HintIsBounded()
    {
        // A long server name keeps every selected line long enough that the listing itself overruns the ceiling.
        var server = new string('S', 60);
        var tools = Enumerable.Range(0, 16).Select(index => "t" + index.ToString("00")).ToArray();
        var message = string.Join(" ", tools.Select(name => "#" + server + "." + name));

        var selection = McpToolSelection.Resolve(Snapshot((server, tools)), message);

        Assert.True(selection.Narrows);
        Assert.Equal(16, selection.Selected.Count);
        Assert.True(selection.Hint!.Length <= 1600);
        Assert.EndsWith("…", selection.Hint!);
    }

    [Fact]
    public void Resolve_SanitizesANewlineInAServerNameBeforeItReachesARow()
    {
        var snapshot = Snapshot(("Evil\nServer", new[] { "do_it" }));

        var selection = McpToolSelection.Resolve(snapshot, "#Evil_Server.do_it");

        Assert.True(selection.Narrows);
        var detail = Assert.Single(selection.Rows).Detail!;
        Assert.DoesNotContain("Evil\nServer", detail);
        Assert.Contains("Evil Server", detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SelectedRowNamesHowManyToolsAreOffered()
    {
        var selection = McpToolSelection.Resolve(Snapshot(), "#Files.search #Orders.search_customers");

        var row = Assert.Single(selection.Rows);
        Assert.True(row.Ok);
        Assert.Contains("2 selected tools", row.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_LeavesAnExplainedMentionToItsOwnRow()
    {
        // The disabled-server check names the server and says why; a second "no offered tool matches" row for the
        // same token would be two answers to one word.
        var selection = McpToolSelection.Resolve(Snapshot(), "Use #Files.make_it.",
            explained: new[] { "Files.make_it" });

        Assert.False(selection.Narrows);
        Assert.Empty(selection.Rows);
    }

    private static McpToolSnapshot Snapshot() =>
        Snapshot(("Orders", new[] { "search_customers", "search" }), ("Files", new[] { "search", "read_file" }));

    private static McpToolSnapshot Snapshot(params (string Server, string[] Tools)[] servers) =>
        McpToolSnapshot.Build(servers.Select(server => new McpToolListing
        {
            Server = new McpServerConfiguration { Name = server.Server, Command = "fake" },
            Tools = server.Tools.Select(name => new JObject
            {
                ["name"] = name,
                ["description"] = name + " description"
            }).ToList()
        }));
}
