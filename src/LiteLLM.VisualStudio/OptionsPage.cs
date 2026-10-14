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
    [Category("Features"), DisplayName("Verify the build after edits"), Description("In Agent mode, build the solution after the assistant changes files and hand any errors back to it to fix. The build runs the way Visual Studio's own Build Solution does, so any project type is built the way its project system expects."), DefaultValue(true)] public bool VerifyBuildAfterEdits { get; set; } = true;
    [Category("Features"), DisplayName("Verify the tests after edits"), Description("In Agent mode, run the solution's tests once a build has passed and hand any failures back to the assistant to fix. Off by default: a test run costs far more time than a build. Needs \"Verify the build after edits\", because tests are only meaningful against the assemblies that build just produced."), DefaultValue(false)] public bool VerifyTestsAfterEdits { get; set; }
    [Category("Terminal"), DisplayName("Enable terminal tools"), Description("Let the agent run PowerShell and Command Prompt commands on this computer from the solution directory. Commands are not confined to the solution."), DefaultValue(true)] public bool EnableTerminalTools { get; set; } = true;
    [Category("Terminal"), DisplayName("Confirm before each command"), Description("Show every command and wait for explicit approval before running it."), DefaultValue(true)] public bool ConfirmEachCommand { get; set; } = true;
    [Category("Terminal"), DisplayName("Command timeout (seconds)"), Description("Stop a command after this many seconds. Interactive programs, servers, and watchers are not supported."), DefaultValue(120)] public int CommandTimeoutSeconds { get; set; } = 120;
    [Category("Claude Code"), DisplayName("Executable"), Description("Claude Code CLI executable or full path."), DefaultValue("claude")] public string ClaudeExecutable { get; set; } = "claude";
    [Category("Claude Code"), DisplayName("Allowed tools"), Description("Explicit automatic approval for these Claude tools (comma/whitespace-separated rules). File writes and Bash are constrained by the editing, terminal, and confirmation guards; this is separate from the LiteLLM confirmation UI."), DefaultValue("Read,Glob,Grep")] public string ClaudeAllowedTools { get; set; } = "Read,Glob,Grep";
    [Category("Claude Code"), DisplayName("Disallowed tools"), Description("Comma/whitespace-separated Claude tools that must remain unavailable. Claude allowlisting is automatic approval, not the LiteLLM confirmation UI."), DefaultValue("WebFetch,WebSearch")] public string ClaudeDisallowedTools { get; set; } = "WebFetch,WebSearch";
    [Category("Context"), DisplayName("Utility model"), Description("Model used for the extension's own calls, such as summarizing the older part of a long conversation so it can be dropped without losing the thread. Use a small, cheap model here. A model id, or the full key from the model list when two servers offer the same id. Empty uses the model the chat is already talking to."), DefaultValue("")] public string UtilityModelKey { get; set; } = "";
    [Category("Diagnostics"), DisplayName("Logging level"), DefaultValue("Information")] public string LoggingLevel { get; set; } = "Information";
}
