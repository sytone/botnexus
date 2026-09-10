using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Bridges raised notifications to SignalR so a connected portal updates without polling.
/// </summary>
/// <remarks>
/// Broadcast to ALL clients rather than a session group: a notification is about the gateway and
/// its agents, not about one conversation, and read state is shared server-side anyway. Failures
/// forwarding one notification are contained so the bridge keeps running - the store already has
/// it, so the worst case is a client learning about it on its next read instead of instantly.
/// </remarks>
public sealed class NotificationSignalRBridge(
    INotificationBroadcaster broadcaster,
    IHubContext<GatewayHub, IGatewayHubClient> hubContext,
    ILogger<NotificationSignalRBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NotificationSignalRBridge started.");

        try
        {
            await foreach (var notification in broadcaster.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await hubContext.Clients.All
                        .NotificationRaised(NotificationPayload.From(notification))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to forward the {Kind} notification to SignalR.",
                        notification.Kind);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        logger.LogInformation("NotificationSignalRBridge stopped.");
    }
}

/// <summary>One raised notification, shaped for the wire.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Kind">What it is about.</param>
/// <param name="Severity">How much attention it wants.</param>
/// <param name="Title">One-line summary.</param>
/// <param name="Body">Optional detail.</param>
/// <param name="AgentId">Agent concerned, when any.</param>
/// <param name="ConversationId">Conversation concerned, when any.</param>
/// <param name="Link">Where to go to act on it.</param>
/// <param name="CreatedAtUtc">When it was raised.</param>
public sealed record NotificationPayload(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string? Body,
    string? AgentId,
    string? ConversationId,
    string? Link,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Projects a notification for the wire. Kind and severity are NAMES, matching the REST API, so
    /// a client parses one shape whichever way the notification reached it.
    /// </summary>
    /// <param name="notification">The raised notification.</param>
    public static NotificationPayload From(Notification notification) => new(
        notification.Id,
        notification.Kind.ToString(),
        notification.Severity.ToString(),
        notification.Title,
        notification.Body,
        notification.AgentId,
        notification.ConversationId,
        notification.Link,
        notification.CreatedAtUtc);
}
