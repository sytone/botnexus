using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Channels;

/// <summary>
/// Optional contract for channel adapters whose destinations are not universally addressable
/// (#3518). A fan-out binding can exist for a channel the adapter is genuinely unable to
/// deliver to — on Service Bus, a gateway-created binding addressed by AGENT ID has no external
/// wire address at all, so every envelope built for it is certain to be refused downstream.
/// </summary>
/// <remarks>
/// <para>
/// This is the delivery analogue of <see cref="IStreamEventChannelAdapter.CanSendStreamEvent"/>:
/// it lets the fan-out ask "can you address this at all?" BEFORE constructing an envelope, so an
/// unsatisfiable destination becomes a logged skip instead of a per-turn ERROR plus a thrown
/// exception that the deliverer can only swallow.
/// </para>
/// <para>
/// Returning <c>false</c> does NOT relax the adapter's own outbound guard. The guard remains the
/// authority on what may reach the wire; this probe only spares the caller from asking a question
/// whose answer is already known. Adapters that do not implement the interface are treated as
/// always addressable, preserving existing behaviour.
/// </para>
/// </remarks>
public interface IAddressableChannelAdapter
{
    /// <summary>
    /// Whether this adapter can deliver a message to <paramref name="channelAddress"/> when the
    /// fan-out has no external destination to offer beyond the binding itself.
    /// </summary>
    /// <param name="channelAddress">The binding address the fan-out intends to deliver to.</param>
    /// <param name="reason">
    /// On <c>false</c>, a short human-readable reason suitable for a DEBUG log line; otherwise <c>null</c>.
    /// </param>
    bool CanDeliverTo(ChannelAddress channelAddress, out string? reason);
}
