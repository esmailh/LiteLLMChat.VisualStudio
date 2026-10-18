using System;
using System.Threading.Tasks;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>The question an MCP tool call asks before it runs, in the same shape as <see cref="EditTools.Confirm"/>
/// and <see cref="TerminalTools.Confirm"/>: a static delegate the chat installs while it is open and clears when it
/// is not.
///
/// The harness treats a missing delegate as "not approved". An approval that silently defaults to yes would turn a
/// closed tool window into an unattended shell, which is the one failure this card exists to prevent.</summary>
internal static class McpApproval
{
    internal static Func<McpApprovalRequest, Task<bool>>? Confirm { get; set; }
}
