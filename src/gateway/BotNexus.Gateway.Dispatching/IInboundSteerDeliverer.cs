using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Delivers an inbound message into a turn that is already running, for the cases where
/// <see cref="IInboundDeliveryResolver"/> resolved <see cref="InboundDeliveryMode.Steer"/> or
/// <see cref="InboundDeliveryMode.Interrupt"/> (#3028).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a separate seam from the resolver. The resolver answers a question and has
/// no side effects; this performs the injection. Keeping them apart means the decision can be
/// unit-tested without a live agent, and means the orchestrator depends on the <em>capability</em>
/// to steer rather than on the isolation strategy that implements it.
/// </para>
/// <para>
/// Implementations live above <c>BotNexus.Gateway.Dispatching</c> because injecting into a turn
/// requires the agent handle, session persistence, and the interrupt path — none of which this
/// assembly references. <c>BotNexus.Gateway</c> supplies the concrete implementation at composition
/// time.
/// </para>
/// </remarks>
public interface IInboundSteerDeliverer
{
    /// <summary>
    /// Injects the message into the running turn addressed by the message's routing hints.
    /// </summary>
    /// <param name="message">The inbound message to inject.</param>
    /// <param name="decision">
    /// The resolved decision, so the implementation knows whether to steer or to interrupt-and-steer
    /// without re-deriving the choice (and possibly deriving it differently).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the message was injected into a running turn.
    /// <see langword="false"/> when it could not be — for example the turn ended between the
    /// resolver's check and this call. A <see langword="false"/> return is NOT an error: the
    /// orchestrator falls back to queueing so the message is never silently lost.
    /// </returns>
    Task<bool> TryDeliverAsync(
        InboundMessage message,
        InboundDeliveryDecision decision,
        CancellationToken cancellationToken = default);
}
