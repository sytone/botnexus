// IEventDeliveryHandler.cs
using BotNexus.Gateway.Contracts.Events;

namespace BotNexus.Gateway.Events;

/// <summary>
/// Handles delivery of world events to subscribing agents. The default implementation
/// creates a new conversation session on the target agent with the event context as
/// the opening message.
/// </summary>
public interface IEventDeliveryHandler
{
    /// <summary>
    /// Delivers a world event to the specified agent.
    /// </summary>
    /// <param name="agentId">The target agent to notify.</param>
    /// <param name="worldEvent">The event to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeliverAsync(string agentId, WorldEvent worldEvent, CancellationToken cancellationToken = default);
}
