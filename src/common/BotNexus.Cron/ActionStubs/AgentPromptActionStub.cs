using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591 // Public members implement framework contracts

/// <summary>
/// Executes a cron job by triggering an internal gateway session.
/// </summary>
public sealed class AgentPromptAction : ICronAction
{
    public string ActionType => "agent-prompt";

    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var agentId = context.Job.AgentId;
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Cron job must define an agent id for agent-prompt actions.");

        var message = context.Job.Message;
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Cron job must define a message for agent-prompt actions.");

        var registry = context.Services.GetService<IAgentRegistry>();
        var descriptor = registry?.Get(AgentId.From(agentId));

        if (context.Job.System
            && context.Job.Id.StartsWith("heartbeat:", StringComparison.OrdinalIgnoreCase)
            && descriptor?.Heartbeat?.QuietHours is { Enabled: true } quietHours)
        {
            if (IsInQuietHours(quietHours, quietHours.Timezone ?? descriptor.Soul?.Timezone ?? "UTC"))
            {
                context.Services.GetService<ILogger<AgentPromptAction>>()?.LogDebug(
                    "Skipping heartbeat for agent '{AgentId}' — quiet hours active.", agentId);
                return;
            }
        }

        var preferredTriggerType = descriptor?.Soul?.Enabled == true
            ? TriggerType.Soul
            : TriggerType.Cron;

        var trigger = context.Services.GetServices<IInternalTrigger>()
            .FirstOrDefault(candidate => candidate.Type.Equals(preferredTriggerType))
            ?? throw new InvalidOperationException(
                preferredTriggerType.Equals(TriggerType.Soul)
                    ? "Soul internal trigger is not registered."
                    : "Cron internal trigger is not registered.");

        var sessionId = await trigger
            .CreateSessionAsync(AgentId.From(agentId), message, cancellationToken)
            .ConfigureAwait(false);

        context.RecordSessionId(sessionId);
    }

    private static bool IsInQuietHours(QuietHoursConfig config, string timezoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var currentTime = localNow.TimeOfDay;

            if (!TimeSpan.TryParse(config.Start, out var start) ||
                !TimeSpan.TryParse(config.End, out var end))
                return false;

            if (start <= end)
                return currentTime >= start && currentTime < end;

            return currentTime >= start || currentTime < end;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
