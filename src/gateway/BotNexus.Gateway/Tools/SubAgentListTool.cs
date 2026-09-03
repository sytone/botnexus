using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentListTool(
    ISubAgentManager subAgentManager,
    SessionId sessionId) : IAgentTool
{
    public string Name => "list_subagents";
    public string Label => "List Sub-Agents";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Gateway-owned sub-agent registry.</summary>
    public string ContentSource => ToolContentSource.Local;

    public Tool Definition => new(
        Name,
        "List active and completed sub-agents for the current session.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {}
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var subAgents = await subAgentManager.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // #3703: project rather than serializing SubAgentInfo directly so the delivery verdict is
        // rendered as text the calling model cannot mistake for a clean completion. A record whose
        // completion never reached this session says so on its face.
        var projected = subAgents
            .Select(info => new
            {
                info.SubAgentId,
                info.Name,
                info.Task,
                info.Model,
                info.Archetype,
                info.Status,
                info.StartedAt,
                info.CompletedAt,
                info.TurnsUsed,
                info.ResultSummary,
                info.BudgetClamp,
                // Emitted as text, not the raw enum: JsonOptions has no enum converter, so an
                // unprojected enum would reach the calling model as a bare integer.
                CompletionDelivery = info.CompletionDelivery.ToString(),
                info.CompletionDeliveryError,
                DeliveryWarning = info.CompletionDelivery == SubAgentCompletionDelivery.Failed
                    ? "This sub-agent finished but its completion announcement never reached this session. "
                      + "Treat the resultSummary here as the only copy of its result; no wake-up is coming."
                    : null
            })
            .ToArray();

        var result = JsonSerializer.Serialize(new { SubAgents = projected }, JsonOptions);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
