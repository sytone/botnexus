using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>A scoped compaction trigger ceiling and its owning configuration layer; null means no ceiling.</summary>
public sealed record ScopedCompactionCeiling(int? Tokens, string? Source);

/// <summary>Resolves current conversation, agent and world ceilings without changing model capacity.</summary>
public interface ISessionCompactionCeilingResolver
{
    /// <summary>Reads fresh policy for a conversation. Cancellation and read failures propagate rather than silently relaxing policy.</summary>
    Task<ScopedCompactionCeiling> ResolveAsync(AgentId agentId, ConversationId conversationId, CancellationToken cancellationToken = default);
}
