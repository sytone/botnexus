using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Pushes every raised notification to subscribed devices.
/// </summary>
/// <remarks>
/// Mirrors the SignalR bridge, and for the same reason: the publisher should not know or care how
/// many ways a notification is delivered. This one reaches devices the SignalR bridge cannot -
/// a browser with the portal closed, or a phone.
/// </remarks>
public sealed class WebPushBridge(
    INotificationBroadcaster broadcaster,
    WebPushSender sender,
    ILogger<WebPushBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WebPushBridge started.");

        try
        {
            await foreach (var notification in broadcaster.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var delivered = await sender.SendAsync(notification, stoppingToken).ConfigureAwait(false);

                    if (delivered > 0)
                        logger.LogInformation("Pushed a notification to {Count} subscriber(s).", delivered);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Contained per notification: one bad subscription must not stop the bridge,
                    // because that would silently end delivery for every other device.
                    logger.LogWarning(ex, "Failed to push the {Kind} notification.", notification.Kind);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        logger.LogInformation("WebPushBridge stopped.");
    }
}
