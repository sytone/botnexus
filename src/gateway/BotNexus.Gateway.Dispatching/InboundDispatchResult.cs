namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Aggregate outcome returned from <see cref="IInboundMessageOrchestrator.AcceptAsync"/>
/// once an inbound message has been queued, resolved, and processed by every target
/// agent. Carries the per-agent <see cref="DispatchResult"/> list alongside a
/// transport-friendly <see cref="InboundDispatchStatus"/>.
/// </summary>
/// <param name="Status">High-level outcome (accepted, no route, busy, rejected).</param>
/// <param name="Dispatches">
/// Zero or more per-agent dispatch results captured while the worker processed
/// the message. Empty for <see cref="InboundDispatchStatus.NoRoute"/>,
/// <see cref="InboundDispatchStatus.Busy"/>, and <see cref="InboundDispatchStatus.Rejected"/>.
/// </param>
public sealed record InboundDispatchResult(
    InboundDispatchStatus Status,
    IReadOnlyList<DispatchResult> Dispatches)
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    /// <summary>
    /// Convenience factory for an accepted outcome with the supplied per-agent
    /// dispatch results.
    /// </summary>
    public static InboundDispatchResult Accepted(IReadOnlyList<DispatchResult> dispatches) =>
        new(InboundDispatchStatus.Accepted, dispatches);

    /// <summary>
    /// Convenience factory for a no-route outcome (router resolved zero agents).
    /// </summary>
    public static InboundDispatchResult NoRoute() =>
        new(InboundDispatchStatus.NoRoute, EmptyDispatches);

    /// <summary>
    /// Convenience factory for a busy outcome (per-session queue was full).
    /// </summary>
    public static InboundDispatchResult Busy() =>
        new(InboundDispatchStatus.Busy, EmptyDispatches);

    /// <summary>
    /// Convenience factory for a rejected outcome (processor raised). The
    /// originating exception is rethrown to the caller — this factory is
    /// primarily a placeholder for callers that catch and inspect.
    /// </summary>
    public static InboundDispatchResult Rejected() =>
        new(InboundDispatchStatus.Rejected, EmptyDispatches);
}
