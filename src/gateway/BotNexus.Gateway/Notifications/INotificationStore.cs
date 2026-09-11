using BotNexus.Gateway.Abstractions.Notifications;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// Stores notifications so every client - the portal today, a phone or desktop app later - reads
/// the same list and the same read state.
/// </summary>
public interface INotificationStore
{
    /// <summary>Creates the store if it does not exist yet.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Records a notification and returns it as stored.</summary>
    /// <param name="notification">The notification to record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Notification> AppendAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Lists notifications, newest first.
    /// </summary>
    /// <param name="includeRead">When false, returns only unread notifications.</param>
    /// <param name="limit">Maximum to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Notification>> ListAsync(
        bool includeRead = true,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Number of unread notifications, for a badge.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> UnreadCountAsync(CancellationToken ct = default);

    /// <summary>Marks one notification read. Returns false when the id is unknown.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> MarkReadAsync(string id, CancellationToken ct = default);

    /// <summary>Marks every unread notification read and returns how many changed.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> MarkAllReadAsync(CancellationToken ct = default);

    /// <summary>Deletes one notification. Returns false when the id is unknown.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Deletes read notifications older than the given age, returning how many were removed.
    /// </summary>
    /// <remarks>
    /// An append-only log of every agent run would grow without bound on a gateway that runs cron
    /// jobs overnight - tonight's own gateway raised three agent handles at 04:00 unattended. Only
    /// READ notifications are eligible: pruning something nobody has seen would silently discard
    /// the very thing the feature exists to surface.
    /// </remarks>
    /// <param name="olderThan">Age beyond which a read notification may be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> PruneReadAsync(TimeSpan olderThan, CancellationToken ct = default);
}
