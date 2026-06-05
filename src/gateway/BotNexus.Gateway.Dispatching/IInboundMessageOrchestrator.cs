using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Single inbound entry point that every transport (channel adapter, SignalR hub,
/// REST controller) calls to inject a message into the gateway. The orchestrator
/// owns the per-session FIFO queue, applies bounded backpressure, and delegates
/// the actual processing — resolution, session save, agent execution — to
/// <see cref="IInboundMessageProcessor"/> inside the queued worker.
/// </summary>
/// <remarks>
/// <para>
/// Resolution intentionally happens INSIDE the queued worker (not before
/// <see cref="AcceptAsync"/> enqueues) so that concurrent messages for the
/// same logical session cannot race on session creation, binding insertion,
/// or participant registration. The orchestrator is the single place that
/// guarantees per-session serialization for inbound work.
/// </para>
/// <para>
/// <see cref="AcceptAsync"/> awaits the worker's completion and returns the
/// post-processing <see cref="InboundDispatchResult"/>. Transports that need
/// to subscribe to outbound streams before the agent runs (e.g. SignalR hubs)
/// require a different shape — that is a separate concern intentionally not
/// covered by this PR.
/// </para>
/// </remarks>
public interface IInboundMessageOrchestrator
{
    /// <summary>
    /// Enqueue an inbound message for processing on its per-session queue and
    /// await completion. Returns an aggregate result describing whether the
    /// message was accepted, refused (queue full), routed nowhere, or rejected
    /// by the processor.
    /// </summary>
    /// <param name="message">Inbound message from the transport layer.</param>
    /// <param name="cancellationToken">
    /// Cancellation token tied to the originating transport call. Cancellation
    /// stops the caller's await on the queued completion — the processor work
    /// itself runs on a detached token so client disconnect does not kill
    /// in-progress agent execution (matches the legacy GatewayHost behaviour).
    /// </param>
    /// <returns>
    /// Aggregate outcome of processing. The per-agent <see cref="DispatchResult"/>
    /// list is populated only for <see cref="InboundDispatchStatus.Accepted"/>.
    /// </returns>
    Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fire-and-forget enqueue: writes the message onto the per-session queue and
    /// returns immediately without awaiting the processing outcome. Use this when
    /// the caller must not block on agent execution (e.g. the conversation tool
    /// seeding a message into another agent's conversation).
    /// </summary>
    /// <param name="message">Inbound message from the transport layer.</param>
    /// <returns>
    /// <see langword="true"/> if the message was accepted onto the queue;
    /// <see langword="false"/> if the queue was full (backpressure: caller may
    /// surface a busy indication and ask the user to retry).
    /// </returns>
    bool Post(InboundMessage message);
}
