using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>What a session needs from a wire.
///
/// Receiving is a queue rather than a request/response pair because every transport can deliver a message the client
/// did not ask for — a notification, a progress report, a sampling request — on the same channel as the response it is
/// waiting for. <see cref="ReceiveAsync"/> returns the next such message, or null once the channel has closed.</summary>
public interface IMcpTransport : IDisposable
{
    string Describe { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task SendAsync(JObject message, CancellationToken cancellationToken);

    Task<JObject?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>The HTTP transports send this on every later request. The stdio transport ignores it.</summary>
    void SetSession(string? sessionId, string? protocolVersion);
}
