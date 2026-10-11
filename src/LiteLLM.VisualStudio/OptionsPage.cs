using System.ComponentModel;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;
public sealed class LiteLlmOptionsPage : DialogPage
{
    [Category("Chat"), DisplayName("Request timeout (seconds)"), DefaultValue(120)] public int RequestTimeoutSeconds { get; set; } = 120;
    [Category("Chat"), DisplayName("Enable streaming"), DefaultValue(true)] public bool EnableStreaming { get; set; } = true;
    [Category("Chat"), DisplayName("Maximum tokens"), Description("Reasoning models spend this budget on thinking before they write anything — keep it generous."), DefaultValue(8192)] public int MaxTokens { get; set; } = 8192;
    [Category("Chat"), DisplayName("Temperature"), DefaultValue(.2)] public double Temperature { get; set; } = .2;
    [Category("Features"), DisplayName("Show reasoning"), DefaultValue(true)] public bool ShowReasoning { get; set; } = true;
    [Category("Features"), DisplayName("Enable safe tool calling"), DefaultValue(true)] public bool EnableToolCalling { get; set; } = true;
    [Category("Features"), DisplayName("Permission mode"), Description("Default permissions observes the individual confirmation settings. Allow all lets every enabled edit and command tool run without prompting."), DefaultValue("default")] public string PermissionMode { get; set; } = "default";
    [Category("Features"), DisplayName("Maximum tool rounds"), Description("Tool rounds before the chat asks whether to keep going. Continuing raises the budget by half, up to 200."), DefaultValue(ChatStateLimits.DefaultToolRounds)] public int MaxToolRounds { get; set; } = ChatStateLimits.DefaultToolRounds;
    [Category("Features"), DisplayName("Enable file editing"), Description("Let the agent create and change files in the open solution. Edits are made in the editor where the file is open, so Ctrl+Z takes them back. Nothing outside the solution can be touched either way."), DefaultValue(true)] public bool EnableFileEditing { get; set; } = true;
    [Category("Features"), DisplayName("Confirm before each edit"), Description("Show the change and wait for Allow before it is written. Off means edits apply straight away, as they do in Visual Studio Code."), DefaultValue(false)] public bool ConfirmEachEdit { get; set; }
    [Category("Terminal"), DisplayName("Enable terminal tools"), Description("Let the agent run PowerShell and Command Prompt commands on this computer from the solution directory. Commands are not confined to the solution."), DefaultValue(true)] public bool EnableTerminalTools { get; set; } = true;
    [Category("Terminal"), DisplayName("Confirm before each command"), Description("Show every command and wait for explicit approval before running it."), DefaultValue(true)] public bool ConfirmEachCommand { get; set; } = true;
    [Category("Terminal"), DisplayName("Command timeout (seconds)"), Description("Stop a command after this many seconds. Interactive programs, servers, and watchers are not supported."), DefaultValue(120)] public int CommandTimeoutSeconds { get; set; } = 120;
    [Category("Claude Code"), DisplayName("Executable"), Description("Claude Code CLI executable or full path."), DefaultValue("claude")] public string ClaudeExecutable { get; set; } = "claude";
    [Category("Claude Code"), DisplayName("Allowed tools"), Description("Explicit automatic approval for these Claude tools (comma/whitespace-separated rules). File writes and Bash are constrained by the editing, terminal, and confirmation guards; this is separate from the LiteLLM confirmation UI."), DefaultValue("Read,Glob,Grep")] public string ClaudeAllowedTools { get; set; } = "Read,Glob,Grep";
    [Category("Claude Code"), DisplayName("Disallowed tools"), Description("Comma/whitespace-separated Claude tools that must remain unavailable. Claude allowlisting is automatic approval, not the LiteLLM confirmation UI."), DefaultValue("WebFetch,WebSearch")] public string ClaudeDisallowedTools { get; set; } = "WebFetch,WebSearch";
    [Category("Diagnostics"), DisplayName("Logging level"), DefaultValue("Information")] public string LoggingLevel { get; set; } = "Information";
}
