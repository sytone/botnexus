using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Pushes every raised notification to registered iOS devices.
/// </summary>
/// <remarks>
/// The third subscriber on the broadcaster, beside SignalR and web push. Each delivery layer is
/// independent by design: adding one does not touch the publisher, and one failing does not affect
/// the others.
/// </remarks>
public sealed class ApnsBridge(
    INotificationBroadcaster broadcaster,
    ApnsSender sender,
    ApnsOptions options,
    ILogger<ApnsBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Said once, at Information, so an operator who expects iOS notifications can see at a
        // glance whether the gateway believes it is configured for them.
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "ApnsBridge is idle: gateway:apns is not configured, so iOS devices will not be pushed to.");

            return;
        }

        logger.LogInformation("ApnsBridge started for bundle {BundleId}.", options.BundleId);

        try
        {
            await foreach (var notification in broadcaster.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var delivered = await sender.SendAsync(notification, stoppingToken).ConfigureAwait(false);

                    if (delivered > 0)
                        logger.LogInformation("Pushed a notification to {Count} iOS device(s).", delivered);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to push the {Kind} notification to iOS.", notification.Kind);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        logger.LogInformation("ApnsBridge stopped.");
    }
}
