using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One tool as a server listed it, with the name the model will see. The annotations are hints the server
/// publishes, not grants: <see cref="McpPolicy"/> reads them, but a hint never approves anything on its own.</summary>
public sealed class McpToolInfo
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public string Name { get; set; } = "";
    public string ExposedName { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public JObject InputSchema { get; set; } = new JObject { ["type"] = "object", ["properties"] = new JObject() };
    public JObject? OutputSchema { get; set; }
    public JObject? Annotations { get; set; }

    public bool ReadOnlyHint => Hint("readOnlyHint");
    public bool DestructiveHint => Hint("destructiveHint");
    public bool OpenWorldHint => Hint("openWorldHint");

    /// <summary>What the approval card and the transcript call it: the server's title if it has one, else its
    /// name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title!;

    private bool Hint(string name) =>
        Annotations?[name]?.Type == JTokenType.Boolean && Annotations.Value<bool>(name);
}

/// <summary>The tools one server returned from <c>tools/list</c>.</summary>
public sealed class McpToolListing
{
    public McpServerConfiguration Server { get; set; } = new McpServerConfiguration();
    public List<JObject> Tools { get; set; } = new List<JObject>();
}

/// <summary>An immutable view of every MCP tool offered this turn: the definitions sent to the model and the reverse
/// map dispatch resolves a call through.
///
/// The cap exists because every definition costs prompt space on every round; when it cuts the list, <see cref="Note"/>
/// says how many were left out rather than letting the model believe the list is complete.</summary>
public sealed class McpToolSnapshot
{
    private readonly Dictionary<string, McpToolInfo> _byName;

    internal McpToolSnapshot(List<McpToolInfo> tools, List<ToolDefinition> definitions, int omitted, int skipped)
    {
        Tools = tools;
        Definitions = definitions;
        Omitted = omitted;
        Skipped = skipped;
        _byName = tools.ToDictionary(tool => tool.ExposedName, StringComparer.Ordinal);

        var notes = new List<string>();
        if (omitted > 0)
            notes.Add("only the first " + tools.Count + " MCP tools were offered; " + omitted + " more " +
                      (omitted == 1 ? "was" : "were") + " left out by the per-turn cap");
        if (skipped > 0)
            notes.Add(skipped + (skipped == 1
                ? " MCP tool entry had no name and was ignored"
                : " MCP tool entries had no name and were ignored"));
        Note = notes.Count == 0 ? null : string.Join("; ", notes);
    }

    public IReadOnlyList<McpToolInfo> Tools { get; }
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Tools the cap left out.</summary>
    public int Omitted { get; }

    /// <summary>Listed entries that could not be offered at all because they had no usable name.</summary>
    public int Skipped { get; }

    /// <summary>Null when nothing was lost; otherwise the sentence the transcript draws.</summary>
    public string? Note { get; }

    public static McpToolSnapshot Empty { get; } = new McpToolSnapshot(new List<McpToolInfo>(),
        new List<ToolDefinition>(), 0, 0);

    public bool TryResolve(string exposedName, out McpToolInfo tool)
    {
        if (_byName.TryGetValue(exposedName ?? "", out var found))
        {
            tool = found;
            return true;
        }
        tool = null!;
        return false;
    }

    /// <summary>The definitions for exactly these exposed names, in snapshot order. Null returns every definition.
    /// A name the snapshot does not have is ignored rather than reported: a selection is re-resolved from the stored
    /// message on every turn, and the server it named may since have gone away.</summary>
    public IReadOnlyList<ToolDefinition> DefinitionsFor(IEnumerable<string>? exposedNames)
    {
        if (exposedNames == null) return Definitions;
        var wanted = new HashSet<string>(exposedNames.Where(name => !string.IsNullOrEmpty(name)),
            StringComparer.Ordinal);
        if (wanted.Count == 0) return Array.Empty<ToolDefinition>();

        // Tools and Definitions are built together in Build, index for index.
        var result = new List<ToolDefinition>();
        for (var index = 0; index < Tools.Count && index < Definitions.Count; index++)
            if (wanted.Contains(Tools[index].ExposedName)) result.Add(Definitions[index]);
        return result;
    }

    public static McpToolSnapshot Build(IEnumerable<McpToolListing> listings, int maxTools = 16)
    {
        var tools = new List<McpToolInfo>();
        var omitted = 0;
        var skipped = 0;

        foreach (var listing in listings)
        {
            foreach (var entry in listing.Tools)
            {
                var name = entry["name"]?.Type == JTokenType.String ? entry.Value<string>("name") : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    skipped++;
                    continue;
                }
                if (listing.Server.DisabledTools.Any(disabled =>
                        string.Equals(disabled, name, StringComparison.Ordinal)))
                    continue;
                if (tools.Count >= Math.Max(0, maxTools))
                {
                    omitted++;
                    continue;
                }

                var annotations = entry["annotations"] as JObject;
                var title = entry["title"]?.Type == JTokenType.String
                    ? entry.Value<string>("title")
                    : annotations?["title"]?.Type == JTokenType.String
                        ? annotations.Value<string>("title")
                        : null;
                var schema = entry["inputSchema"] as JObject;
                schema = schema == null
                    ? new JObject { ["type"] = "object", ["properties"] = new JObject() }
                    : (JObject)schema.DeepClone();
                if (schema["type"] == null) schema["type"] = "object";

                tools.Add(new McpToolInfo
                {
                    Server = listing.Server,
                    Name = name!,
                    Title = title,
                    Description = entry["description"]?.Type == JTokenType.String
                        ? entry.Value<string>("description")
                        : null,
                    InputSchema = schema,
                    OutputSchema = entry["outputSchema"]?.DeepClone() as JObject,
                    Annotations = annotations?.DeepClone() as JObject
                });
            }
        }

        var refs = McpToolNaming.Assign(tools.Select(tool => (tool.Server, tool.Name)));
        for (var index = 0; index < tools.Count; index++) tools[index].ExposedName = refs[index].ExposedName;

        var definitions = tools.Select(Define).ToList();
        return new McpToolSnapshot(tools, definitions, omitted, skipped);
    }

    private static ToolDefinition Define(McpToolInfo tool)
    {
        var description = "[" + tool.Server.Name + "] " + tool.DisplayName;
        if (!string.IsNullOrWhiteSpace(tool.Description))
            description += ": " + Clip(tool.Description!, McpToolDefinitionLimits.MaxDescriptionChars);
        return new ToolDefinition
        {
            Function = new JObject
            {
                ["name"] = tool.ExposedName,
                ["description"] = description,
                ["parameters"] = tool.InputSchema.DeepClone()
            }
        };
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";
}

internal static class McpToolDefinitionLimits
{
    /// <summary>A server controls this text. The model is not given unbounded prose per tool, and the transcript
    /// does not grow by a server's choice.</summary>
    public const int MaxDescriptionChars = 1500;
}
