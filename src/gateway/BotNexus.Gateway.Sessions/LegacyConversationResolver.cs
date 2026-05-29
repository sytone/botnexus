using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Idempotently resolves the per-agent legacy conversation used to backfill
/// sessions that were persisted before <see cref="Session.ConversationId"/> was
/// guaranteed non-null (Phase 9 / P9-B; issue #615).
/// </summary>
/// <remarks>
/// <para>
/// The legacy conversation is a regular <see cref="Conversation"/> identified by
/// <c>Title = "legacy:{agentId}"</c> and <c>Status = Active</c>. It is created
/// lazily on first lookup for a given agent. Subsequent lookups return the
/// existing conversation so all orphan sessions for the same agent collapse into
/// the same conversation thread.
/// </para>
/// <para>
/// <b>Cross-cutting invariant:</b> there must be at most one active legacy
/// conversation per (world, agent). Within a single process this is enforced via
/// a per-agent <see cref="SemaphoreSlim"/>. Across processes the conversation
/// store's idempotency comes from the title-based <c>FirstOrDefault</c> lookup —
/// a race that ends up creating two active legacy conversations is degraded-but-
/// recoverable behaviour (next read picks whichever comes first; orphan sessions
/// may end up split across the duplicates but both are still valid conversations
/// owned by the same agent).
/// </para>
/// </remarks>
public sealed class LegacyConversationResolver
{
    private readonly IConversationStore _conversationStore;
    private readonly ILogger<LegacyConversationResolver>? _logger;
    private readonly ConcurrentDictionary<AgentId, SemaphoreSlim> _agentLocks = new();

    public LegacyConversationResolver(
        IConversationStore conversationStore,
        ILogger<LegacyConversationResolver>? logger = null)
    {
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _logger = logger;
    }

    /// <summary>
    /// Returns the agent's <c>legacy:{agentId}</c> conversation, creating it if
    /// it does not yet exist. The created conversation has
    /// <see cref="Conversation.Initiator"/> set to <see cref="CitizenId.Of(AgentId)"/>
    /// (the agent itself is the initiator for legacy ungrouped sessions) and
    /// <see cref="Conversation.IsDefault"/> set to <c>false</c> so it does not
    /// shadow the agent's real default conversation.
    /// </summary>
    public async Task<Conversation> ResolveAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        var legacyTitle = LegacyTitleFor(agentId);

        // Fast path: no lock needed if the conversation already exists.
        var existing = await FindExistingAsync(agentId, legacyTitle, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        // Slow path: serialise creation per-agent within this process so two
        // concurrent orphan loads don't both call CreateAsync.
        var agentLock = _agentLocks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await agentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock — another caller may have created it.
            existing = await FindExistingAsync(agentId, legacyTitle, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing;

            var conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = agentId,
                Title = legacyTitle,
                IsDefault = false,
                Initiator = CitizenId.Of(agentId),
                Kind = ConversationKind.HumanAgent
            };
            var created = await _conversationStore.CreateAsync(conversation, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "Created legacy conversation {ConversationId} for agent {AgentId} to backfill orphan sessions.",
                created.ConversationId,
                agentId);
            return created;
        }
        finally
        {
            agentLock.Release();
        }
    }

    /// <summary>
    /// The canonical title used for the per-agent legacy conversation. Stable
    /// across all stores so the existing SQLite startup migration, on-demand
    /// load-path backfill, and save-time defensive stamping all converge on the
    /// same row.
    /// </summary>
    public static string LegacyTitleFor(AgentId agentId) => $"legacy:{agentId.Value}";

    /// <summary>
    /// Best-effort: bind <paramref name="conversation"/>.<see cref="Conversation.ActiveSessionId"/>
    /// to <paramref name="sessionId"/> if the conversation currently has no active session pointer.
    /// </summary>
    /// <remarks>
    /// <para>Called by session stores after stamping an orphan session with the legacy
    /// conversation so the canonical reset / dispatch paths
    /// (<c>DefaultConversationResetService.ResetActiveSessionAsync</c>,
    /// <c>DefaultConversationRouter</c>) can find the session via the conversation pointer.</para>
    /// <para>If <see cref="Conversation.ActiveSessionId"/> is already set, this is a no-op —
    /// the existing pointer is preserved to avoid clobbering a concurrent caller who may
    /// have just bound their own active session. <b>Eventual-consistency invariant:</b>
    /// last-stamp-wins races are acceptable because the conversation router self-heals
    /// stale active-session pointers (<c>DefaultConversationResetService.cs</c> already
    /// clears <c>ActiveSessionId</c> defensively when the pointed-at session is missing).</para>
    /// <para>The caller is responsible for verifying the session is in a state worth
    /// binding (typically <see cref="SessionStatus.Active"/>) — this method does not
    /// inspect the session itself.</para>
    /// </remarks>
    public async Task BindActiveSessionIfNoneAsync(
        Conversation conversation,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.ActiveSessionId is not null)
            return;

        conversation.ActiveSessionId = sessionId;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversationStore.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation(
            "Bound legacy conversation {ConversationId} ActiveSessionId to backfilled session {SessionId}.",
            conversation.ConversationId,
            sessionId);
    }

    private async Task<Conversation?> FindExistingAsync(AgentId agentId, string legacyTitle, CancellationToken cancellationToken)
    {
        var conversations = await _conversationStore.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
        return conversations.FirstOrDefault(c =>
            c.Title == legacyTitle &&
            c.Status == ConversationStatus.Active);
    }
}
