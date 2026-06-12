// IWorldEventBus.cs
namespace BotNexus.Gateway.Contracts.Events;

/// <summary>
/// Lightweight event bus that lets agents react to things happening in the world
/// without polling. Agents declare interest via <see cref="EventSubscription"/> and
/// receive triggered conversations when matching events fire.
/// </summary>
public interface IWorldEventBus
{
    /// <summary>
    /// Publishes an event to all matching subscribers.
    /// </summary>
    /// <param name="worldEvent">The event to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of subscribers that were notified.</returns>
    Task<int> PublishAsync(WorldEvent worldEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers subscriptions for an agent. Replaces any existing subscriptions for that agent.
    /// </summary>
    /// <param name="agentId">The subscribing agent's identifier.</param>
    /// <param name="subscriptions">The subscriptions to register.</param>
    void SetSubscriptions(string agentId, IReadOnlyList<EventSubscription> subscriptions);

    /// <summary>
    /// Gets all active subscriptions for a given agent.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <returns>The agent's subscriptions, or empty if none registered.</returns>
    IReadOnlyList<EventSubscription> GetSubscriptions(string agentId);

    /// <summary>
    /// Gets all agents subscribed to a given event type.
    /// </summary>
    /// <param name="eventType">The event type to query.</param>
    /// <returns>Agent IDs with active subscriptions for this event type.</returns>
    IReadOnlyList<string> GetSubscribers(string eventType);
}
