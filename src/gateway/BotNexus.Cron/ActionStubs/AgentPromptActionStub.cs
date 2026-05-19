using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using BotNexus.Cron.Prompts;
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
        if (!string.IsNullOrWhiteSpace(context.Job.TemplateName))
        {
            var resolver = context.Services.GetService<IPromptTemplateResolver>()
                ?? throw new InvalidOperationException("Prompt template resolver is not registered.");

            if (!resolver.TryRender(agentId, context.Job.TemplateName, context.Job.TemplateParameters, out var renderedPrompt, out var error))
                throw new InvalidOperationException(error ?? $"Unable to render prompt template '{context.Job.TemplateName}'.");

            message = renderedPrompt;
        }

        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Cron job must define either a message or a templateName for agent-prompt actions.");

        var registry = context.Services.GetService<IAgentRegistry>();
        var descriptor = registry?.Get(AgentId.From(agentId));

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
            .CreateSessionAsync(
                AgentId.From(agentId),
                message,
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

    private static bool IsInQuietHours(QuietHoursConfig config, string timezoneId)
    {
        var tz = ResolveTimeZone(timezoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var currentTime = localNow.TimeOfDay;

        if (!TimeSpan.TryParse(config.Start, out var start) ||
            !TimeSpan.TryParse(config.End, out var end))
            return false;

        if (start <= end)
            return currentTime >= start && currentTime < end;

        return currentTime >= start || currentTime < end;
    }

    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
        => TimeZoneHelper.Resolve(timezoneId);
}
