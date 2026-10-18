using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class ServerConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Local LiteLLM";
    public string BaseUrl { get; set; } = "http://localhost:4000";
    public bool Enabled { get; set; } = true;
    public string ProtectedApiKey { get; set; } = "";
    /// <summary>Transport used by this connection. Existing settings are OpenAI-compatible.</summary>
    public string Provider { get; set; } = "OpenAI Compatible";
    public string Organization { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "";
}
public sealed class UserSettings
{
    public List<ServerConfiguration> Servers { get; set; } = new List<ServerConfiguration>();
    public List<string> DisabledModelKeys { get; set; } = new List<string>();
    public string SelectedModelKey { get; set; } = "";
    public string ClaudeExecutable { get; set; } = "claude";
    public string ClaudeAllowedTools { get; set; } = "Read,Glob,Grep";
    public string ClaudeDisallowedTools { get; set; } = "WebFetch,WebSearch";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public bool EnableStreaming { get; set; } = true;
    public int MaxTokens { get; set; } = 8192;
    public double Temperature { get; set; } = .2;
    public bool ShowReasoning { get; set; } = true;
    public bool EnableToolCalling { get; set; } = true;
    /// <summary>Approval policy selected in the chat composer. "default" observes the individual edit and command
    /// confirmation settings; "allowAll" permits all enabled tools without approval.</summary>
    public string PermissionMode { get; set; } = "default";
    /// <summary>Whether the agent may change files. On by default: a chat that can read a solution but never correct
    /// it answers every request to fix something with a snippet to paste, which is the report this setting answers.</summary>
    public bool EnableFileEditing { get; set; } = true;
    /// <summary>Ask before each edit instead of applying it. Off by default, matching Visual Studio Code, which writes
    /// an ordinary workspace file without asking and leaves the editor's undo stack to take it back.</summary>
    public bool ConfirmEachEdit { get; set; }
    /// <summary>Build the solution after the agent edits files, and hand any errors back to it to fix. On by
    /// default because an edit that does not compile is the failure users notice last: without this the chat
    /// reports success and the breakage surfaces at the next manual build. Agent mode only, and it uses Visual
    /// Studio's own build so a solution of any project type is built the way its project system expects.</summary>
    public bool VerifyBuildAfterEdits { get; set; } = true;
    /// <summary>Run the solution's tests after the agent edits files and a build has passed, handing any
    /// failures back to it the same way build errors are. Off by default: a test run costs far more time than a
    /// build, so it is opted into per solution. It is a check on top of the build rather than instead of it —
    /// tests are only run against the assemblies that build just produced, since a test run against an older
    /// binary reports on code that is not in the editor any more.</summary>
    public bool VerifyTestsAfterEdits { get; set; }
    /// <summary>Allow the agent to run PowerShell and CMD commands from the open solution.</summary>
    public bool EnableTerminalTools { get; set; } = true;
    /// <summary>Ask before every command. Enabled by default because commands can affect resources outside files.</summary>
    public bool ConfirmEachCommand { get; set; } = true;
    /// <summary>Maximum duration of one command before its process is terminated.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;
    /// <summary>Tool-calling rounds allowed before the chat asks whether to continue.</summary>
    public int MaxToolRounds { get; set; } = ChatStateLimits.DefaultToolRounds;
    /// <summary>Model the extension uses for its own calls — summarizing a long conversation's older messages
    /// so they can be dropped without losing the thread. A model id, or the full key from the model list when
    /// two servers offer the same id. Empty uses the model the chat is already talking to, which is what makes
    /// this work with no configuration at all.</summary>
    public string UtilityModelKey { get; set; } = "";
    /// <summary>Whether the file open in the editor rides along with every message, the way Visual Studio Code keeps
    /// the active editor in the chat's context. On by default; the composer's Auto button turns it off.</summary>
    public bool AutoAttachActiveDocument { get; set; } = true;
    public string LoggingLevel { get; set; } = "Information";

    /// <summary>MCP servers configured in the Dashboard. This list is saved by the Dashboard; the options page
    /// carries only the global switches, and ApplyOptions must not overwrite it.</summary>
    public List<McpServerConfiguration> McpServers { get; set; } = new List<McpServerConfiguration>();
    public bool EnableMcp { get; set; } = true;
    /// <summary>The global half of the read-only exception. A server can decline it, never grant it on its own.</summary>
    public bool McpAutoApproveReadOnly { get; set; }
    /// <summary>Sampling spends the user's tokens on a request the server asked for, so it is off unless chosen.</summary>
    public bool McpAllowSampling { get; set; }
    public bool McpAllowElicitation { get; set; } = true;
    public bool McpAllowRoots { get; set; } = true;
    public int McpConnectTimeoutSeconds { get; set; } = 30;
    public int McpToolTimeoutSeconds { get; set; } = 120;
    public int McpMaxToolsPerTurn { get; set; } = 64;
    public string McpLogLevel { get; set; } = "info";
    /// <summary>Only the generated --mcp-config is given to the Claude CLI. Off also loads the user's own Claude
    /// MCP configuration, which can add tools the Dashboard does not show.</summary>
    public bool McpStrictClaudeConfig { get; set; } = true;

    /// <summary>Every MCP server whose scope covers <paramref name="solutionRoot"/> and that the user enabled.</summary>
    public IEnumerable<McpServerConfiguration> EnabledMcpServers(string? solutionRoot) =>
        (McpServers ?? new List<McpServerConfiguration>())
            .Where(server => server != null && server.Enabled && server.AppliesTo(solutionRoot));
}
public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public Guid ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public string OwnedBy { get; set; } = "";
    public string Key => ServerId.ToString("N") + ":" + Id;
    public string ShortId => Id.Length <= 16 ? Id : Id.Substring(0, 15) + "…";
    public override string ToString() => Id + " · " + ServerName;
}
public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();
    /// <summary>Set on <c>tool</c> messages: the id of the call this message answers.</summary>
    public string ToolCallId { get; set; } = "";
    /// <summary>Set on <c>tool</c> messages: the function the result came from.</summary>
    public string ToolName { get; set; } = "";
}
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}
public sealed class StreamUpdate
{
    public string ContentDelta { get; set; } = "";
    public string ReasoningDelta { get; set; } = "";
    public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();
    public string FinishReason { get; set; } = "";
    /// <summary>Set on the last chunk when the request asked the server to include usage; null on every chunk
    /// that only carries text.</summary>
    public TokenUsage? Usage { get; set; }
}
public sealed class ToolDefinition
{
    [JsonProperty("type")] public string Type { get; set; } = "function";
    [JsonProperty("function")] public JObject Function { get; set; } = new JObject();
}
