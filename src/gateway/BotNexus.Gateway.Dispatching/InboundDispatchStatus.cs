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
    Rejected = 3,

    /// <summary>
    /// The message was injected into a turn that was already running rather than queued for a turn
    /// of its own (#3028). No new agent run was started and
    /// <see cref="InboundDispatchResult.Dispatches"/> is empty, because a steer produces no separate
    /// dispatch — the running turn absorbs it.
    /// </summary>
    /// <remarks>
    /// This status is only ever returned when the caller explicitly asked for
    /// <see cref="BotNexus.Gateway.Abstractions.Models.InboundDeliveryMode.Steer"/> or
    /// <see cref="BotNexus.Gateway.Abstractions.Models.InboundDeliveryMode.Interrupt"/> AND a turn
    /// was running. The default <c>Auto</c> intent never yields it.
    /// </remarks>
    Steered = 4,

    /// <summary>
    /// The message was written onto its per-isolation-unit queue but the queue did not drain within
    /// the orchestrator's bounded observation window, so the caller's await was released without a
    /// processing outcome (#3600).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a drop.</b> The message remains on the channel and is still processed when the
    /// head of the queue clears; only the caller's <em>await</em> is bounded. The status exists so a
    /// transport can tell "processed" from "still waiting behind a head that is not moving", which
    /// before #3600 were indistinguishable: <c>AcceptAsync</c> simply never returned, nothing threw,
    /// and nothing was logged, so an inbound message was unobservable between accept and processing.
    /// </para>
    /// <para>
    /// Every <see cref="Stalled"/> outcome is accompanied by a warning-level diagnostic naming the
    /// isolation key, the channel, and the requested conversation/session/agent, so the gap is never
    /// silent again.
    /// </para>
    /// </remarks>
    Stalled = 5
}
