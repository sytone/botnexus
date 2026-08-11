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
    /// Persists the session state <b>only if</b> the on-disk row still matches the run identity
    /// captured in <paramref name="fence"/>. When the row was deleted, sealed by a competing
    /// reset, or rebound to a different conversation while the run was in flight, the write is
    /// skipped and <see cref="SessionSaveOutcome.Rebound"/> is returned instead of resurrecting
    /// or clobbering the row. See issue #1518 and <see cref="SessionWriteFence"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the write path the gateway's post-run finalizer uses (turn-transcript
    /// persistence, compaction record, metadata patch). The plain
    /// <see cref="SaveAsync(GatewaySession, CancellationToken)"/> overload keeps its
    /// unconditional create-or-update semantics for pre-run write-ahead saves (the user
    /// message and crash sentinel) that must be able to create the row.
    /// </para>
    /// <para>
    /// The default implementation re-reads the session via <see cref="GetAsync"/> and applies
    /// the fence before delegating to the unfenced <see cref="SaveAsync(GatewaySession, CancellationToken)"/>.
    /// Stores that can perform the re-read and the write under a single lock (e.g. the SQLite
    /// store) override this to close the check-then-write window atomically.
    /// </para>
    /// </remarks>
    /// <param name="session">The session to persist.</param>
    /// <param name="fence">The run identity captured at run start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="SessionSaveOutcome.Persisted"/> when the fence passed and the session was
    /// written; <see cref="SessionSaveOutcome.Rebound"/> when the write was skipped.
    /// </returns>
    async Task<SessionSaveOutcome> SaveAsync(
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

    /// <summary>
    /// Appends transcript entries to an existing session <b>without</b> rewriting the rest of the
    /// aggregate (issue #2132). Use this instead of read-mutate-<see cref="SaveAsync(GatewaySession, CancellationToken)"/>
    /// whenever the caller only needs to add turns: the whole-aggregate save replaces the complete
    /// history plus the persisted metadata and status from the caller's snapshot, so a metadata
    /// patch or lifecycle transition that landed in the read-write gap is silently lost.
    /// </summary>
    /// <remarks>
    /// Conflict contract: appends are refused (<see cref="SessionMutationOutcome.Conflict"/>) when
    /// the authoritative row is <see cref="SessionStatus.Sealed"/> or <see cref="SessionStatus.Expired"/>,
    /// because those are terminal states a competing reset established deliberately and an append
    /// would otherwise revive the transcript. Appends against Active or Suspended sessions always
    /// apply and never conflict with a concurrent metadata patch - transcript and metadata are
    /// disjoint state. The row is never created: a missing session yields
    /// <see cref="SessionMutationOutcome.NotFound"/>.
    /// </remarks>
    /// <param name="sessionId">The session to append to.</param>
    /// <param name="entries">The entries to append, in order. An empty sequence is a no-op that still reports the row's existence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionAppendMutationResult> AppendEntriesAsync(
        SessionId sessionId,
        IReadOnlyList<SessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return AppendEntriesDefaultAsync(this, sessionId, entries, cancellationToken);

        // Default: re-read the authoritative session and append onto THAT instance, so a stale
        // caller snapshot can never replace the complete history. Stores that can append rows
        // without rewriting the aggregate (the SQLite store) override this.
        static async Task<SessionAppendMutationResult> AppendEntriesDefaultAsync(
            ISessionStore store,
            SessionId sessionId,
            IReadOnlyList<SessionEntry> entries,
            CancellationToken cancellationToken)
        {
            var session = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return new SessionAppendMutationResult(SessionMutationOutcome.NotFound, 0);

            if (SessionMutationPolicy.IsTerminal(session.Status))
                return new SessionAppendMutationResult(SessionMutationOutcome.Conflict, 0);

            if (entries.Count == 0)
                return new SessionAppendMutationResult(SessionMutationOutcome.Applied, 0);

            session.AddEntries(entries);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return new SessionAppendMutationResult(SessionMutationOutcome.Applied, entries.Count);
        }
    }

    /// <summary>
    /// Merges a metadata patch into an existing session <b>without</b> touching its transcript or
    /// lifecycle status (issue #2132). Keys mapped to <c>null</c> are removed; all other keys are
    /// added or overwritten. This is the write path the sessions API metadata endpoint uses so a
    /// concurrent turn append is never rolled back by a stale aggregate save.
    /// </summary>
    /// <remarks>
    /// The read of the current metadata and the write of the merged result happen under the store's
    /// per-session lock, so two concurrent patches compose rather than clobber. Metadata edits never
    /// conflict with transcript appends; they only report
    /// <see cref="SessionMutationOutcome.NotFound"/> when the row is gone.
    /// </remarks>
    /// <param name="sessionId">The session whose metadata to patch.</param>
    /// <param name="patch">Keys to add/update, or map a key to <c>null</c> to remove it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionMetadataMutationResult> PatchMetadataAsync(
        SessionId sessionId,
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return PatchMetadataDefaultAsync(this, sessionId, patch, cancellationToken);

        // Default: merge onto the authoritative re-read, not the caller's snapshot.
        static async Task<SessionMetadataMutationResult> PatchMetadataDefaultAsync(
            ISessionStore store,
            SessionId sessionId,
            IReadOnlyDictionary<string, object?> patch,
            CancellationToken cancellationToken)
        {
            var session = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return SessionMetadataMutationResult.NotFound;

            SessionMutationPolicy.ApplyMetadataPatch(session.Metadata, patch);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return new SessionMetadataMutationResult(
                SessionMutationOutcome.Applied,
                new Dictionary<string, object?>(session.Metadata));
        }
    }

    /// <summary>
    /// Atomically compare-and-sets the session's lifecycle status (issue #2132). The transition is
    /// applied only when the authoritative persisted status is one of
    /// <paramref name="expectedStatuses"/>, so a suspend/resume/seal computed from a snapshot that
    /// another actor has already moved on from is refused instead of silently reverting them.
    /// </summary>
    /// <remarks>
    /// The status column is written on its own: the transcript and metadata of the authoritative row
    /// are left exactly as they stand, so a lifecycle change and a concurrent transcript append both
    /// survive. On refusal the result carries the authoritative status the caller lost to, which is
    /// what an HTTP caller needs for a meaningful 409.
    /// </remarks>
    /// <param name="sessionId">The session to transition.</param>
    /// <param name="expectedStatuses">The statuses from which this transition is legal. Must not be empty.</param>
    /// <param name="newStatus">The status to move to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionStatusMutationResult> TransitionStatusAsync(
        SessionId sessionId,
        IReadOnlyList<SessionStatus> expectedStatuses,
        SessionStatus newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        return TransitionStatusDefaultAsync(this, sessionId, expectedStatuses, newStatus, cancellationToken);

        // Default: evaluate the compare-and-set against the authoritative re-read so a transition
        // another actor already performed is reported as a conflict rather than reverted.
        static async Task<SessionStatusMutationResult> TransitionStatusDefaultAsync(
            ISessionStore store,
            SessionId sessionId,
            IReadOnlyList<SessionStatus> expectedStatuses,
            SessionStatus newStatus,
            CancellationToken cancellationToken)
        {
            var session = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return new SessionStatusMutationResult(SessionMutationOutcome.NotFound, SessionStatus.Active, default);

            if (!SessionMutationPolicy.CanTransition(expectedStatuses, session.Status))
                return new SessionStatusMutationResult(SessionMutationOutcome.Conflict, session.Status, session.UpdatedAt);

            session.Status = newStatus;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return new SessionStatusMutationResult(SessionMutationOutcome.Applied, newStatus, session.UpdatedAt);
        }
    }

    /// <summary>
    /// Deletes a session and its history.
    /// </summary>
    Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the session, preserving its data but removing it from active use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Drain guarantee (issue #2903).</b> Implementations MUST stop and drain any agent run
    /// bound to <paramref name="sessionId"/> <em>before</em> they commit the archive, and MUST
    /// scope that fence to the exact session - archiving session A must never disturb a run on an
    /// unrelated session, even one owned by the same agent.
    /// </para>
    /// <para>
    /// If the run cannot be drained within the implementation's bounded timeout, the archive MUST
    /// fail with <see cref="SessionArchiveDrainTimeoutException"/> and leave the session untouched
    /// rather than seal over live work. Callers can therefore rely on exactly two outcomes: the
    /// in-flight turn completed and was persisted before the seal, or nothing was archived at all.
    /// A sealed session never subsequently gains turns, and no turn is silently lost.
    /// </para>
    /// <para>
    /// This guarantee is about run lifecycle only. It does not change what "archived" means for a
    /// given store - the SQLite store seals the row in place, the file store moves the files
    /// aside, the in-memory store drops the row.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">The session to archive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SessionArchiveDrainTimeoutException">
    /// A run bound to the session did not drain inside the timeout; nothing was archived.
    /// </exception>
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
    /// <c>MessageCount</c> from a <c>COUNT(*)</c> aggregate rather than reading entries and
    /// applies the window as a real <c>LIMIT</c>/<c>OFFSET</c>.
    /// </para>
    /// <para>
    /// Issue #2411: the returned page is bounded by <paramref name="limit"/>. Passing
    /// <c>null</c> is the <b>explicit</b> unbounded opt-in and is reserved for background
    /// callers that genuinely need the whole set (session warmup, cron signal folds).
    /// Request-scoped callers must always pass a bound - an unbounded collection read grows
    /// monotonically with session count on a long-lived gateway.
    /// </para>
    /// </remarks>
    /// <param name="updatedAfter">Lower bound (inclusive) on session <c>UpdatedAt</c>.</param>
    /// <param name="limit">Maximum number of summaries to return, or <c>null</c> to opt in to an unbounded read.</param>
    /// <param name="offset">Number of matching summaries to skip, newest first. Negative values are treated as zero.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        DateTimeOffset updatedAfter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ListAsync(null, cancellationToken).ConfigureAwait(false);
        return SessionSummaryWindow.Apply(
            sessions
                .Where(session => session.UpdatedAt >= updatedAfter)
                .Select(SessionSummary.FromSession),
            limit,
            offset);
    }

    /// <summary>
    /// Returns one page of <see cref="SessionSummary"/> records matching <paramref name="query"/>,
    /// together with the total size of the matching set (#2532).
    /// </summary>
    /// <remarks>
    /// This is the read path <c>GET /api/sessions</c> uses. It exists because
    /// <see cref="ListSummariesAsync"/> can only page the <b>store</b>: callers that then filtered
    /// the page by agent or status in memory were paging one set and consuming another, so a
    /// client walking offsets advanced through the global session table one matching row at a time
    /// (issue #2532). Here the agent/status predicate is part of the query, so
    /// <see cref="SessionSummaryQuery.Offset"/> always addresses the FILTERED set.
    /// <para>
    /// The default implementation filters in memory over <see cref="ListAsync"/> so non-SQLite
    /// stores (File, InMemory, test doubles) keep working. The SQLite store overrides it to push
    /// the status predicate and the window into SQL.
    /// </para>
    /// </remarks>
    /// <param name="query">The filter and window to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task<SessionSummaryPage> ListSummaryPageAsync(
        SessionSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sessions = await ListAsync(null, cancellationToken).ConfigureAwait(false);
        return SessionSummaryWindow.ApplyQuery(sessions.Select(SessionSummary.FromSession), query);
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
    /// Returns persisted sub-agent session rows across <em>all</em> parent sessions, ordered by
    /// <c>started_at</c> descending (newest first) for a platform-wide observability feed (#1941).
    /// This is the parent-agnostic counterpart to <see cref="ListSubAgentSessionsAsync"/>; it reads
    /// the existing <c>sub_agent_sessions</c> store read-only and adds no new persistence.
    /// Implementations that do not support sub-agent persistence return an empty list.
    /// </summary>
    /// <param name="status">Optional case-insensitive status filter (e.g. Completed, Failed, Killed, TimedOut, Active). When null or whitespace, all statuses are returned.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SubAgentSessionSummary>> ListAllSubAgentSessionsAsync(
        string? status = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SubAgentSessionSummary>>(Array.Empty<SubAgentSessionSummary>());

    /// <summary>
    /// Gets aggregate session statistics. Default implementation returns null (not supported).
    /// </summary>
    Task<SessionStats?> GetStatsAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<SessionStats?>(null);
}
