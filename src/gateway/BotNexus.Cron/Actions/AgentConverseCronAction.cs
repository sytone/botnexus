using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

/// <summary>
/// Executes a cron job by initiating an agent-to-agent conversation via <see cref="IAgentExchangeService"/>.
/// Action type: "agent-converse".
/// </summary>
/// <remarks>
/// Configuration is read from <see cref="CronJob.Metadata"/>:
/// <list type="bullet">
///   <item><c>targetAgentId</c> (required): the agent to converse with.</item>
///   <item><c>message</c> (optional, falls back to <see cref="CronJob.Message"/>): opening message.</item>
///   <item><c>objective</c> (optional): conversation objective.</item>
///   <item><c>maxTurns</c> (optional, default 5): maximum conversation turns.</item>
/// </list>
/// Budget limits from <c>AgentExchangeBudgetTracker</c> apply identically to interactive exchanges.
/// </remarks>
public sealed class AgentConverseCronAction : ICronAction
{
    /// <summary>Default maximum turns if not specified in job metadata.</summary>
    internal const int DefaultMaxTurns = 5;

    /// <inheritdoc/>
    public string ActionType => "agent-converse";

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var initiatorAgentId = context.Job.AgentId
            ?? throw new InvalidOperationException(
                $"Cron job '{context.Job.Id}' has action type 'agent-converse' but AgentId is null.");

        var targetAgentId = ResolveMetadataString(context.Job, "targetAgentId")
            ?? throw new InvalidOperationException(
                $"Cron job '{context.Job.Id}' requires 'targetAgentId' in metadata for agent-converse actions.");

        var message = ResolveMetadataString(context.Job, "message") ?? context.Job.Message;
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException(
                $"Cron job '{context.Job.Id}' requires a message (in metadata or job Message) for agent-converse actions.");

        var objective = ResolveMetadataString(context.Job, "objective");
        var maxTurns = ResolveMetadataInt(context.Job, "maxTurns", DefaultMaxTurns);

        var exchangeService = context.Services.GetService<IAgentExchangeService>()
            ?? throw new InvalidOperationException("IAgentExchangeService is not registered.");

        var logger = context.Services.GetService<ILogger<AgentConverseCronAction>>();

        logger?.LogInformation(
            "AgentConverseCronAction: job '{JobId}' initiating conversation from '{Initiator}' to '{Target}' (maxTurns={MaxTurns}).",
            context.Job.Id, initiatorAgentId.Value, targetAgentId, maxTurns);

        var request = new AgentExchangeRequest
        {
            InitiatorId = initiatorAgentId,
            TargetId = AgentId.From(targetAgentId),
            Message = message,
            Objective = objective,
            MaxTurns = maxTurns,
            CallChain = [initiatorAgentId]
        };

        var result = await exchangeService.ConverseAsync(request, cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "AgentConverseCronAction: job '{JobId}' completed. Status={Status}, Turns={Turns}.",
            context.Job.Id, result.Status, result.Turns);
    }

    private static string? ResolveMetadataString(CronJob job, string key)
    {
        if (job.Metadata is null) return null;
        if (!job.Metadata.TryGetValue(key, out var value)) return null;
        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
            _ => value?.ToString()
        };
    }

    private static int ResolveMetadataInt(CronJob job, string key, int defaultValue)
    {
        var raw = ResolveMetadataString(job, key);
        if (raw is null) return defaultValue;
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}