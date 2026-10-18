using System;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpConfigJsonTests
{
    [Fact]
    public void Import_ReadsStdioAndHttpServers()
    {
        var result = McpConfigJson.Import(@"{
            ""mcpServers"": {
                ""files"": { ""command"": ""npx"", ""args"": [""-y"", ""server-files""], ""env"": { ""ROOT"": ""C:\\work"" } },
                ""remote"": { ""type"": ""http"", ""url"": ""https://example.test/mcp"", ""headers"": { ""Authorization"": ""Bearer t"" } },
                ""legacy"": { ""type"": ""sse"", ""url"": ""https://example.test/sse"" }
            }
        }");

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Servers.Count);
        var files = result.Servers.Single(server => server.Name == "files");
        Assert.Equal(McpTransportKind.Stdio, files.Kind);
        Assert.Equal("npx", files.Command);
        Assert.Equal(new[] { "-y", "server-files" }, files.Args);
        Assert.Equal("C:\\work", files.Environment["ROOT"]);
        Assert.Equal(McpTransportKind.Http, result.Servers.Single(server => server.Name == "remote").Kind);
        Assert.Equal(McpTransportKind.Sse, result.Servers.Single(server => server.Name == "legacy").Kind);
    }

    [Fact]
    public void Import_ReadsOurExtensionKeysWhenTheyArePresent()
    {
        var result = McpConfigJson.Import(@"{
            ""mcpServers"": {
                ""scoped"": { ""command"": ""node"", ""enabled"": false, ""exposeToClaude"": false,
                    ""allowedTools"": [""read""], ""disabledTools"": [""write""],
                    ""autoApproveReadOnly"": true, ""protocolVersion"": ""legacy"", ""solutionRoot"": ""C:\\sln"" }
            }
        }");

        var server = Assert.Single(result.Servers);
        Assert.False(server.Enabled);
        Assert.False(server.ExposeToClaude);
        Assert.Equal(new[] { "read" }, server.AllowedTools);
        Assert.Equal(new[] { "write" }, server.DisabledTools);
        Assert.True(server.AutoApproveReadOnly);
        Assert.Equal("legacy", server.ProtocolVersion);
        Assert.Equal("C:\\sln", server.SolutionRoot);
    }

    [Fact]
    public void Import_ReportsAnEntryWithNeitherCommandNorUrl()
    {
        var result = McpConfigJson.Import("{\"mcpServers\":{\"broken\":{}}}");

        Assert.Empty(result.Servers);
        Assert.Contains(result.Warnings, warning => warning.IndexOf("broken", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Import_ReportsMalformedJsonInsteadOfThrowing()
    {
        var result = McpConfigJson.Import("{ not json");

        Assert.Empty(result.Servers);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Import_MakesDuplicateNamesUnique()
    {
        // JSON objects cannot repeat a key, but a single server object at the root and the declared name can collide
        // with the default used for a nameless import; this asserts the uniqueness rule directly across two entries.
        var result = McpConfigJson.Import(@"{""mcpServers"":{""same"":{""command"":""a""},""Same"":{""command"":""b""}}}");

        Assert.Equal(2, result.Servers.Select(server => server.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Export_OmitsSecretsUnlessTheyAreAskedFor()
    {
        var server = new McpServerConfiguration
        {
            Name = "remote",
            Transport = "http",
            Url = "https://example.test/mcp",
            Headers = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Authorization"] = "Bearer hunter2",
                ["X-Client"] = "lite"
            }
        };

        var without = McpConfigJson.Export(new[] { server });
        var with = McpConfigJson.Export(new[] { server }, includeSecrets: true);

        Assert.DoesNotContain("hunter2", without, StringComparison.Ordinal);
        Assert.Contains("<set>", without, StringComparison.Ordinal);
        Assert.Contains("hunter2", with, StringComparison.Ordinal);
        Assert.Contains("\"X-Client\": \"lite\"", without, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ProducesADocumentTheImporterReadsBack()
    {
        var original = new McpServerConfiguration
        {
            Name = "files",
            Command = "npx",
            Args = { "-y", "server-files" },
            Environment = new System.Collections.Generic.Dictionary<string, string> { ["ROOT"] = "C:\\work" }
        };

        var result = McpConfigJson.Import(McpConfigJson.Export(new[] { original }));
        var read = Assert.Single(result.Servers);

        Assert.Equal(original.Command, read.Command);
        Assert.Equal(original.Args, read.Args);
        Assert.Equal("C:\\work", read.Environment["ROOT"]);
    }
}
