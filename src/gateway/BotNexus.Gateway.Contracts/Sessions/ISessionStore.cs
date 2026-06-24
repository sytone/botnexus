using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Persistence interface for gateway sessions. Implementations control where
/// and how session data (conversation history, metadata) is stored.
/// </summary>
/// <remarks>
/// <para>Built-in implementations:</para>
/// <list type="bullet">
///   <item><b>InMemorySessionStore</b> — Non-durable, in-process. For development and testing.</item>
///   <item><b>FileSessionStore</b> — File-backed with JSONL history + JSON metadata sidecar. For single-instance deployments.</item>
///   <item><b>SqliteSessionStore</b> — SQLite database-backed, production-ready. Features indexed queries,
///   WAL mode for concurrency, and per-session locking.</item>
/// </list>
/// <para>
/// Future implementations could use Redis, PostgreSQL, or other backends.
/// All implementations must be thread-safe.
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Gets a session by ID, or <c>null</c> if it doesn't exist.
    /// </summary>
    Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an existing session or creates a new one bound to the specified agent.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="agentId">The agent to bind to if creating a new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the session state. Creates or updates as needed.
    /// </summary>
    Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a session and its history.
    /// </summary>
    Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the session, preserving its data but removing it from active use.
    /// </summary>
    Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions, optionally filtered by agent ID.
    /// </summary>
    /// <param name="agentId">If set, only returns sessions for this agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns lightweight <see cref="SessionSummary"/> records for sessions whose
    /// <c>UpdatedAt</c> is at or after <paramref name="updatedAfter"/>, <b>without loading
    /// conversation transcripts</b>.
    /// </summary>
    /// <remarks>
    /// This is the read path the WebUI session list and <c>SessionWarmupService</c> use.
    /// Loading full session history just to render a metadata list does not scale — on a
    /// large database it dominates the request and can exceed the SignalR hub cancellation
    /// window. Transcript content is only needed when a user actually opens a conversation.
    /// <para>
    /// The default implementation maps from <see cref="ListAsync"/>, which still materialises
    /// history; it exists so non-SQLite stores (File, InMemory, test doubles) keep working.
    /// The SQLite store overrides this with a metadata-only query that derives
    /// <c>MessageCount</c> from a <c>COUNT(*)</c> aggregate rather than reading entries.
    /// </para>
    /// </remarks>
    /// <param name="updatedAfter">Lower bound (inclusive) on session <c>UpdatedAt</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        DateTimeOffset updatedAfter,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ListAsync(null, cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => session.UpdatedAt >= updatedAfter)
            .Select(SessionSummary.FromSession)
            .ToList();
    }

    /// <summary>
    /// Lists sessions for a specific agent filtered by channel type,
    /// ordered by created time descending (newest first).
    /// </summary>
    Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(
        AgentId agentId,
        ChannelKey channelType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions belonging to a specific conversation, in chronological
    /// (ascending CreatedAt) order. Includes both Active and Sealed sessions
    /// — conversation history requires the full timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the canonical "give me the sessions for conversation X" API.
    /// Replaces the previous load-all-then-filter pattern
    /// (<c>ListAsync(...).Where(s =&gt; s.ConversationId == ...)</c>) which was
    /// pinned by issue F-7.
    /// </para>
    /// <para>
    /// Behavioural contract (must be honoured by every implementation):
    /// </para>
    /// <list type="bullet">
    ///   <item>Returns an empty list (never <c>null</c>) when no sessions match.</item>
    ///   <item>Excludes sessions whose <c>ConversationId</c> is <c>null</c>.</item>
    ///   <item>Ordered by <c>CreatedAt</c> ascending; ties broken by
    ///   <c>SessionId</c> ascending so the order is fully deterministic.</item>
    ///   <item>Includes sessions with <c>Status == Sealed</c> and
    ///   <c>Status == Active</c> alike; conversation history needs the full sequence.</item>
    /// </list>
    /// </remarks>
    /// <param name="conversationId">The conversation to query.</param>
    /// <param name="agentId">
    /// Optional agent filter. When set, only sessions owned by this agent are returned.
    /// Useful for access-control-shaped callers and cron normalisation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(
        ConversationId conversationId,
        AgentId? agentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions where the agent either owns the session or is listed as a participant.
    /// </summary>
    Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(
        AgentId agentId,
        ExistenceQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new sub-agent session row when a sub-agent is spawned.
    /// Implementations that do not support sub-agent session tracking may no-op.
    /// </summary>
    /// <param name="info">The sub-agent runtime info to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Updates the sub-agent session row when the sub-agent completes, fails, times out, or is killed.
    /// Implementations that do not support sub-agent session tracking may no-op.
    /// </summary>
    /// <param name="subAgentId">The sub-agent ID whose row to update.</param>
    /// <param name="endedAt">When the sub-agent ended.</param>
    /// <param name="status">The final status string (Completed, Failed, TimedOut, Killed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateSubAgentSessionAsync(
        string subAgentId,
        DateTimeOffset endedAt,
        string status,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Returns the persisted sub-agent session rows for a given parent session,
    /// ordered by <c>started_at</c> ascending.
    /// Implementations that do not support sub-agent persistence return an empty list.
    /// </summary>
    /// <param name="sessionId">The parent session whose sub-agent history to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SubAgentSessionSummary>> ListSubAgentSessionsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SubAgentSessionSummary>>(Array.Empty<SubAgentSessionSummary>());

    /// <summary>
    /// Gets aggregate session statistics. Default implementation returns null (not supported).
    /// </summary>
    Task<SessionStats?> GetStatsAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<SessionStats?>(null);
}
