using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.SignalR;

/// <summary>
/// Bridges sub-agent lifecycle events from <see cref="IActivityBroadcaster"/> to SignalR
/// session groups so the web UI receives real-time spawned/completed/failed/killed updates.
/// </summary>
public sealed class SubAgentSignalRBridge(
    IActivityBroadcaster activity,
    IHubContext<GatewayHub, IGatewayHubClient> hubContext,
    ILogger<SubAgentSignalRBridge> logger) : BackgroundService
{
    private static readonly HashSet<GatewayActivityType> SubAgentEventTypes =
    [
        GatewayActivityType.SubAgentSpawned,
        GatewayActivityType.SubAgentCompleted,
        GatewayActivityType.SubAgentFailed,
        GatewayActivityType.SubAgentKilled
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SubAgentSignalRBridge started.");

        try
        {
            await foreach (var evt in activity.SubscribeAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!SubAgentEventTypes.Contains(evt.Type))
                    continue;

                try
                {
                    await ForwardToSignalRAsync(evt, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to forward sub-agent event {EventType} to SignalR.", evt.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        logger.LogInformation("SubAgentSignalRBridge stopped.");
    }

    private async Task ForwardToSignalRAsync(GatewayActivity evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return;

        // Extract SubAgentInfo from the activity data
        SubAgentInfo? subAgent = null;
        if (evt.Data?.TryGetValue("subAgent", out var subAgentObj) == true && subAgentObj is SubAgentInfo info)
            subAgent = info;

        if (subAgent is null)
        {
            logger.LogWarning("Sub-agent event {EventType} missing SubAgentInfo in Data.", evt.Type);
            return;
        }

        var parentSessionId = evt.SessionId;
        var group = $"session:{parentSessionId}";

        var payload = new SubAgentEventPayload(
            parentSessionId,
            subAgent.SubAgentId,
            subAgent.Name,
            subAgent.Task,
            subAgent.Model,
            subAgent.Archetype.Value,
            subAgent.Status.ToString(),
            subAgent.StartedAt,
            subAgent.CompletedAt,
            subAgent.TurnsUsed,
            subAgent.ResultSummary,
            subAgent.Status == SubAgentStatus.TimedOut);

        logger.LogDebug("Forwarding {EventType} for sub-agent '{SubAgentId}' to group '{Group}'.",
            evt.Type, subAgent.SubAgentId, group);

        var client = hubContext.Clients.Group(group);
        switch (evt.Type)
        {
            case GatewayActivityType.SubAgentSpawned:
                await client.SubAgentSpawned(payload).ConfigureAwait(false);
                break;
            case GatewayActivityType.SubAgentCompleted:
                await client.SubAgentCompleted(payload).ConfigureAwait(false);
                break;
            case GatewayActivityType.SubAgentFailed:
                await client.SubAgentFailed(payload).ConfigureAwait(false);
                break;
            case GatewayActivityType.SubAgentKilled:
                await client.SubAgentKilled(payload).ConfigureAwait(false);
                break;
        }
    }
}
