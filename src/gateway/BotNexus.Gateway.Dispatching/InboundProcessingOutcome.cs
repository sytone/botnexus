namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Outcome returned from <see cref="IInboundMessageProcessor.ProcessAsync"/>
/// describing what the processor did with the message and whether the
/// orchestrator's per-session queue should now close (e.g., session sealed).
/// </summary>
/// <param name="Dispatches">
/// Per-agent dispatch results captured while the processor ran the message.
/// Empty when the router resolved zero target agents.
/// </param>
/// <param name="ShouldClosePerSessionQueue">
/// <c>true</c> when the processed message left the addressed session in a
/// terminal state (sealed/closed). The orchestrator uses this signal to
/// complete the per-session queue's writer so no further work is enqueued
/// against a dead session. Keeps the queue cleanup logic owned by the
/// component that knows about <c>SessionStatus</c>.
/// </param>
public sealed record InboundProcessingOutcome(
    IReadOnlyList<DispatchResult> Dispatches,
    bool ShouldClosePerSessionQueue);
