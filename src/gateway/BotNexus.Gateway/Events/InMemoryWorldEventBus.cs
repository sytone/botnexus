// InMemoryWorldEventBus.cs
using System.Collections.Concurrent;
using BotNexus.Gateway.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Events;

/// <summary>
/// In-memory implementation of <see cref="IWorldEventBus"/>. Subscriptions are held
/// in a concurrent dictionary keyed by agent ID. When an event is published, all
/// matching subscribers are notified via the configured <see cref="IEventDeliveryHandler"/>.
/// </summary>
public sealed class InMemoryWorldEventBus : IWorldEventBus
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<EventSubscription>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEventDeliveryHandler _deliveryHandler;
    private readonly ILogger<InMemoryWorldEventBus> _logger;

    /// <summary>Creates a new in-memory event bus.</summary>
    public InMemoryWorldEventBus(IEventDeliveryHandler deliveryHandler, ILogger<InMemoryWorldEventBus> logger)
    {
        _deliveryHandler = deliveryHandler ?? throw new ArgumentNullException(nameof(deliveryHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<int> PublishAsync(WorldEvent worldEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);

        int notifiedCount = 0;

        foreach (var (agentId, subscriptions) in _subscriptions)
        {
            foreach (var subscription in subscriptions)
            {
                if (subscription.Matches(worldEvent))
                {
                    try
                    {
                        await _deliveryHandler.DeliverAsync(agentId, worldEvent, cancellationToken).ConfigureAwait(false);
                        notifiedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deliver event {EventType} to agent {AgentId}", worldEvent.EventType, agentId);
                    }

                    // Only deliver once per agent even if multiple subscriptions match.
                    break;
                }
            }
        }

        _logger.LogInformation("Published event {EventType} to {NotifiedCount} subscriber(s)", worldEvent.EventType, notifiedCount);
        return notifiedCount;
    }

    /// <inheritdoc/>
    public void SetSubscriptions(string agentId, IReadOnlyList<EventSubscription> subscriptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(subscriptions);

        if (subscriptions.Count == 0)
            _subscriptions.TryRemove(agentId, out _);
        else
            _subscriptions[agentId] = subscriptions;
    }

    /// <inheritdoc/>
    public IReadOnlyList<EventSubscription> GetSubscriptions(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _subscriptions.TryGetValue(agentId, out var subs) ? subs : [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetSubscribers(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var result = new List<string>();
        foreach (var (agentId, subscriptions) in _subscriptions)
        {
            if (subscriptions.Any(s => string.Equals(s.EventType, eventType, StringComparison.OrdinalIgnoreCase)))
                result.Add(agentId);
        }

        return result;
    }
}
