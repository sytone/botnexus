using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591 // Public members implement framework contracts

/// <summary>
/// Executes a cron job by dispatching a prompt through the gateway channel pipeline.
/// </summary>
public sealed class AgentPromptAction : ICronAction
{
    public const string CronChannelType = "cron";

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

        var dispatcher = context.Services.GetService(typeof(IChannelDispatcher)) as IChannelDispatcher
            ?? throw new InvalidOperationException("IChannelDispatcher is not registered.");

        var sessionId = $"cron:{context.Job.Id}:{context.RunId}";
        var inbound = new InboundMessage
        {
            ChannelType = ChannelKey.From(CronChannelType),
            SenderId = $"cron:{context.Job.Id}",
            ConversationId = sessionId,
            SessionId = sessionId,
            TargetAgentId = agentId,
            Content = message,
            Metadata = new Dictionary<string, object?>
            {
                ["source"] = "cron",
                ["jobId"] = context.Job.Id,
                ["runId"] = context.RunId
            }
        };

        await dispatcher.DispatchAsync(inbound, cancellationToken).ConfigureAwait(false);
        context.RecordSessionId(sessionId);
    }
}
