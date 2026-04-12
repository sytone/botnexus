using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;

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
}
