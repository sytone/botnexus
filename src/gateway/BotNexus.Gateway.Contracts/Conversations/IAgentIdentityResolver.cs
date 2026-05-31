using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Resolves the owning <see cref="AgentId"/> for a conversation or session. Replaces
/// the deleted <c>Session.AgentId</c> / <c>GatewaySession.AgentId</c> facade members:
/// agent ownership lives on the <see cref="Conversation"/> only, and every consumer that
/// previously read <c>session.AgentId</c> must now go through this resolver so the lookup
/// path is explicit and cacheable.
/// </summary>
/// <remarks>
/// <para>
/// Introduced in P9-H (issue #662) per directive W-4
/// ("The conversation has the Agent, Participants, etc. They should not be on the session.").
/// The implementation is expected to cache by <see cref="ConversationId"/>; the cache is safe
/// because <see cref="Conversation.AgentId"/> is treated as write-once / init-only post P9-H
/// (verified by <c>ConversationAgentIdImmutabilityArchitectureTests</c>).
/// </para>
/// <para>
/// Implementations must be thread-safe.
/// </para>
/// </remarks>
public interface IAgentIdentityResolver
{
    /// <summary>
    /// Returns the agent that owns the supplied conversation, or <c>null</c> if the
    /// conversation does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AgentId?> GetAgentIdAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the agent that owns the supplied conversation. Throws
    /// <see cref="InvalidOperationException"/> when the conversation cannot be resolved —
    /// callers should use <see cref="GetAgentIdAsync"/> when the missing-conversation case is
    /// part of their normal control flow.
    /// </summary>
    /// <param name="conversationId">The conversation to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AgentId> GetRequiredAgentIdAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Invalidates any cached entry for the supplied conversation. Call from
    /// <see cref="IConversationStore.CreateAsync"/> / <see cref="IConversationStore.SaveAsync"/>
    /// paths so a freshly persisted conversation can be re-read on the next request even when a
    /// negative cache entry was previously written by a racing resolver call.
    /// </summary>
    /// <param name="conversationId">The conversation whose cache entry should be evicted.</param>
    void Invalidate(ConversationId conversationId);
}
