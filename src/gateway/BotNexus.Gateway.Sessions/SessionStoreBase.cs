using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

public abstract class SessionStoreBase : ISessionStore
{
    private readonly IConversationStore? _conversationStoreForExistence;

    /// <summary>
    /// Initialises the base. Concrete subclasses pass through the optional
    /// <see cref="IConversationStore"/> so <see cref="GetExistenceAsync"/> can resolve a
    /// citizen's participation via the conversation-level Participants set instead of
    /// scanning every session's legacy <c>Participants</c> field. When <c>null</c>, the
    /// existence query falls back to an AgentId-owner match only.
    /// </summary>
    protected SessionStoreBase(IConversationStore? conversationStoreForExistence = null)
    {
        _conversationStoreForExistence = conversationStoreForExistence;
    }

    public abstract Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public abstract Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default);

    public abstract Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fenced post-run finalizer save (issue #1518). Default implementation re-reads the session
    /// via <see cref="GetAsync"/> and evaluates the fence through
    /// <see cref="SessionFenceEvaluator.Passes"/> before delegating to the unfenced
    /// <see cref="SaveAsync(GatewaySession, CancellationToken)"/>. This closes the resurrect/clobber
    /// window for File and InMemory stores. <see cref="SqliteSessionStore"/> overrides this to
    /// perform the re-read and the write under a single per-session lock so the check-then-write
    /// is atomic against a concurrent delete/reset.
    /// </summary>
    public virtual async Task<SessionSaveOutcome> SaveAsync(
        GatewaySession session,
        SessionWriteFence fence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var current = await GetAsync(fence.ExpectedSessionId, cancellationToken).ConfigureAwait(false);
        if (!SessionFenceEvaluator.Passes(fence, current))
            return SessionSaveOutcome.Rebound;

        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return SessionSaveOutcome.Persisted;
    }

    public abstract Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public abstract Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    public async Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        return ApplyAgentFilter(sessions, agentId).ToList();
    }

    /// <summary>
    /// Default summary path for stores without a transcript-free read: enumerate sessions
    /// (still materialising history) and project to <see cref="SessionSummary"/>. Suitable
    /// for File/InMemory stores used in development and tests. <see cref="SqliteSessionStore"/>
    /// overrides this with a metadata-only query so production never loads the whole DB.
    /// </summary>
    public virtual async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        DateTimeOffset updatedAfter,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => session.UpdatedAt >= updatedAfter)
            .Select(SessionSummary.FromSession)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> ListAsync(
        AgentId? agentId,
        BotNexus.Gateway.Abstractions.Models.SessionStatus? status,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GatewaySession> result = ApplyAgentFilter(sessions, agentId);
        if (status is not null)
        {
            result = result.Where(session => session.Status == status.Value);
        }

        return result.ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(
        AgentId agentId,
        ChannelKey channelType,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => session.AgentId == agentId)
            .Where(session => session.ChannelType is not null && session.ChannelType == channelType)
            .OrderByDescending(session => session.CreatedAt)
            .ToList();
    }

    public virtual async Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(
        ConversationId conversationId,
        AgentId? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GatewaySession> result = sessions
            .Where(session => session.ConversationId.IsInitialized()
                && session.ConversationId == conversationId);
        if (agentId is not null)
        {
            result = result.Where(session => session.AgentId == agentId);
        }
        return result
            .OrderBy(session => session.CreatedAt)
            .ThenBy(session => session.SessionId.Value, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(
        AgentId agentId,
        ExistenceQuery query,
        CancellationToken cancellationToken = default)
    {
        query ??= new ExistenceQuery();

        // Conversation-first lookup (P9-F, #657): a citizen "exists" in a session if they
        // own it (Session.AgentId match) OR if they participate in its Conversation
        // (Conversation.Participants set). Pre-P9-F this used a per-session
        // Session.Participants scan; the conversation-level set is the durable source of
        // truth and is indexed in SqliteConversationStore. When no conversation store is
        // wired (older test fixtures), participation falls back to owner-only.
        HashSet<ConversationId>? participantConversationIds = null;
        if (_conversationStoreForExistence is not null)
        {
            var citizen = CitizenId.Of(agentId);
            var conversations = await _conversationStoreForExistence
                .ListForCitizenAsync(citizen, cancellationToken)
                .ConfigureAwait(false);
            participantConversationIds = new HashSet<ConversationId>(
                conversations
                    .Select(c => c.ConversationId)
                    .Where(id => id.IsInitialized()));
        }

        var sessions = await EnumerateSessionsAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GatewaySession> result = sessions
            .Where(session =>
                session.AgentId == agentId
                || (participantConversationIds is not null
                    && session.ConversationId.IsInitialized()
                    && participantConversationIds.Contains(session.ConversationId)))
            .Where(session => !query.From.HasValue || session.CreatedAt >= query.From.Value)
            .Where(session => !query.To.HasValue || session.CreatedAt <= query.To.Value)
            .Where(session => query.TypeFilter is null || session.SessionType == query.TypeFilter)
            .OrderByDescending(session => session.CreatedAt);

        if (query.Limit is > 0)
            result = result.Take(query.Limit.Value);

        return result.ToList();
    }

    protected static GatewaySession CreateSession(SessionId sessionId, AgentId agentId, ChannelKey? channelType, ISecretRedactor? redactor = null)
        => new(
            new Session
            {
                SessionId = sessionId,
                ChannelType = channelType,
                SessionType = InferSessionType(sessionId, channelType)
            },
            redactor)
        {
            AgentId = agentId
        };

    internal static SessionType InferSessionType(SessionId sessionId, ChannelKey? channelType)
    {
        // P9-E (#645): the legacy Soul/Cron/Heartbeat SessionType discriminators are
        // collapsed. Triggers stamp the proxy origin onto SessionEntry.Trigger; here
        // we classify the conversation shape only. Soul sessions become AgentSelf;
        // cron sessions stay UserAgent (the cron channel keeps the interactive-gate
        // exclusion via Session.IsInteractive — see Session.cs).
        if (sessionId.IsSubAgent)
            return SessionType.AgentSubAgent;

        if (sessionId.IsAgentConversation)
            return SessionType.AgentAgent;

        return SessionType.UserAgent;
    }

    protected abstract Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public virtual Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task UpdateSubAgentSessionAsync(
        string subAgentId,
        DateTimeOffset endedAt,
        string status,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Re-abstracts the <see cref="ISessionStore.ListAllSubAgentSessionsAsync"/> default interface
    /// method into the class hierarchy so that callers holding the store as <see cref="ISessionStore"/>
    /// (e.g. the observability controller) dispatch to a derived override rather than the empty DIM
    /// default. Stores without sub-agent persistence keep the empty-list behaviour.
    /// </remarks>
    public virtual Task<IReadOnlyList<SubAgentSessionSummary>> ListAllSubAgentSessionsAsync(
        string? status = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SubAgentSessionSummary>>(Array.Empty<SubAgentSessionSummary>());

    private static IEnumerable<GatewaySession> ApplyAgentFilter(IEnumerable<GatewaySession> sessions, AgentId? agentId)
        => agentId is null ? sessions : sessions.Where(session => session.AgentId == agentId);
}
