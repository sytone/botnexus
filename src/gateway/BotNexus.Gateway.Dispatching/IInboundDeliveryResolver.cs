using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// The single server-side seam that decides whether an inbound message steers a running turn,
/// interrupts it, or joins the per-isolation-unit FIFO queue (#3028 AC1).
/// </summary>
/// <remarks>
/// <para>
/// The decision has two inputs and only one of them belongs to the caller. The caller supplies
/// <em>intent</em> via <see cref="InboundMessageRoutingHints.DeliveryMode"/>; the gateway supplies
/// the <em>evidence</em> — whether a live agent handle exists for the addressed session and is
/// actually running. A client cannot observe the second input reliably (it sees a stream state that
/// may already be stale), which is precisely why the pre-#3028 arrangement, where a Razor component
/// chose the hub method, produced different semantics on the desktop and mobile clients for the
/// same user action.
/// </para>
/// <para>
/// Implementations must be side-effect free: this seam answers a question, it does not deliver the
/// message. <see cref="DefaultInboundMessageOrchestrator"/> owns the delivery.
/// </para>
/// </remarks>
public interface IInboundDeliveryResolver
{
    /// <summary>
    /// Resolves the requested delivery mode to the mechanism that will actually be used.
    /// </summary>
    /// <param name="message">The inbound message, carrying the requested mode in its routing hints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved decision. Never <see cref="InboundDeliveryMode.Auto"/>: <c>Auto</c> is an input
    /// intent, and the resolver's job is to collapse it to a concrete mechanism.
    /// </returns>
    Task<InboundDeliveryDecision> ResolveAsync(
        InboundMessage message,
        CancellationToken cancellationToken = default);
}
