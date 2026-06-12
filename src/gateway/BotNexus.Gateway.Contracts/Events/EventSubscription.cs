// EventSubscription.cs
namespace BotNexus.Gateway.Contracts.Events;

/// <summary>
/// An agent's subscription to a specific event type with optional filter criteria.
/// </summary>
/// <param name="EventType">The event type to subscribe to (must match <see cref="WorldEvent.EventType"/>).</param>
/// <param name="Filter">Optional key-value filter that must match event payload for delivery. Null means match all events of this type.</param>
public sealed record EventSubscription(string EventType, IReadOnlyDictionary<string, string>? Filter = null)
{
    /// <summary>
    /// Determines whether a given event matches this subscription.
    /// </summary>
    public bool Matches(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);

        if (!string.Equals(EventType, worldEvent.EventType, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Filter is null || Filter.Count == 0)
            return true;

        // All filter keys must match in the event payload.
        foreach (var (key, value) in Filter)
        {
            if (!worldEvent.Payload.TryGetValue(key, out var payloadValue))
                return false;
            if (!string.Equals(value, payloadValue, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
