using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>A transport whose request responses may be long-lived streams. The 2026-07-28 revision says a
/// <c>subscriptions/listen</c> is cancelled on HTTP by closing its stream, which is not a JSON-RPC message and
/// therefore cannot travel through <see cref="IMcpTransport.SendAsync"/>. A transport that can do that implements
/// this; a session sends <c>notifications/cancelled</c> when it cannot.</summary>
public interface IMcpStreamControl
{
    /// <summary>Closes the response stream that answers <paramref name="requestId"/>, if one is still open.</summary>
    void CancelStream(JToken requestId);
}
