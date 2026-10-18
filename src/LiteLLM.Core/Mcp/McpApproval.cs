using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What the approval card is shown. The arguments are the model's, so the card redacts and caps them before
/// display; the tool's own name is kept in both spellings so the user recognizes it and the allow-list stores the
/// server's name.</summary>
public sealed class McpApprovalRequest
{
    public McpToolInfo Tool { get; set; } = new McpToolInfo();
    public JObject Arguments { get; set; } = new JObject();
}

/// <summary>Returns whether the call may run. A denial is a normal answer, not an exception: the caller still has to
/// append a result for the call's id.</summary>
public delegate Task<bool> McpApprovalHandler(McpApprovalRequest request, CancellationToken cancellationToken);

/// <summary>Answers <c>sampling/createMessage</c>. The handler owns the model call; the session only carries the
/// answer back.</summary>
public delegate Task<JObject?> McpSamplingHandler(McpServerConfiguration server, JObject request,
    CancellationToken cancellationToken);

/// <summary>Answers <c>elicitation/create</c>. A decline is a normal answer: return the protocol's
/// <c>{"action":"decline"}</c> rather than null, which means "cannot".</summary>
public delegate Task<JObject?> McpElicitationHandler(McpServerConfiguration server, JObject request,
    CancellationToken cancellationToken);

/// <summary>Answers <c>roots/list</c>.</summary>
public delegate Task<JObject?> McpRootsHandler(McpServerConfiguration server, CancellationToken cancellationToken);
