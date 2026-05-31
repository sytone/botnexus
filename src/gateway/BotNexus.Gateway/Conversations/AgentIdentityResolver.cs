using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Default <see cref="IAgentIdentityResolver"/> backed by <see cref="IConversationStore"/>
/// with a process-local cache keyed by <see cref="ConversationId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The cache is safe because, per P9-H (issue #662), agent ownership is treated as
/// write-once on the conversation: <c>Conversation.AgentId</c> is set on
/// <see cref="IConversationStore.CreateAsync"/> and is not expected to change for the
/// lifetime of the conversation. The
/// <see cref="ConversationAgentIdImmutabilityArchitectureTests"/> fence pins this
/// invariant at the domain layer and the <see cref="IConversationStore"/> implementations
/// reject saves that attempt to mutate it.
/// </para>
/// <para>
/// <see cref="Invalidate"/> is called by the conversation store on
/// <see cref="IConversationStore.CreateAsync"/> / <see cref="IConversationStore.SaveAsync"/>
/// so a freshly written row evicts any stale negative cache entry.
/// </para>
/// </remarks>
public sealed class AgentIdentityResolver : IAgentIdentityResolver
{
    private readonly IConversationStore _conversationStore;
    private readonly ILogger<AgentIdentityResolver> _logger;
    private readonly ConcurrentDictionary<ConversationId, AgentId> _cache = new();

    public AgentIdentityResolver(
        IConversationStore conversationStore,
        ILogger<AgentIdentityResolver> logger)
    {
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AgentId?> GetAgentIdAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(conversationId, out var cached))
            return cached;

        var conversation = await _conversationStore.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
            return null;

        // Cache positive results only; negative misses re-query the store next time so a
        // create-then-resolve race never observes a sticky null. Conversation.AgentId is
        // write-once (see ConversationAgentIdImmutabilityArchitectureTests), so the cached
        // positive value is safe for the lifetime of the conversation.
        _cache[conversationId] = conversation.AgentId;
        return conversation.AgentId;
    }

    /// <inheritdoc />
    public async Task<AgentId> GetRequiredAgentIdAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        var resolved = await GetAgentIdAsync(conversationId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogWarning(
                "Agent identity resolver could not resolve conversation {ConversationId} — caller required a non-null AgentId.",
                conversationId);
            throw new InvalidOperationException(
                $"Cannot resolve owning AgentId — conversation '{conversationId}' does not exist.");
        }
        return resolved.Value;
    }

    /// <inheritdoc />
    public void Invalidate(ConversationId conversationId) =>
        _cache.TryRemove(conversationId, out _);
}
