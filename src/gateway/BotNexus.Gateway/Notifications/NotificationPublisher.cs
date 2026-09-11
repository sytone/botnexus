using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// Writes raised notifications to the store, swallowing failures.
/// </summary>
public sealed class NotificationPublisher(
    INotificationStore store,
    INotificationBroadcaster? broadcaster = null,
    ILogger<NotificationPublisher>? logger = null) : INotificationPublisher
{
    private readonly INotificationStore _store = store;
    private readonly INotificationBroadcaster? _broadcaster = broadcaster;
    private readonly ILogger<NotificationPublisher> _logger = logger ?? NullLogger<NotificationPublisher>.Instance;

    /// <inheritdoc />
    public async Task PublishAsync(Notification notification, CancellationToken ct = default)
    {
        if (notification is null)
            return;

        try
        {
            // Stored FIRST, then broadcast. A client that misses the push still finds it on the
            // next read; a client pushed something that was never stored would show a notification
            // that vanishes on refresh.
            var stored = await _store.AppendAsync(notification, ct).ConfigureAwait(false);

            if (_broadcaster is not null)
                await _broadcaster.PublishAsync(stored, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged, not rethrown: the caller is in the middle of doing the actual work, and a
            // failure to record a note about it is not a reason to fail that work.
            _logger.LogWarning(
                ex,
                "Could not record the {Kind} notification '{Title}'.",
                notification.Kind,
                notification.Title);
        }
    }
}
