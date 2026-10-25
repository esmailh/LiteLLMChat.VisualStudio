using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpToolDefinitionsTests
{
    [Fact]
    public void Build_NamesTheToolAfterItsServerAndCopiesTheSchema()
    {
        var schema = Tool("search", "Find files")
            .With("inputSchema", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["query"] = new JObject { ["type"] = "string" } }
            });
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", schema) });

        var tool = Assert.Single(snapshot.Tools);
        Assert.Equal("search", tool.Name);
        Assert.Equal("mcp__Files__search", tool.ExposedName);

        var definition = Assert.Single(snapshot.Definitions);
        Assert.Equal("mcp__Files__search", definition.Function["name"]!.Value<string>());
        Assert.Equal("string",
            definition.Function["parameters"]!["properties"]!["query"]!["type"]!.Value<string>());
        Assert.StartsWith("[Files] search: Find files", definition.Function["description"]!.Value<string>());
    }

    [Fact]
    public void Build_DeepCopiesTheSchemaSoAServerCannotChangeItUnderTheModel()
    {
        var entry = Tool("search", "Find");
        entry["inputSchema"] = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["query"] = new JObject { ["type"] = "string" } }
        };
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", entry) });

        snapshot.Definitions[0].Function["parameters"]!["properties"]!["query"]!["type"] = "number";

        Assert.Equal("string",
            entry["inputSchema"]!["properties"]!["query"]!["type"]!.Value<string>());
    }

    [Fact]
    public void Build_SuppliesAnEmptyObjectSchemaWhenTheServerSendsNone()
    {
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("bare", "")) });

        var parameters = snapshot.Definitions[0].Function["parameters"]!;
        Assert.Equal("object", parameters["type"]!.Value<string>());
        Assert.NotNull(parameters["properties"]);
    }

    [Fact]
    public void Build_LeavesOutADisabledToolWithoutCountingItAsOmitted()
    {
        var server = Server("Files");
        server.DisabledTools.Add("danger");
        var snapshot = McpToolSnapshot.Build(new[]
        {
            new McpToolListing
            {
                Server = server,
                Tools = new List<JObject> { Tool("safe", ""), Tool("danger", "") }
            }
        });

        Assert.Equal("safe", Assert.Single(snapshot.Tools).Name);
        Assert.Equal(0, snapshot.Omitted);
        Assert.Null(snapshot.Note);
    }

    [Fact]
    public void Build_ReportsTheToolsTheCapLeftOut()
    {
        var snapshot = McpToolSnapshot.Build(new[]
        {
            Listing("Files", Tool("one", ""), Tool("two", ""), Tool("three", ""))
        }, maxTools: 2);

        Assert.Equal(2, snapshot.Tools.Count);
        Assert.Equal(1, snapshot.Omitted);
        Assert.Contains("1 more was left out", snapshot.Note!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DefaultsToSixteenToolsPerTurn()
    {
        // The default is the prompt cost of every round, so it is small on purpose. Pinned here because
        // changing it silently changes how long a turn waits before the model starts answering.
        var tools = Enumerable.Range(1, 17).Select(index => Tool("tool" + index, "")).ToArray();
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", tools) });

        Assert.Equal(16, snapshot.Tools.Count);
        Assert.Equal(1, snapshot.Omitted);
        Assert.Contains("per-turn cap", snapshot.Note!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReportsAListedEntryThatHasNoName()
    {
        var snapshot = McpToolSnapshot.Build(new[]
        {
            Listing("Files", new JObject { ["description"] = "nameless" }, Tool("named", ""))
        });

        Assert.Single(snapshot.Tools);
        Assert.Equal(1, snapshot.Skipped);
        Assert.Contains("no name", snapshot.Note!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsTheAnnotationsAndTheirHints()
    {
        var entry = Tool("read", "");
        entry["annotations"] = new JObject
        {
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["openWorldHint"] = true,
            ["title"] = "Read a file"
        };
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", entry) });

        var tool = Assert.Single(snapshot.Tools);
        Assert.True(tool.ReadOnlyHint);
        Assert.False(tool.DestructiveHint);
        Assert.True(tool.OpenWorldHint);
        Assert.Equal("Read a file", tool.DisplayName);
    }

    [Fact]
    public void Snapshot_ResolvesAnExposedNameBackToTheServerTool()
    {
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("read.file", "")) });
        var exposed = snapshot.Tools[0].ExposedName;

        Assert.True(snapshot.TryResolve(exposed, out var tool));
        Assert.Equal("read.file", tool.Name);
        Assert.Equal("Files", tool.Server.Name);
        Assert.False(snapshot.TryResolve("mcp__missing__tool", out _));
    }

    [Fact]
    public void Build_TruncatesAnOverlongDescription()
    {
        var description = new string('x', 5000);
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("verbose", description)) });

        var text = snapshot.Definitions[0].Function["description"]!.Value<string>()!;
        Assert.True(text.Length < 1600, "The description should be capped before it reaches the model.");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void DefinitionsFor_ReturnsTheSelectedToolsInSnapshotOrder()
    {
        var snapshot = McpToolSnapshot.Build(new[]
        {
            Listing("Files", Tool("read", ""), Tool("write", ""), Tool("search", ""))
        });
        var names = snapshot.Tools.Select(tool => tool.ExposedName).ToList();

        var definitions = snapshot.DefinitionsFor(new[] { names[2], names[0] });

        Assert.Equal(2, definitions.Count);
        Assert.Equal(names[0], definitions[0].Function["name"]!.Value<string>());
        Assert.Equal(names[2], definitions[1].Function["name"]!.Value<string>());
    }

    [Fact]
    public void DefinitionsFor_ReturnsEveryDefinitionForNull()
    {
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("read", ""), Tool("write", "")) });

        Assert.Equal(snapshot.Definitions, snapshot.DefinitionsFor(null));
    }

    [Fact]
    public void DefinitionsFor_IgnoresNamesTheSnapshotDoesNotHave()
    {
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("read", "")) });

        Assert.Empty(snapshot.DefinitionsFor(new[] { "mcp__Files__gone", "" }));
        Assert.Single(snapshot.DefinitionsFor(new[] { "mcp__Files__read", "mcp__Files__gone" }));
    }

    [Fact]
    public void DefinitionsFor_ReturnsNothingForAnEmptySelection()
    {
        var snapshot = McpToolSnapshot.Build(new[] { Listing("Files", Tool("read", "")) });

        Assert.Empty(snapshot.DefinitionsFor(new string[0]));
        Assert.Empty(snapshot.DefinitionsFor(Enumerable.Empty<string>()));
    }

    private static McpServerConfiguration Server(string name) => new McpServerConfiguration
    {
        Name = name,
        Command = "fake"
    };

    private static McpToolListing Listing(string server, params JObject[] tools) => new McpToolListing
    {
        Server = Server(server),
        Tools = new List<JObject>(tools)
    };

    private static JObject Tool(string name, string description) => new JObject
    {
        ["name"] = name,
        ["description"] = description
    };
}

internal static class JObjectTestExtensions
{
    public static JObject With(this JObject target, string key, JToken value)
    {
        target[key] = value;
        return target;
    }
}
