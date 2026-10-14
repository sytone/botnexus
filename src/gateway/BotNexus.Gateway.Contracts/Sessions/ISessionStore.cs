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
    /// Persists the session state. Creates or updates as needed. Ordinary history mutation is
    /// append-only: stores should persist only entries added since the last successful save and
    /// must not replace unchanged transcript rows. Explicit destructive mutations made through
    /// <see cref="GatewaySession.ReplaceHistory"/>, compaction, reset/prune, or crash-sentinel
    /// removal require targeted reconciliation of the complete history shape while preserving
    /// every unchanged persisted row identity.
    /// </summary>
    /// <remarks>
    /// Callers changing only transcript, metadata, or lifecycle state should prefer
    /// <see cref="AppendEntriesAsync"/>, <see cref="PatchMetadataAsync"/>, or
    /// <see cref="TransitionStatusAsync"/> respectively. Aggregate save remains the coordinated
    /// path when multiple fields change together or history was explicitly replaced.
    /// </remarks>
    Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebinds sessions owned by an agent whose IDs begin with an exact prefix to a target
    /// conversation, without rewriting sessions already bound to that conversation. This narrow
    /// operation lets migrations avoid loading transcript history for an ID-and-metadata change.
    /// </summary>
    /// <remarks>
    /// The portable default uses aggregate enumeration and save so non-database stores remain
    /// correct. Stores capable of filtering and updating metadata before aggregate materialization
    /// should override it; SQLite does so without reading transcript history.
    /// </remarks>
    /// <param name="agentId">The agent that owns the sessions through their conversations.</param>
    /// <param name="sessionIdPrefix">The exact ordinal session-ID prefix to match.</param>
    /// <param name="targetConversationId">The canonical conversation to bind matching sessions to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of noncanonical session rows rebound.</returns>
    async Task<int> RebindSessionsAsync(
        AgentId agentId,
        string sessionIdPrefix,
        ConversationId targetConversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIdPrefix);

        var sessions = await ListAsync(agentId, cancellationToken).ConfigureAwait(false);
        var rebound = 0;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.SessionId.Value.StartsWith(sessionIdPrefix, StringComparison.Ordinal)
                || (session.ConversationId.IsInitialized() && session.ConversationId == targetConversationId))
            {
                continue;
            }

            session.ConversationId = targetConversationId;
            await SaveAsync(session, cancellationToken).ConfigureAwait(false);
            rebound++;
        }

        return rebound;
    }

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
    /// whenever the caller only needs to add turns: this narrow operation leaves metadata and status
    /// untouched and composes safely with independent mutations.
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
    /// Returns one globally-scoped, bounded, transcript-free page of sessions that still contain crash sentinels.
    /// Callers hydrate one selected session at a time through <see cref="GetAsync"/>.
    /// </summary>
    async Task<UnresolvedCrashSentinelPage> ListUnresolvedCrashSentinelsAsync(
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var sessions = await ListAsync(null, cancellationToken).ConfigureAwait(false);
        var rows = sessions
            .Where(session => session.History.Any(static entry => entry.IsCrashSentinel))
            .OrderBy(session => session.SessionId.Value, StringComparer.Ordinal)
            .Where(session => cursor is null || string.CompareOrdinal(session.SessionId.Value, cursor) > 0)
            .Take(limit + 1)
            .Select(session => new UnresolvedCrashSentinelRow(session.SessionId, session.AgentId))
            .ToList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new UnresolvedCrashSentinelPage(rows, hasMore ? rows[^1].SessionId.Value : null);
    }

    /// <summary>Returns one bounded page of transcript-free rows with byte accounting.</summary>
    Task<SessionCleanupPlanPage> ListCleanupPlanAsync(
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        ListCleanupPlanAsync(limit, includeBytes: true, cursor, cancellationToken);

    /// <summary>
    /// Returns one bounded page of transcript-free rows sufficient for cleanup planning.
    /// Payload-byte accounting is omitted unless <paramref name="includeBytes"/> is requested for disk-budget enforcement.
    /// </summary>
    async Task<SessionCleanupPlanPage> ListCleanupPlanAsync(
        int limit,
        bool includeBytes,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var sessions = await ListAsync(null, cancellationToken).ConfigureAwait(false);
        var rows = sessions
            .OrderBy(session => session.SessionId.Value, StringComparer.Ordinal)
            .Where(session => cursor is null || string.CompareOrdinal(session.SessionId.Value, cursor) > 0)
            .Take(limit + 1)
            .Select(session => new SessionCleanupPlanRow(
                session.SessionId, session.AgentId, session.ConversationId, session.Status, session.UpdatedAt,
                session.MessageCount, includeBytes ? SessionDiskAccounting.Measure(session) : 0))
            .ToList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new SessionCleanupPlanPage(rows, hasMore ? rows[^1].SessionId.Value : null);
    }

    /// <summary>Expires a session only while it still matches the cleanup planning row.</summary>
    async Task<SessionMutationOutcome> ExpireIfMatchesAsync(
        SessionCleanupFence fence,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(fence.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return SessionMutationOutcome.NotFound;
        if (session.ConversationId != fence.ConversationId || session.Status != fence.ExpectedStatus
            || session.UpdatedAt != fence.ExpectedUpdatedAt) return SessionMutationOutcome.Conflict;
        var writeFence = SessionWriteFence.Capture(session);
        session.Status = SessionStatus.Expired;
        session.ExpiresAt ??= expiresAt;
        session.UpdatedAt = expiresAt;
        var outcome = await SaveAsync(session, writeFence, cancellationToken).ConfigureAwait(false);
        return outcome == SessionSaveOutcome.Persisted ? SessionMutationOutcome.Applied : SessionMutationOutcome.Conflict;
    }

    /// <summary>Deletes a session only while it still matches the cleanup planning row.</summary>
    async Task<SessionMutationOutcome> DeleteIfMatchesAsync(
        SessionCleanupFence fence,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(fence.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return SessionMutationOutcome.NotFound;
        if (session.ConversationId != fence.ConversationId || session.Status != fence.ExpectedStatus
            || session.UpdatedAt != fence.ExpectedUpdatedAt) return SessionMutationOutcome.Conflict;
        await DeleteAsync(fence.SessionId, cancellationToken).ConfigureAwait(false);
        return SessionMutationOutcome.Applied;
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
    /// Unsupported stores may no-op only for direct manager callers; tool-origin admission
    /// requires durable retry identity and must fail closed.
    /// </summary>
    /// <param name="info">The sub-agent runtime info to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
        => info.SpawningToolCallId is null ? Task.CompletedTask
            : throw new NotSupportedException("This store cannot durably admit tool-origin sub-agents.");

    /// <summary>
    /// Updates the sub-agent session row when the sub-agent completes, fails, times out, or is killed.
    /// Implementations that do not support sub-agent session tracking may no-op.
    /// </summary>
    /// <param name="info">The retained terminal run metadata to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateSubAgentSessionAsync(
        SubAgentInfo info,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Returns the persisted sub-agent session rows for a given parent session,
    /// ordered by <c>started_at</c> ascending.
    /// Implementations that do not support sub-agent persistence return an empty list.
    /// </summary>
    /// <param name="sessionId">The parent session whose sub-agent history to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SubAgentRunDetail>> ListSubAgentSessionsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SubAgentRunDetail>>(Array.Empty<SubAgentRunDetail>());

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
    /// <param name="parentSessionId">Optional parent session ID filter.</param>
    /// <param name="childAgentId">Optional child agent ID filter.</param>
    /// <param name="offset">Number of matching rows to skip before returning results.</param>
    Task<IReadOnlyList<SubAgentRunDetail>> ListAllSubAgentSessionsAsync(
        string? status = null,
        int limit = 200,
        CancellationToken cancellationToken = default,
        string? parentSessionId = null,
        string? childAgentId = null,
        int offset = 0)
        => Task.FromResult<IReadOnlyList<SubAgentRunDetail>>(Array.Empty<SubAgentRunDetail>());

    /// <summary>Finds the retained admission for an original parent spawn tool call without consuming it.</summary>
    Task<SubAgentRunDetail?> FindSubAgentSpawnAsync(SessionId parentSessionId, string toolCallId, CancellationToken cancellationToken = default)
        => Task.FromResult<SubAgentRunDetail?>(null);

    /// <summary>Reads one retained run by its indexed identity without consuming it.</summary>
    Task<SubAgentRunDetail?> GetSubAgentSessionAsync(string subAgentId, CancellationToken cancellationToken = default)
        => Task.FromResult<SubAgentRunDetail?>(null);

    /// <summary>Commits a terminal result receipt and its original parent ToolResult atomically.
    /// Same-call retries return the original payload; another call receives no result payload.
    /// Unsupported stores fail closed, never acknowledge an unpersisted receipt.</summary>
    Task<string?> ConsumeSubAgentResultAsync(string subAgentId, SessionId parentSessionId,
        ConversationId? parentConversationId, SessionEntry result, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This store cannot atomically retain sub-agent tool results.");

    /// <summary>
    /// Gets aggregate session statistics. Default implementation returns null (not supported).
    /// </summary>
    Task<SessionStats?> GetStatsAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<SessionStats?>(null);
}
