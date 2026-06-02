namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// High-level outcome of <see cref="IInboundMessageOrchestrator.AcceptAsync"/>.
/// Transports inspect this status to decide whether to surface a busy/empty
/// indication to the caller (e.g. SignalR error frame, REST 503) without
/// having to read the per-agent <see cref="DispatchResult"/> list.
/// </summary>
public enum InboundDispatchStatus
{
    /// <summary>
    /// Message was accepted, queued (if applicable), processed, and at least one
    /// agent dispatch ran to completion. The result's
    /// <see cref="InboundDispatchResult.Dispatches"/> list carries the per-agent
    /// resolution metadata.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// Message was accepted and queued but the orchestrator's downstream router
    /// resolved zero target agents. No agent work ran. The result's
    /// <see cref="InboundDispatchResult.Dispatches"/> list is empty.
    /// </summary>
    NoRoute = 1,

    /// <summary>
    /// The per-session queue refused the message because it was full (capacity
    /// guard). The transport should signal back to the caller and ask them to
    /// retry shortly. No processor work ran.
    /// </summary>
    Busy = 2,

    /// <summary>
    /// The processor raised an exception while handling the message. The
    /// exception is rethrown to the caller of <see cref="IInboundMessageOrchestrator.AcceptAsync"/>;
    /// the status is provided for callers that catch and inspect.
    /// </summary>
    Rejected = 3
}
