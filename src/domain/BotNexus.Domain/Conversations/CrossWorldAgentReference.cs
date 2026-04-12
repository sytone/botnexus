using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Conversations;

public sealed record CrossWorldAgentReference
{
    public required string WorldId { get; init; }
    public required AgentId AgentId { get; init; }

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
        var agentId = value[(separatorIndex + 1)..].Trim();
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
