using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpSecretsTests
{
    [Fact]
    public void Protect_RoundTripsAValue()
    {
        var protectedValue = McpSecrets.Protect("secret");

        Assert.NotEqual("secret", protectedValue);
        Assert.Equal("secret", McpSecrets.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_ReturnsAPlainValueThatWasNeverProtected()
    {
        // A hand-edited settings file is readable text, not DPAPI data.
        Assert.Equal("plain", McpSecrets.Unprotect("plain"));
    }

    [Fact]
    public void Redact_HidesValuesWhoseNameLooksSecret()
    {
        Assert.Equal("<redacted>", McpSecrets.RedactValue("GITHUB_TOKEN", "abc"));
        Assert.Equal("lite", McpSecrets.RedactValue("X-Client", "lite"));
    }

    [Fact]
    public void Expand_ResolvesTheWorkspaceAndTheEnvironment()
    {
        System.Environment.SetEnvironmentVariable("LITELLM_MCP_TEST_ENV", "value");
        try
        {
            var expanded = McpSecrets.Expand("${workspaceFolder}|${LITELLM_MCP_TEST_ENV}|${MISSING_NAME}", "C:\\sln");
            Assert.Equal("C:\\sln|value|", expanded);
        }
        finally { System.Environment.SetEnvironmentVariable("LITELLM_MCP_TEST_ENV", null); }
    }

    [Fact]
    public void ForStorage_ProtectsTheServerWithoutMutatingTheLiveObject()
    {
        var server = new McpServerConfiguration
        {
            Environment = new Dictionary<string, string> { ["TOKEN"] = "secret" },
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }
        };

        var stored = McpSecrets.ForStorage(server);

        Assert.Equal("secret", server.Environment["TOKEN"]);
        Assert.NotEqual("secret", stored.Environment["TOKEN"]);
        Assert.NotEqual("Bearer secret", stored.Headers["Authorization"]);
    }

    [Fact]
    public void SettingsStore_DoesNotWriteAnMcpSecretInTheClear()
    {
        var file = Path.Combine(Path.GetTempPath(), "litellm-mcp-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new UserSettings();
            settings.McpServers.Add(new McpServerConfiguration
            {
                Name = "files",
                Command = "npx",
                Environment = new Dictionary<string, string> { ["TOKEN"] = "mcp-secret-value" }
            });

            var store = new SettingsStore(file);
            store.Save(settings);

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("mcp-secret-value", text, System.StringComparison.Ordinal);
            Assert.Equal("mcp-secret-value", store.Load().McpServers[0].Environment["TOKEN"]);
            Assert.Equal("mcp-secret-value", settings.McpServers[0].Environment["TOKEN"]);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
