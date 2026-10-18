using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>One <c>subscriptions/listen</c> stream. The subscription id is the listen request's JSON-RPC id; every
/// message the server sends for it carries that id in its <c>_meta</c>, which is why the handle keeps the raw token.
///
/// The handle is deliberately passive: the session routes messages into it, and <see cref="Dispose"/> ends it. A
/// caller that wants the changes subscribes to <see cref="Received"/>; a caller that wants to know whether the
/// server accepted the listen awaits <see cref="Acknowledged"/>.</summary>
public sealed class McpSubscription : IDisposable
{
    private readonly Action<McpSubscription> _cancel;
    private readonly TaskCompletionSource<JObject> _raw;
    private readonly TaskCompletionSource<bool> _acknowledged =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private volatile bool _ended;

    internal McpSubscription(JToken id, string idKey, TaskCompletionSource<JObject> raw,
        Action<McpSubscription> cancel)
    {
        Id = id;
        IdKey = idKey;
        _raw = raw;
        _cancel = cancel;
        Completion = CompleteAsync();
    }

    /// <summary>The listen request's id, exactly as it was sent.</summary>
    public JToken Id { get; }
    public string IdKey { get; }

    /// <summary>The normal response to the listen request, which the server sends when it closes the subscription
    /// gracefully. Faults with <see cref="McpException"/> if the session ends, and is cancelled by
    /// <see cref="Dispose"/>.</summary>
    public Task<JObject> Completion { get; }

    /// <summary>Completes when the server sends <c>notifications/subscriptions/acknowledged</c>.</summary>
    public Task<bool> Acknowledged => _acknowledged.Task;

    /// <summary>Raised for each notification carrying this subscription's id (the acknowledgement included), on the
    /// session's receive loop. The same notification is also raised through the session's own
    /// <see cref="McpSession.Notification"/>, so a generic handler does not need to know about subscriptions.</summary>
    public event Action<string, JObject?>? Received;

    public bool IsEnded => _ended;

    internal void Receive(string method, JObject? parameters)
    {
        if (method == "notifications/subscriptions/acknowledged")
            _acknowledged.TrySetResult(true);
        try { Received?.Invoke(method, parameters); }
        catch { /* a handler's failure must not stop the receive loop */ }
    }

    /// <summary>The server sent the normal response: the subscription is over and <see cref="Completion"/> holds the
    /// final result. No cancellation is sent afterwards.</summary>
    internal void Ended()
    {
        _ended = true;
        _acknowledged.TrySetResult(false);
    }

    /// <summary>The session ended underneath the subscription.</summary>
    internal void SessionEnded(string message)
    {
        _ended = true;
        _acknowledged.TrySetException(new McpException(message));
        _raw.TrySetException(new McpException(message));
    }

    /// <summary>Called when the session itself cancels the stream; resolves the handle without recursing back.</summary>
    internal void Cancelled()
    {
        _ended = true;
        _acknowledged.TrySetCanceled();
        _raw.TrySetCanceled();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ended) return;
        _ended = true;
        _cancel(this);
    }

    private async Task<JObject> CompleteAsync()
    {
        var response = await _raw.Task.ConfigureAwait(false);
        return McpJsonRpc.ResultOf(response) as JObject ?? new JObject();
    }
}
