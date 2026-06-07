using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.AgentExchange;

/// <summary>
/// Represents cross world agent reference.
/// </summary>
public sealed record CrossWorldAgentReference
{
    /// <summary>
    /// Gets or sets the world id.
    /// </summary>
    public required string WorldId { get; init; }
    /// <summary>
    /// Gets or sets the agent id.
    /// </summary>
    public required AgentId AgentId { get; init; }

    /// <summary>
    /// Executes try parse.
    /// </summary>
    /// <param name="rawAgentId">The raw agent id.</param>
    /// <param name="reference">The reference.</param>
    /// <returns>The try parse result.</returns>
    public static bool TryParse(AgentId rawAgentId, out CrossWorldAgentReference? reference)
    {
        var value = rawAgentId.Value;
        var separatorIndex = value.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            reference = null;
            return false;
        }

        var worldId = value[..separatorIndex].Trim();
        var agentId = value[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(agentId))
        {
            reference = null;
            return false;
        }

        reference = new CrossWorldAgentReference
        {
            WorldId = worldId,
            AgentId = AgentId.From(agentId)
        };
        return true;
    }
}
