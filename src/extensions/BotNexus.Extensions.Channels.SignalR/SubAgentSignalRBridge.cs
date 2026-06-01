using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

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
        // PR1.5 (#682): route by conversation so the connection's subscription survives
        // post-compaction session swaps. Activity emitters that haven't been updated yet
        // (no ConversationId on the activity) fall back to "conversation:{sessionId}" —
        // the same back-compat synonym the hub uses for legacy JoinSession/SubscribeAll.
        var conversationKey = !string.IsNullOrWhiteSpace(evt.ConversationId)
            ? evt.ConversationId
            : parentSessionId;
        var group = SignalRChannelAdapter.GetConversationGroup(conversationKey);

        var taskSummary = subAgent.Task.Length > 120
            ? subAgent.Task[..120].TrimEnd() + "\u2026"
            : subAgent.Task;

        var payload = new SubAgentEventPayload(
            parentSessionId,
            subAgent.SubAgentId,
            subAgent.Name,
            taskSummary,
            subAgent.Model,
            subAgent.Archetype.Value,
            subAgent.Status.ToString(),
            subAgent.StartedAt,
            subAgent.CompletedAt,
            subAgent.TurnsUsed,
            subAgent.ResultSummary,
            subAgent.Status == SubAgentStatus.TimedOut,
            subAgent.ChildSessionId.Value,
            evt.ConversationId);

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
