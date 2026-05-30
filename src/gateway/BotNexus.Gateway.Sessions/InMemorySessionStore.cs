using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// In-memory session store for development and testing.
/// Not durable — all sessions are lost on restart.
/// </summary>
public sealed class InMemorySessionStore : SessionStoreBase
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    private readonly Dictionary<SessionId, GatewaySession> _sessions = [];
    private readonly Lock _sync = new();
    private readonly ISecretRedactor? _redactor;
    private readonly LegacyConversationResolver? _legacyResolver;
    private readonly ILogger<InMemorySessionStore>? _logger;

    /// <summary>Creates an in-memory session store without secret redaction.</summary>
    public InMemorySessionStore() : this(null) { }

    /// <summary>Creates an in-memory session store that redacts secrets at write time.</summary>
    public InMemorySessionStore(ISecretRedactor? redactor)
        : this(redactor, conversationStore: null, logger: null)
    {
    }

    /// <summary>
    /// Creates an in-memory session store with optional secret redaction and a
    /// conversation store for legacy-conversation backfill at save time
    /// (Phase 9 / P9-B; issue #615). Sessions saved with no
    /// <see cref="Session.ConversationId"/> are stamped with the agent's
    /// <c>legacy:{agentId}</c> conversation. There is no load-time backfill because
    /// in-memory rows are never persisted from a pre-Phase-9 schema.
    /// </summary>
    public InMemorySessionStore(
        ISecretRedactor? redactor,
        IConversationStore? conversationStore,
        ILogger<InMemorySessionStore>? logger = null)
        : base(conversationStore)
    {
        _redactor = redactor;
        _legacyResolver = conversationStore is not null
            ? new LegacyConversationResolver(conversationStore, logger: null)
            : null;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
    }

    /// <inheritdoc />
    public override Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
                return Task.FromResult(existing);

            var session = CreateSession(sessionId, agentId, null, _redactor);
            _sessions[sessionId] = session;
            return Task.FromResult(session);
        }
    }

    /// <inheritdoc />
    public override async Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);

        // Backfill outside the lock — the resolver may await a conversation store call.
        // _sync is non-async so we cannot hold it across the await anyway.
        if (!session.ConversationId.IsInitialized() && _legacyResolver is not null)
        {
            var legacy = await _legacyResolver.ResolveAsync(session.AgentId, cancellationToken).ConfigureAwait(false);
            session.ConversationId = legacy.ConversationId;

            // If this is the current active session, also bind it as the conversation's
            // active session pointer so the canonical reset path (DefaultConversationResetService)
            // can find it. Sealed/Suspended/Expired sessions are not bound.
            if (session.Status == SessionStatus.Active)
            {
                await _legacyResolver.BindActiveSessionIfNoneAsync(legacy, session.SessionId, cancellationToken).ConfigureAwait(false);
            }

            _logger?.LogWarning(
                "Session {SessionId} for agent {AgentId} was saved with no ConversationId; stamped legacy conversation {LegacyConversationId}.",
                session.SessionId, session.AgentId, legacy.ConversationId);
        }

        lock (_sync) _sessions[session.SessionId] = session;
    }

    /// <inheritdoc />
    public override Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        lock (_sync) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<GatewaySession>>([.. _sessions.Values]);
        }
    }
}
