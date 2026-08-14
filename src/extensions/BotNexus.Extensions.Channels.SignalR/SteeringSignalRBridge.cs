using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Bridges steering activity events from <see cref="IActivityBroadcaster"/> to SignalR
/// session groups so the web UI receives real-time steering feedback.
/// </summary>
public sealed class SteeringSignalRBridge(
    IActivityBroadcaster activity,
    IHubContext<GatewayHub, IGatewayHubClient> hubContext,
    ILogger<SteeringSignalRBridge> logger) : BackgroundService
{
    private static readonly HashSet<GatewayActivityType> SteeringEventTypes =
    [
        GatewayActivityType.SteeringInjected,
        GatewayActivityType.SteeringQueued
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SteeringSignalRBridge started.");

        try
        {
            await foreach (var evt in activity.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!SteeringEventTypes.Contains(evt.Type))
                    continue;

                try
                {
                    await ForwardToSignalRAsync(evt, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to forward steering event {EventType} to SignalR.", evt.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        logger.LogInformation("SteeringSignalRBridge stopped.");
    }

    private async Task ForwardToSignalRAsync(GatewayActivity evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId) || string.IsNullOrWhiteSpace(evt.AgentId))
            return;

        var kind = evt.Type == GatewayActivityType.SteeringInjected
            ? SteeringFeedbackKind.Injected
            : SteeringFeedbackKind.Queued;

        var payload = new SteeringFeedbackPayload(evt.AgentId, evt.SessionId, kind, evt.ConversationId);

        // #3065: steering feedback is conversation-scoped, so it must name its conversation.
        // GatewayHost now stamps ConversationId from the resolved ConversationSessionResolution on
        // BOTH steering activity events, so the historical "conversation:{sessionId}" synonym is
        // dead weight: it addressed a group no client ever subscribes to (clients join by
        // conversation id), which made the send look delivered while reaching nobody, and left the
        // portal to attribute the feedback to whatever conversation happened to be active.
        if (string.IsNullOrWhiteSpace(evt.ConversationId))
        {
            logger.LogError(
                "Refusing to forward conversation-scoped {EventType} for session '{SessionId}': no conversation "
                + "id on the activity event. A conversation-scoped event must name its conversation (#3065).",
                evt.Type,
                evt.SessionId);
            return;
        }

        var group = SignalRChannelAdapter.GetConversationGroup(evt.ConversationId);

        logger.LogDebug("Forwarding {EventType} for session '{SessionId}' to group '{Group}'.",
            evt.Type, evt.SessionId, group);

        await hubContext.Clients.Group(group).SteeringFeedback(payload).ConfigureAwait(false);
    }
}
