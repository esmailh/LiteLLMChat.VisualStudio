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
    [Category("Features"), DisplayName("Maximum tool rounds"), Description("Tool rounds before the chat asks whether to keep going. Continuing raises the budget by half, up to 200."), DefaultValue(ChatStateLimits.DefaultToolRounds)] public int MaxToolRounds { get; set; } = ChatStateLimits.DefaultToolRounds;
    [Category("Features"), DisplayName("Enable file editing"), Description("Let the agent create and change files in the open solution. Edits are made in the editor where the file is open, so Ctrl+Z takes them back. Nothing outside the solution can be touched either way."), DefaultValue(true)] public bool EnableFileEditing { get; set; } = true;
    [Category("Features"), DisplayName("Confirm before each edit"), Description("Show the change and wait for Allow before it is written. Off means edits apply straight away, as they do in Visual Studio Code."), DefaultValue(false)] public bool ConfirmEachEdit { get; set; }
    [Category("Diagnostics"), DisplayName("Logging level"), DefaultValue("Information")] public string LoggingLevel { get; set; } = "Information";
}
