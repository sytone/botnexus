using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Activity;

/// <summary>
/// Broadcasts real-time activity events to subscribers (WebSocket clients, monitoring tools).
/// Uses a fan-out pattern: each subscriber gets their own bounded channel with drop-oldest
/// semantics to prevent slow consumers from blocking the Gateway.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the archive's <c>IActivityStream</c> with a cleaner contract.
/// The subscriber model uses <see cref="IAsyncEnumerable{T}"/> instead of requiring
/// explicit subscription management.
/// </para>
/// </remarks>
public interface IActivityBroadcaster
{
    /// <summary>
    /// Publishes an activity event to all current subscribers.
    /// This is fire-and-forget for the publisher — slow subscribers are skipped.
    /// </summary>
    /// <param name="activity">The activity event to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new subscription that receives all future activity events.
    /// The returned <see cref="IAsyncEnumerable{T}"/> completes when the
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to end the subscription.</param>
    /// <returns>An async stream of activity events.</returns>
    IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default);
}
