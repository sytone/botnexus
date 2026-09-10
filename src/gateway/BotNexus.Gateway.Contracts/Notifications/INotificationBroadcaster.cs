namespace BotNexus.Gateway.Abstractions.Notifications;

/// <summary>
/// Fans raised notifications out to live subscribers, so a connected client learns about one
/// without polling for it.
/// </summary>
/// <remarks>
/// The STORE is the source of truth, not this stream. A subscriber that misses a broadcast - it was
/// slow, or was not connected - loses nothing permanent, because the notification is already
/// persisted and will be there on the next read. That is what makes dropping for a slow subscriber
/// the right behaviour rather than a compromise, and it is the difference between this and a
/// delivery mechanism.
/// </remarks>
public interface INotificationBroadcaster
{
    /// <summary>Publishes to all current subscribers. Fire-and-forget; slow subscribers are skipped.</summary>
    /// <param name="notification">The notification to fan out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to future notifications. The sequence completes when the token is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<Notification> SubscribeAsync(CancellationToken cancellationToken = default);
}
