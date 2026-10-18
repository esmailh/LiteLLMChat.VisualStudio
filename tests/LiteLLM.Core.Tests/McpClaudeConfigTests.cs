using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class McpClaudeConfigTests
{
    private static McpServerConfiguration Stdio(string name, string command = "npx")
    {
        return new McpServerConfiguration
        {
            Id = name,
            Name = name,
            Transport = "stdio",
            Command = command,
            ExposeToClaude = true,
            Args = new List<string> { "-y", "server" }
        };
    }

    private static McpServerConfiguration Http(string name, string url = "https://mcp.example.invalid/mcp")
    {
        return new McpServerConfiguration
        {
            Id = name,
            Name = name,
            Transport = "http",
            Url = url,
            ExposeToClaude = true
        };
    }

    private static UserSettings Settings(params McpServerConfiguration[] servers)
    {
        return new UserSettings { EnableMcp = true, McpServers = servers.ToList() };
    }

    private static JObject Map(McpClaudeRun run) => (JObject)run.Document["mcpServers"]!;

    [Fact]
    public void Prepare_WritesOnlyEnabledExposedServersInScope()
    {
        var included = Stdio("files");
        var hidden = Stdio("hidden");
        hidden.ExposeToClaude = false;
        var elsewhere = Stdio("elsewhere");
        elsewhere.SolutionRoot = @"C:\work\Other";
        var disabled = Stdio("disabled");
        disabled.Enabled = false;

        var run = McpClaudeConfig.Prepare(Settings(included, hidden, elsewhere, disabled), @"C:\work\Mine",
            new[] { included, hidden, elsewhere, disabled });

        var map = Map(run);
        Assert.Single(map.Properties());
        Assert.NotNull(map["files"]);
        Assert.Equal(1, run.Servers);
        Assert.True(run.HasServers);
    }

    [Fact]
    public void Prepare_SanitizesAndDeduplicatesServerKeys()
    {
        var first = Stdio("My Server");
        var second = Stdio("My_Server");

        var run = McpClaudeConfig.Prepare(Settings(first, second), null, new[] { first, second });

        var names = Map(run).Properties().Select(p => p.Name).ToList();
        Assert.Contains("My_Server", names);
        Assert.Contains("My_Server_2", names);
    }

    [Fact]
    public void Prepare_ResolvesPlaceholdersAndTheStoredBearerToken()
    {
        var server = Http("web");
        server.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Root"] = "${workspaceFolder}"
        };
        server.ProtectedAccessToken = "plain-token";

        var run = McpClaudeConfig.Prepare(Settings(server), @"C:\work\Mine", new[] { server });

        var body = (JObject)Map(run)["web"]!;
        Assert.Equal("http", body.Value<string>("type"));
        Assert.Equal("https://mcp.example.invalid/mcp", body.Value<string>("url"));
        Assert.Equal(@"C:\work\Mine", body["headers"]!.Value<string>("X-Root"));
        Assert.Equal("Bearer plain-token", body["headers"]!.Value<string>("Authorization"));
    }

    [Fact]
    public void Prepare_ExpandsStdioEnvironmentAndWorkingDirectory()
    {
        var server = Stdio("files");
        server.Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ROOT"] = "${workspaceFolder}"
        };
        server.WorkingDirectory = "${workspaceFolder}\\tools";

        var run = McpClaudeConfig.Prepare(Settings(server), @"C:\work\Mine", new[] { server });

        var body = (JObject)Map(run)["files"]!;
        Assert.Equal("npx", body.Value<string>("command"));
        Assert.Equal(@"C:\work\Mine", body["env"]!.Value<string>("ROOT"));
        Assert.Equal(@"C:\work\Mine\tools", body.Value<string>("cwd"));
    }

    [Fact]
    public void Prepare_AllowListContainsOnlyToolsThePolicyAllows()
    {
        var server = Stdio("files");
        server.AutoApproveReadOnly = true;
        server.AllowedTools.Add("pinned");
        var settings = Settings(server);
        settings.McpAutoApproveReadOnly = true;
        var tools = new List<McpToolInfo>
        {
            new McpToolInfo { Server = server, Name = "read", Annotations = new JObject { ["readOnlyHint"] = true } },
            new McpToolInfo { Server = server, Name = "write" },
            new McpToolInfo
            {
                Server = server, Name = "remove",
                Annotations = new JObject { ["readOnlyHint"] = true, ["destructiveHint"] = true }
            }
        };

        var run = McpClaudeConfig.Prepare(settings, null, new[] { server }, tools);

        Assert.Equal(new[] { "mcp__files__read", "mcp__files__pinned" }, run.AllowedTools);
    }

    [Fact]
    public void Prepare_RefusesAToolNameThatWouldBreakTheCommaSeparatedAllowList()
    {
        var server = Stdio("files");
        server.AutoApproveReadOnly = true;
        server.AllowedTools.Add("pinned,other");
        var settings = Settings(server);
        settings.McpAutoApproveReadOnly = true;
        var tools = new List<McpToolInfo>
        {
            new McpToolInfo { Server = server, Name = "read", Annotations = new JObject { ["readOnlyHint"] = true } },
            new McpToolInfo
            {
                Server = server,
                Name = "read,Bash(*)",
                Annotations = new JObject { ["readOnlyHint"] = true }
            }
        };

        var run = McpClaudeConfig.Prepare(settings, null, new[] { server }, tools);

        Assert.Equal(new[] { "mcp__files__read" }, run.AllowedTools);
        Assert.Equal(2, run.Notes.Count);
        Assert.All(run.Notes, note => Assert.Contains("cannot be written", note));
    }

    [Fact]
    public void Prepare_AllowAllAddsTheServerWidePatternUnlessAToolIsDenied()
    {
        var open = Stdio("open");
        var guarded = Stdio("guarded");
        guarded.DisabledTools.Add("dangerous");
        var settings = Settings(open, guarded);
        settings.PermissionMode = "allowAll";

        var run = McpClaudeConfig.Prepare(settings, null, new[] { open, guarded });

        Assert.Contains("mcp__open__*", run.AllowedTools);
        Assert.DoesNotContain("mcp__guarded__*", run.AllowedTools);
    }

    [Fact]
    public void Prepare_ReportsAnInvalidServerWithoutWritingIt()
    {
        var invalid = Stdio("broken", command: "");
        var valid = Stdio("files");
        var settings = Settings(invalid, valid);

        var run = McpClaudeConfig.Prepare(settings, null, new[] { invalid, valid });

        Assert.Single(Map(run).Properties());
        Assert.NotNull(Map(run)["files"]);
        var note = Assert.Single(run.Notes);
        Assert.Contains("broken", note);
        Assert.Contains("command", note);
    }

    [Fact]
    public void Prepare_AddsTheStrictFlagFromTheSettings()
    {
        var server = Stdio("files");
        var settings = Settings(server);
        settings.McpStrictClaudeConfig = false;

        Assert.False(McpClaudeConfig.Prepare(settings, null, new[] { server }).Strict);
        settings.McpStrictClaudeConfig = true;
        Assert.True(McpClaudeConfig.Prepare(settings, null, new[] { server }).Strict);
    }

    [Fact]
    public void Write_DisposesWithTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp-claude-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = Stdio("files");
            var run = McpClaudeConfig.Prepare(Settings(server), null, new[] { server });
            string path;
            using (var lease = McpClaudeConfig.Write(run, directory))
            {
                path = lease.Path;
                Assert.True(File.Exists(path));
                Assert.Equal(1, lease.Servers);
                Assert.Contains("mcpServers", File.ReadAllText(path));
                Assert.DoesNotContain("${workspaceFolder}", File.ReadAllText(path));
            }
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "run-*.json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Write_RefusesAConfigurationWithNoServers()
    {
        var run = McpClaudeConfig.Prepare(Settings(), null, Array.Empty<McpServerConfiguration>());
        Assert.Throws<InvalidOperationException>(() => McpClaudeConfig.Write(run));
    }

    [Fact]
    public void Sweep_DeletesOnlyStaleRunFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stale = Path.Combine(directory, "run-stale.json");
            var current = Path.Combine(directory, "run-current.json");
            var other = Path.Combine(directory, "notes.json");
            File.WriteAllText(stale, "{}");
            File.WriteAllText(current, "{}");
            File.WriteAllText(other, "{}");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-3));

            var deleted = McpClaudeConfig.SweepStale(TimeSpan.FromDays(1), directory);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(other));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

public sealed class ClaudeCodeArgumentsTests
{
    [Fact]
    public void Build_MergesTheMcpAllowListIntoTheConfiguredOne()
    {
        var arguments = ClaudeCodeArguments.Build("", "Bash(git),Edit", "",
            @"C:\temp\run.json", new[] { "mcp__files__read", "mcp__files__search" }, true);

        Assert.Contains("--allowedTools Bash(git),Edit,mcp__files__read,mcp__files__search", arguments);
    }

    [Fact]
    public void Build_AddsTheConfigAndTheStrictFlag()
    {
        var arguments = ClaudeCodeArguments.Build("", "", "", @"C:\Users\a b\run.json", null, true);

        Assert.Contains("--mcp-config \"C:\\Users\\a b\\run.json\"", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
    }

    [Fact]
    public void Build_LeavesTheCommandAloneWhenNoConfigIsGiven()
    {
        var arguments = ClaudeCodeArguments.Build("session-1", "Read", "Bash", null, null, true);

        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.DoesNotContain("--strict-mcp-config", arguments);
        Assert.Contains("--resume session-1", arguments);
    }
}
