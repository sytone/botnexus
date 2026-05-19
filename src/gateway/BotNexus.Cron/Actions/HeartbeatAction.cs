using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591 // Public members implement framework contracts

/// <summary>
/// Dedicated cron action for system heartbeat jobs (actionType = "heartbeat").
/// Handles quiet-hours gating and heartbeat-specific trigger routing.
/// The quiet-hours check that was previously embedded as a heuristic inside
/// <see cref="AgentPromptAction"/> now lives exclusively here.
/// </summary>
public sealed class HeartbeatAction : ICronAction
{
    /// <inheritdoc/>
    public string ActionType => "heartbeat";

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agentId = context.Job.AgentId;
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Heartbeat cron job must define an agent id.");

        var registry = context.Services.GetService<IAgentRegistry>();
        var descriptor = registry?.Get(AgentId.From(agentId));

        var quietHours = descriptor?.Heartbeat?.QuietHours;
        var timezoneFallback = descriptor?.Soul?.Timezone ?? "UTC";
        if (quietHours is { Enabled: true }
            && IsInQuietHours(quietHours, quietHours.Timezone ?? timezoneFallback))
        {
            context.Services.GetService<ILogger<HeartbeatAction>>()?.LogDebug(
                "Skipping heartbeat for agent '{AgentId}' — quiet hours active.", agentId);
            return;
        }

        var prompt = context.Job.Message;
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Heartbeat cron job must define a message prompt.");

        var trigger = ResolveHeartbeatTrigger(context.Services, descriptor);

        var sessionId = await trigger
            .CreateSessionAsync(
                AgentId.From(agentId),
                prompt,
                cancellationToken,
                new InternalTriggerRequest
                {
                    CronJobId = context.Job.Id,
                    ModelOverride = context.Job.Model,
                    ConversationId = context.Job.ConversationId
                })
            .ConfigureAwait(false);

        context.RecordSessionId(sessionId);
    }

    /// <summary>
    /// Resolves the best available trigger for a heartbeat run.
    /// Priority: HeartbeatTrigger > SoulTrigger (soul agents) > CronTrigger.
    /// </summary>
    private static IInternalTrigger ResolveHeartbeatTrigger(IServiceProvider services, AgentDescriptor? descriptor)
    {
        var all = services.GetServices<IInternalTrigger>().ToList();

        // First choice: dedicated heartbeat trigger
        var heartbeatTrigger = all.FirstOrDefault(t => t.Type.Equals(TriggerType.Heartbeat));
        if (heartbeatTrigger is not null)
            return heartbeatTrigger;

        // Second choice: soul trigger for soul-enabled agents (preserves prior routing behaviour)
        if (descriptor?.Soul?.Enabled == true)
        {
            var soulTrigger = all.FirstOrDefault(t => t.Type.Equals(TriggerType.Soul));
            if (soulTrigger is not null)
                return soulTrigger;
        }

        // Fallback: standard cron trigger
        return all.FirstOrDefault(t => t.Type.Equals(TriggerType.Cron))
            ?? throw new InvalidOperationException(
                "No suitable internal trigger found for heartbeat action (heartbeat, soul, or cron).");
    }

    private static bool IsInQuietHours(QuietHoursConfig config, string timezoneId)
    {
        var tz = TimeZoneHelper.Resolve(timezoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var currentTime = localNow.TimeOfDay;

        if (!TimeSpan.TryParse(config.Start, out var start) ||
            !TimeSpan.TryParse(config.End, out var end))
            return false;

        // Overnight range e.g. 23:00–07:00
        if (start > end)
            return currentTime >= start || currentTime < end;

        return currentTime >= start && currentTime < end;
    }
}
