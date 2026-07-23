using System.Text.Json;
using System.Diagnostics;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using MessageRole = BotNexus.Domain.Primitives.MessageRole;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using TriggerType = BotNexus.Domain.Primitives.TriggerType;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Persistence.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// SQLite-backed session store for single-node persistent gateway sessions.
/// Uses WAL journal mode and per-session <see cref="SemaphoreSlim"/> locks so
/// concurrent agents can read and write independent sessions without blocking each other.
/// The global initialisation lock (<c>_initLock</c>) is only held during the one-time
/// schema creation; all subsequent operations use per-session granularity.
/// </summary>
public sealed class SqliteSessionStore : SessionStoreBase
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");

    /// <summary>Default bound for the in-memory session cache (entries).</summary>
    public const int DefaultSessionCacheCapacity = 500;

    private readonly string _connectionString;
    private readonly SqliteWalMaintenance _walMaintenance = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // Striped write locks: a fixed pool hashed by session id. This bounds the number
    // of sync primitives (no per-session SemaphoreSlim leak across the process
    // lifetime) and never strands a lock on an exception, since the stripe is the
    // pool's, not a per-key entry that must be removed.
    private readonly StripedAsyncLock _sessionLocks = new();
    // Bounded read-through cache: holds the hot set of materialized sessions (each
    // carries its full history) capped by LRU so a long-running gateway does not
    // retain every session ever touched. Cold reads fall through to SQLite.
    private readonly BoundedLruCache<SessionId, GatewaySession> _cache;
    private bool _initialized;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationStore _conversationStore;
    private readonly LegacyConversationResolver _legacyResolver;
    // Bounded conversation -> agent id cache (shared by hydration + stats). Same LRU
    // bound as the session cache so it cannot grow unbounded across distinct
    // conversations over the process lifetime.
    private readonly BoundedLruCache<ConversationId, AgentId> _agentIdCache;
    private readonly ILogger<SqliteSessionStore> _logger;
    private readonly ISecretRedactor? _redactor;

    /// <summary>
    /// Initialises a new <see cref="SqliteSessionStore"/>.
    /// </summary>
    /// <param name="connectionString">SQLite connection string for the sessions database.</param>
    /// <param name="logger">Logger for diagnostic output including migration summaries.</param>
    /// <param name="conversationStore">
    /// Mandatory post-P9-I (issue #674): durable agent ownership lives on
    /// <see cref="Conversation.AgentId"/>, and the SQLite session row no longer carries an
    /// <c>agent_id</c> column. The store uses the conversation store to (a) link orphaned
    /// sessions to their agent's legacy conversation at startup, (b) hydrate
    /// <see cref="GatewaySession.AgentId"/> on every load from <see cref="Conversation.AgentId"/>
    /// (cached per <see cref="ConversationId"/>; safe because <c>Conversation.AgentId</c> is
    /// init-only per <c>ConversationAgentIdImmutabilityArchitectureTests</c>), and (c) stamp
    /// <see cref="Session.ConversationId"/> defensively at save time.
    /// </param>
    /// <param name="redactor">When provided, secrets in content are redacted before storage.</param>
    /// <param name="cacheCapacity">
    /// Maximum number of sessions (and conversation->agent mappings) retained in the
    /// in-memory caches. Older entries are evicted by LRU; cold reads fall through to
    /// SQLite. Defaults to <see cref="DefaultSessionCacheCapacity"/>.
    /// </param>
    public SqliteSessionStore(
        string connectionString,
        ILogger<SqliteSessionStore> logger,
        IConversationStore conversationStore,
        ISecretRedactor? redactor = null,
        int cacheCapacity = DefaultSessionCacheCapacity)
        : base(conversationStore)
    {
        _connectionString = connectionString;
        _logger = logger;
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _legacyResolver = new LegacyConversationResolver(conversationStore, logger: null);
        _redactor = redactor;
        _cache = new BoundedLruCache<SessionId, GatewaySession>(cacheCapacity);
        _agentIdCache = new BoundedLruCache<ConversationId, AgentId>(cacheCapacity);
    }

    /// <inheritdoc />
    public override async Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (_cache.TryGet(sessionId, out var cached))
            return cached;

        var loaded = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (loaded is not null)
            _cache.Set(sessionId, loaded);

        return loaded;
    }

    /// <inheritdoc />
    public override async Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.get_or_create", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.agent.id", agentId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (_cache.TryGet(sessionId, out var cached))
            return cached;

        var loaded = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (loaded is not null)
        {
            _cache.Set(sessionId, loaded);
            return loaded;
        }

        var session = CreateSession(sessionId, agentId, null, _redactor);
        _cache.Set(sessionId, session);
        return session;
    }

    /// <inheritdoc />
    public override async Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // Backfill before the per-session lock so the resolver's per-agent lock
        // does not interleave with concurrent saves of the same session.
        await EnsureConversationIdStampedAsync(session, cancellationToken).ConfigureAwait(false);

        var sessionLock = await AcquireSessionLockAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.Set(session.SessionId, session);
            await RetryOnTransientAsync(async () =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await UpsertSessionAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await ReplaceHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { sessionLock.Dispose(); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Issue #1518: fenced post-run finalizer write. Unlike the base-interface default (which
    /// re-reads via <see cref="GetAsync"/> and then calls the unfenced save - two separate lock
    /// acquisitions), this override performs the identity re-read and the upsert under a
    /// <b>single</b> striped session lock, so a concurrent
    /// <see cref="DeleteAsync"/>/<see cref="ArchiveAsync"/> cannot slip between the check and the
    /// write. The re-read deliberately hits SQLite directly (not the in-memory cache): the cache
    /// may still hold the pre-delete session, whereas the authoritative row is what a competing
    /// delete/reset mutated. When the fence fails the cache entry for the (now rebound) session is
    /// evicted so a subsequent read does not serve the stale, resurrected view.
    /// </remarks>
    public override async Task<SessionSaveOutcome> SaveAsync(
        GatewaySession session,
        SessionWriteFence fence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var activity = ActivitySource.StartActivity("session.save.fenced", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", session.SessionId);
        activity?.SetTag("botnexus.agent.id", session.AgentId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // Backfill before the per-session lock so the resolver's per-agent lock does not
        // interleave with concurrent saves of the same session (mirrors the unfenced SaveAsync).
        await EnsureConversationIdStampedAsync(session, cancellationToken).ConfigureAwait(false);

        var sessionLock = await AcquireSessionLockAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = await RetryOnTransientAsync(async () =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Re-read the authoritative row identity (conversation + status) directly from
                // SQLite, inside the lock, before deciding whether to write. A NULL result means
                // the row was deleted mid-run; a diverged conversation_id or a sealed/expired
                // status means a competing reset rebound/sealed it. Either way the fence fails.
                var snapshot = await ReadFenceSnapshotAsync(connection, session.SessionId, cancellationToken).ConfigureAwait(false);
                if (!SessionFenceEvaluator.Passes(fence, snapshot))
                {
                    activity?.SetTag("botnexus.session.fence.rebound", true);
                    return false;
                }

                await UpsertSessionAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await ReplaceHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (persisted)
            {
                // Only refresh the cache on a real write. On a rebound, evict any stale entry so a
                // later read re-materialises the authoritative (deleted/sealed/rebound) state.
                _cache.Set(session.SessionId, session);
                return SessionSaveOutcome.Persisted;
            }

            _cache.Remove(session.SessionId);
            _logger.LogInformation(
                "Fenced session save for {SessionId} skipped as rebound: the on-disk row was deleted, sealed, " +
                "or rebound to another conversation while the run was in flight (#1518).",
                session.SessionId);
            return SessionSaveOutcome.Rebound;
        }
        finally { sessionLock.Dispose(); }
    }

    /// <summary>
    /// Lightweight, lock-scoped read of just the fence-relevant fields (conversation_id + status)
    /// for issue #1518. Returns a synthetic <see cref="GatewaySession"/> carrying only those two
    /// values (never touches history or the conversation store) so
    /// <see cref="SessionFenceEvaluator.Passes"/> can evaluate it, or <c>null</c> when the row no
    /// longer exists. Deliberately avoids <see cref="LoadSessionAsync(SqliteConnection, SessionId, CancellationToken)"/>,
    /// which hydrates AgentId from the conversation store and would throw if a competing reset had
    /// already removed the conversation - the fence must observe "row gone" as a clean skip, not an
    /// exception.
    /// </summary>
    private async Task<GatewaySession?> ReadFenceSnapshotAsync(
        SqliteConnection connection,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, conversation_id
            FROM sessions
            WHERE id = $sessionId
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var status = reader.IsDBNull(0) ? null : reader.GetString(0);
        var conversationIdValue = reader.IsDBNull(1) ? null : reader.GetString(1);

        // Build a minimal session carrying only the fence-relevant fields. AgentId is left
        // unhydrated on purpose - SessionFenceEvaluator only inspects ConversationId and Status.
        var snapshot = new GatewaySession(new Session { SessionId = sessionId }, _redactor)
        {
            Status = ParseStatus(status)
        };
        if (conversationIdValue is not null)
            snapshot.ConversationId = ConversationId.From(conversationIdValue);

        return snapshot;
    }

    /// <inheritdoc />
    public override async Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("session.delete", ActivityKind.Internal);
        activity?.SetTag("botnexus.session.id", sessionId);

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _cache.Remove(sessionId);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var deleteHistory = connection.CreateCommand();
        deleteHistory.CommandText = "DELETE FROM session_history WHERE session_id = $sessionId";
        deleteHistory.Parameters.AddWithValue("$sessionId", sessionId.Value);
        await deleteHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var deleteSession = connection.CreateCommand();
        deleteSession.CommandText = "DELETE FROM sessions WHERE id = $sessionId";
        deleteSession.Parameters.AddWithValue("$sessionId", sessionId.Value);
        await deleteSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // No per-session lock entry to remove: the striped lock pool is fixed-size.
    }

    /// <inheritdoc />
    public override async Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _cache.Remove(sessionId);

        var session = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            session.Status = SessionStatus.Sealed;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            _cache.Set(sessionId, session);
            await RetryOnTransientAsync(async () =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await UpsertSessionAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await ReplaceHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    protected override Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken)
        => RetryOnTransientAsync(async () =>
        {
        // EnumerateSessionsAsync reads across all sessions — safe without per-session lock under WAL
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM sessions
            ORDER BY updated_at DESC
            """;

        // Read all ids first so the data reader is released before we hydrate rows
        // (hydration touches the conversation store, which must not run while this
        // reader holds the connection).
        var ids = new List<SessionId>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(SessionId.From(reader.GetString(0)));
        }

        var sessions = new List<GatewaySession>(ids.Count);
        foreach (var sessionId in ids)
        {
            // #2188: one unrecoverable row (e.g. a non-null conversation_id whose
            // conversation was deleted) must never poison the whole enumeration. The
            // startup self-heal removes such rows, but external DB edits or a heal that
            // has not yet run can still surface one - skip-and-log it here so bulk reads
            // (including CronSessionStartupReconciler during host startup) degrade
            // gracefully instead of throwing.
            GatewaySession? session;
            try
            {
                session = (_cache.TryGet(sessionId, out var c1) ? c1 : null)
                    ?? await LoadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (UnrecoverableSessionException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping unrecoverable session '{SessionId}' during enumeration; it will be quarantined on next startup self-heal.",
                    sessionId.Value);
                continue;
            }

            if (session is not null)
            {
                _cache.Set(sessionId, session);
                sessions.Add(session);
            }
        }

        return (IReadOnlyList<GatewaySession>)sessions;
        }, cancellationToken: cancellationToken);

    /// <summary>
    /// Transcript-free summary read: a single metadata query over <c>sessions</c> with a
    /// <c>COUNT(*)</c> aggregate from <c>session_history</c> for <c>MessageCount</c>. Session
    /// <c>content</c> is never read, so this stays cheap regardless of how large the history
    /// table grows. Replaces the previous warmup path that loaded every session's full
    /// transcript just to build a metadata list (issue #1581).
    /// </summary>
    /// <remarks>
    /// The <paramref name="updatedAfter"/> bound is applied on parsed values rather than in
    /// SQL: timestamps are persisted via <c>DateTimeOffset.ToString("O")</c>, which preserves
    /// the original UTC offset, so a lexicographic <c>updated_at &gt;= ?</c> comparison would be
    /// wrong across rows written at different offsets. AgentId is resolved from
    /// <c>Conversation.AgentId</c> (P9-I) and cached per conversation; rows whose conversation
    /// no longer resolves to an agent are skipped — they cannot belong to an agent's visible
    /// list.
    /// </remarks>
    public override async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        DateTimeOffset updatedAfter,
        CancellationToken cancellationToken = default)
        => await RetryOnTransientAsync(async () =>
        {
            await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.channel_type, s.session_type, s.status,
                       s.created_at, s.updated_at, s.conversation_id,
                       COALESCE(h.cnt, 0) AS message_count
                FROM sessions s
                LEFT JOIN (
                    SELECT session_id, COUNT(*) AS cnt
                    FROM session_history
                    GROUP BY session_id
                ) h ON h.session_id = s.id
                """;

            // Read raw rows first; resolving the agent per row touches the conversation
            // store and must not happen while the data reader holds the connection.
            var rows = new List<SessionRowMapper.SessionSummaryRow>();


            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // #1627: the summary-row ordinals live in SessionRowMapper.MapSummaryRow.
                    // The updatedAfter bound is applied on the parsed value (not in SQL) because
                    // timestamps preserve their original UTC offset, so a lexicographic compare
                    // would be wrong across rows written at different offsets.
                    var row = SessionRowMapper.MapSummaryRow(reader);
                    if (row.Updated < updatedAfter)
                        continue;
                    rows.Add(row);

                }
            }

            var summaries = new List<SessionSummary>(rows.Count);
            foreach (var row in rows)
            {
                var agentId = await ResolveAgentForConversationAsync(row.ConversationId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(agentId))
                    continue;

                // Mirror Session.IsInteractive: a user-facing session is UserAgent-typed and
                // not delivered over the internal "cron" channel.
                var isInteractive = row.Type.Equals(SessionType.UserAgent)
                    && (!row.Channel.HasValue
                        || !string.Equals(row.Channel.Value.Value, "cron", StringComparison.OrdinalIgnoreCase));

                summaries.Add(new SessionSummary(
                    row.Id,
                    agentId,
                    row.Channel,
                    row.Status,
                    row.Type,
                    isInteractive,
                    row.Count,
                    row.Created,
                    row.Updated,
                    row.ConversationId));
            }

            return (IReadOnlyList<SessionSummary>)summaries;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Returns sessions for a single conversation, using the
    /// <c>idx_sessions_conversation_created</c> index to avoid loading the
    /// full session table. Honours the same chronological-ascending /
    /// SessionId-tiebreaker contract as <see cref="SessionStoreBase.ListByConversationAsync"/>.
    /// </summary>
    public override async Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(
        ConversationId conversationId,
        AgentId? agentId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // P9-I (#674): agent_id predicate removed — column is dropped post-migration.
        // The agentId argument is applied post-hydration via the inherited
        // SessionStoreBase contract: the hydrated AgentId comes from Conversation.AgentId
        // (resolved by HydrateAgentIdAsync) and is the authoritative owner. Note that
        // every session in a conversation has the same AgentId by construction
        // (Conversation.AgentId is init-only and shared across all its sessions), so
        // the agentId filter is effectively an assertion that the conversation belongs
        // to the requested agent — and is preserved here for API contract continuity.
        command.CommandText = """
            SELECT id
            FROM sessions
            WHERE conversation_id = $conversationId
            ORDER BY created_at ASC, id ASC
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.Value);

        var sessions = new List<GatewaySession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = SessionId.From(reader.GetString(0));
            var session = (_cache.TryGet(sessionId, out var c2) ? c2 : null)
                ?? await LoadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                _cache.Set(sessionId, session);
                if (agentId is null || session.AgentId == agentId)
                    sessions.Add(session);
            }
        }

        return sessions;
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return; // double-check after acquiring lock

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // #1436: filesystem-aware journal mode (WAL on local disk, DELETE on network
            // mounts) with bounded wal_autocheckpoint, consolidated into the shared helper.
            await _walMaintenance.ApplyJournalModeAsync(connection, connection.DataSource, cancellationToken: cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    channel_type TEXT,
                    caller_id TEXT,
                    session_type TEXT,
                    participants_json TEXT,
                    status TEXT,
                    metadata TEXT,
                    created_at TEXT,
                    updated_at TEXT,
                    conversation_id TEXT
                );

                CREATE TABLE IF NOT EXISTS session_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT,
                    role TEXT,
                    content TEXT,
                    timestamp TEXT,
                    tool_name TEXT,
                    tool_call_id TEXT,
                    is_compaction_summary INTEGER NOT NULL DEFAULT 0,
                    is_crash_sentinel INTEGER NOT NULL DEFAULT 0,
                    is_history INTEGER NOT NULL DEFAULT 0,
                    trigger_type TEXT
                );

                CREATE TABLE IF NOT EXISTS sub_agent_sessions (
                    id TEXT PRIMARY KEY,
                    parent_session_id TEXT NOT NULL,
                    parent_agent_id TEXT NOT NULL,
                    child_agent_id TEXT NOT NULL,
                    archetype TEXT,
                    started_at TEXT NOT NULL,
                    ended_at TEXT,
                    status TEXT NOT NULL DEFAULT 'Active'
                );

                CREATE INDEX IF NOT EXISTS idx_sub_agent_sessions_parent ON sub_agent_sessions(parent_session_id);
                CREATE INDEX IF NOT EXISTS idx_sub_agent_sessions_child ON sub_agent_sessions(child_agent_id);
                CREATE INDEX IF NOT EXISTS idx_session_history_session_id ON session_history(session_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_created_at ON sessions(created_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Migrate: add tool columns to existing databases
            await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

            // P9-I (#674): the legacy idx_sessions_conversation_agent index referenced
            // the (now-dropped) agent_id column. Migration below drops the old shape
            // and recreates as (conversation_id, created_at, id). For fresh DBs this
            // CREATE INDEX IF NOT EXISTS catches the new shape immediately.
            await using var convIndexCmd = connection.CreateCommand();
            convIndexCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_sessions_conversation_created ON sessions(conversation_id, created_at, id);";
            await convIndexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // P9-I (#674): orphan migration runs BEFORE the column drop because it
            // reads the legacy agent_id column. Gated on column existence so fresh DBs
            // skip the scan entirely.
            var hasAgentIdColumn = await HasColumnAsync(connection, "sessions", "agent_id", cancellationToken).ConfigureAwait(false);
            if (hasAgentIdColumn)
            {
                await MigrateOrphanedSessionsAsync(connection, _conversationStore, cancellationToken).ConfigureAwait(false);
            }

            // P9-F (#657): forward any legacy participants_json into the conversation
            // store's normalised participant set. Idempotent (INSERT OR IGNORE inside
            // AddParticipantsAsync). Runs on every startup; cheap when the legacy column
            // has aged out across deployments.
            await BackfillParticipantsToConversationsAsync(connection, _conversationStore, cancellationToken).ConfigureAwait(false);

            // #2188: self-heal sessions whose non-null conversation_id references a
            // conversation that no longer exists. Left in place, each such row throws
            // on every load and (via CronSessionStartupReconciler) can crash gateway
            // startup. Runs on every startup regardless of the legacy agent_id column,
            // since a dangling reference can be created at any time by deleting a
            // conversation while a session row survives.
            await QuarantineDanglingConversationSessionsAsync(connection, _conversationStore, cancellationToken).ConfigureAwait(false);

            // P9-I (#674): drop the legacy agent_id column and its indexes once orphan
            // migration has completed. The column is no longer the source of truth —
            // Conversation.AgentId via IAgentIdentityResolver is. We log a verification
            // summary of any row whose agent_id column disagrees with the conversation's
            // AgentId so operators can spot pre-existing data corruption. SQLite >= 3.35
            // supports ALTER TABLE DROP COLUMN directly. Microsoft.Data.Sqlite ships
            // 3.46+ so this is unconditionally available.
            if (hasAgentIdColumn)
            {
                await DropLegacyAgentIdColumnAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private Task<IDisposable> AcquireSessionLockAsync(SessionId sessionId, CancellationToken cancellationToken)
        => _sessionLocks.AcquireAsync(sessionId, cancellationToken);

    /// <summary>
    /// Returns <c>true</c> when the named column exists on the given SQLite table.
    /// Implemented via <c>PRAGMA table_info(<paramref name="table"/>)</c> which is the
    /// portable, schema-version-independent way to introspect SQLite. Used by P9-I
    /// migration paths to gate legacy <c>agent_id</c> reads/writes/drops so fresh DBs
    /// skip the entire orphan-migration + column-drop dance.
    /// </summary>
    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        // PRAGMA does not support parameter binding for the table name in older SQLite
        // versions; interpolation is safe here because `table` is a hardcoded literal
        // controlled by callers (not user input).
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // table_info columns: cid, name, type, notnull, dflt_value, pk
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// P9-I (#674): drops the legacy <c>sessions.agent_id</c> column and the two
    /// indexes that referenced it. SQLite refuses <c>DROP COLUMN</c> on a column that
    /// is part of any index, so the dependent <c>idx_sessions_agent_id</c> and
    /// <c>idx_sessions_conversation_agent</c> indexes are dropped first. After the
    /// column drop the new conversation-routing index <c>idx_sessions_conversation_created</c>
    /// is created (the fresh-DB shape — it may already exist from the prior CREATE INDEX
    /// IF NOT EXISTS in EnsureCreatedAsync, in which case this is a no-op).
    /// </summary>
    /// <remarks>
    /// A verification scan runs first: any session whose stamped <c>conversation_id</c>
    /// resolves to a different <see cref="Conversation.AgentId"/> than the row's
    /// legacy <c>agent_id</c> column is logged as a warning. We log-and-proceed rather
    /// than fail because the new source of truth (Conversation.AgentId) is the
    /// authoritative durable value post-P9-H; the legacy column was effectively
    /// read-only and may contain stale data from pre-migration writes.
    /// </remarks>
    private async Task DropLegacyAgentIdColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await VerifyAgentIdColumnConsistencyAsync(connection, cancellationToken).ConfigureAwait(false);

        await using (var dropAgentIndex = connection.CreateCommand())
        {
            dropAgentIndex.CommandText = "DROP INDEX IF EXISTS idx_sessions_agent_id;";
            await dropAgentIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var dropConvIndex = connection.CreateCommand())
        {
            dropConvIndex.CommandText = "DROP INDEX IF EXISTS idx_sessions_conversation_agent;";
            await dropConvIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var dropColumn = connection.CreateCommand())
        {
            dropColumn.CommandText = "ALTER TABLE sessions DROP COLUMN agent_id;";
            await dropColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Recreate the conversation-routing index in its new (no agent_id) shape.
        // EnsureCreatedAsync ran CREATE INDEX IF NOT EXISTS earlier; that statement is
        // a no-op when the index already exists, so we run it explicitly here too in
        // case the database had only the old shape and nothing else.
        await using (var newIndex = connection.CreateCommand())
        {
            newIndex.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_sessions_conversation_created ON sessions(conversation_id, created_at, id);";
            await newIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "P9-I: dropped legacy 'sessions.agent_id' column and dependent indexes. " +
            "AgentId is now hydrated from Conversation.AgentId via IAgentIdentityResolver.");
    }

    /// <summary>
    /// Logs a warning for every row whose legacy <c>agent_id</c> column disagrees
    /// with the owning <see cref="Conversation.AgentId"/>. Read-only — never mutates
    /// rows. Skipped (and logged) when a row has a non-null <c>agent_id</c> but no
    /// resolvable conversation, which indicates pre-existing orphan data the migration
    /// could not link.
    /// </summary>
    private async Task VerifyAgentIdColumnConsistencyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var scanCmd = connection.CreateCommand();
        scanCmd.CommandText = """
            SELECT id, agent_id, conversation_id
            FROM sessions
            WHERE agent_id IS NOT NULL
            """;

        var mismatches = 0;
        var unresolvable = 0;
        await using var reader = await scanCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = reader.GetString(0);
            var legacyAgentId = reader.GetString(1);
            var convIdValue = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (convIdValue is null)
            {
                unresolvable++;
                continue;
            }

            var conv = await _conversationStore.GetAsync(ConversationId.From(convIdValue), cancellationToken).ConfigureAwait(false);
            if (conv is null)
            {
                unresolvable++;
                continue;
            }

            if (!string.Equals(conv.AgentId.Value, legacyAgentId, StringComparison.Ordinal))
            {
                mismatches++;
                _logger.LogWarning(
                    "P9-I verification: session {SessionId} legacy agent_id={LegacyAgentId} disagrees with Conversation.AgentId={ConversationAgentId} on {ConversationId}. Conversation.AgentId wins.",
                    sessionId, legacyAgentId, conv.AgentId.Value, convIdValue);
            }
        }

        if (mismatches > 0 || unresolvable > 0)
        {
            _logger.LogWarning(
                "P9-I verification summary: {Mismatches} session(s) had a legacy agent_id that disagreed with the conversation, {Unresolvable} session(s) had no resolvable conversation. Authoritative AgentId is the conversation's.",
                mismatches, unresolvable);
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var migration in new[]
                 {
                     ("tool_name", "TEXT"),
                     ("tool_call_id", "TEXT"),
                     ("is_compaction_summary", "INTEGER NOT NULL DEFAULT 0"),
                     ("tool_args", "TEXT"),
                     ("tool_is_error", "INTEGER NOT NULL DEFAULT 0"),
                     ("is_crash_sentinel", "INTEGER NOT NULL DEFAULT 0"),
                     ("is_history", "INTEGER NOT NULL DEFAULT 0"),
                     // P9-E (#645): trigger_type captures the proxy origin of a turn
                     // (Cron/Soul/Heartbeat/Memory). Idempotent ALTER guarantees existing
                     // pre-P9-E DBs gain the column on the first post-upgrade open.
                     ("trigger_type", "TEXT"),
                     // #1191: thinking content persistence for portal reload
                     ("thinking_content", "TEXT")
                 })
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE session_history ADD COLUMN {migration.Item1} {migration.Item2}";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }
        }

        foreach (var migration in new[]
                 {
                     ("session_type", "TEXT"),
                     ("participants_json", "TEXT"),
                     ("conversation_id", "TEXT")
                 })
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE sessions ADD COLUMN {migration.Item1} {migration.Item2}";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }
        }

        await using var renameStatus = connection.CreateCommand();
        renameStatus.CommandText = """
            UPDATE sessions
            SET status = 'Sealed'
            WHERE lower(status) = 'closed'
            """;
        await renameStatus.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds every session with no <c>conversation_id</c>, groups them by agent, and
    /// links each group to the agent's legacy conversation via
    /// <see cref="LegacyConversationResolver"/>. The most recently updated orphaned
    /// session becomes <see cref="Conversation.ActiveSessionId"/>.
    /// Safe to run on every startup — no-op when there are no orphaned rows.
    /// </summary>
    private async Task MigrateOrphanedSessionsAsync(
        SqliteConnection connection,
        IConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        // Collect distinct agents that have at least one orphaned session.
        await using var agentCmd = connection.CreateCommand();
        agentCmd.CommandText = """
            SELECT DISTINCT agent_id
            FROM sessions
            WHERE conversation_id IS NULL
              AND agent_id IS NOT NULL
            """;

        var agents = new List<string>();
        await using (var agentReader = await agentCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await agentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                agents.Add(agentReader.GetString(0));
        }

        if (agents.Count == 0)
            return;

        // Use the shared resolver so startup migration, save-time stamping, and
        // load-time backfill all converge on the same legacy:{agentId} title/Initiator/Kind.
        var resolver = _legacyResolver ?? new LegacyConversationResolver(conversationStore, logger: null);
        var totalMigrated = 0;

        foreach (var agentIdValue in agents)
        {
            var agentId = AgentId.From(agentIdValue);
            var conversation = await resolver.ResolveAsync(agentId, cancellationToken).ConfigureAwait(false);
            var convIdValue = conversation.ConversationId.Value;

            // Find the most recently updated orphaned session for this agent.
            await using var latestCmd = connection.CreateCommand();
            latestCmd.CommandText = """
                SELECT id
                FROM sessions
                WHERE conversation_id IS NULL
                  AND agent_id = $agentId
                ORDER BY updated_at DESC
                LIMIT 1
                """;
            latestCmd.Parameters.AddWithValue("$agentId", agentIdValue);
            var latestSessionId = (string?)await latestCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            // Bulk-update all orphaned sessions for this agent.
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE sessions
                SET conversation_id = $convId
                WHERE conversation_id IS NULL
                  AND agent_id = $agentId
                """;
            updateCmd.Parameters.AddWithValue("$convId", convIdValue);
            updateCmd.Parameters.AddWithValue("$agentId", agentIdValue);
            var count = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            totalMigrated += count;

            // Point the conversation at the most recently active orphaned session via
            // the shared resolver helper — first-wins under the per-agent lock so any
            // concurrent stamping from a load-time backfill races safely.
            if (latestSessionId is not null)
            {
                await resolver.BindActiveSessionIfNoneAsync(
                    conversation,
                    SessionId.From(latestSessionId),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "Orphaned session migration: linked {Count} session(s) across {AgentCount} agent(s) to their legacy conversations.",
            totalMigrated, agents.Count);
    }

    /// <summary>
    /// #2188 self-heal: finds sessions whose non-null <c>conversation_id</c> has no
    /// matching row in the conversation store and deletes them (row + history) with a
    /// logged summary. Such a row is unrecoverable - its owning agent can only be
    /// resolved from <see cref="Conversation.AgentId"/>, which is gone - so quarantining
    /// it prevents the fatal enumeration/startup crash described in the issue. Safe to
    /// run on every startup: a no-op when every conversation_id resolves.
    /// </summary>
    private async Task QuarantineDanglingConversationSessionsAsync(
        SqliteConnection connection,
        IConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        // Collect distinct non-null conversation ids referenced by session rows.
        await using var scanCmd = connection.CreateCommand();
        scanCmd.CommandText = """
            SELECT id, conversation_id
            FROM sessions
            WHERE conversation_id IS NOT NULL
              AND conversation_id != ''
            """;

        var rows = new List<(string SessionId, string ConversationId)>();
        await using (var reader = await scanCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (rows.Count == 0)
            return;

        // Resolve each distinct conversation once; a conversation that returns null is
        // deleted, so every session referencing it is unrecoverable.
        var resolution = new Dictionary<string, bool>(StringComparer.Ordinal);
        var danglingSessionIds = new List<string>();
        foreach (var (sessionId, convIdValue) in rows)
        {
            if (!resolution.TryGetValue(convIdValue, out var exists))
            {
                var conversation = await conversationStore
                    .GetAsync(ConversationId.From(convIdValue), cancellationToken)
                    .ConfigureAwait(false);
                exists = conversation is not null;
                resolution[convIdValue] = exists;
            }

            if (!exists)
                danglingSessionIds.Add(sessionId);
        }

        if (danglingSessionIds.Count == 0)
            return;

        // Delete each dangling session and its history in a single transaction.
        await using var transaction = connection.BeginTransaction();
        foreach (var sessionId in danglingSessionIds)
        {
            await using var deleteHistory = connection.CreateCommand();
            deleteHistory.Transaction = transaction;
            deleteHistory.CommandText = "DELETE FROM session_history WHERE session_id = $id";
            deleteHistory.Parameters.AddWithValue("$id", sessionId);
            await deleteHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteSession = connection.CreateCommand();
            deleteSession.Transaction = transaction;
            deleteSession.CommandText = "DELETE FROM sessions WHERE id = $id";
            deleteSession.Parameters.AddWithValue("$id", sessionId);
            await deleteSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Evict any cached copy so a later read cannot resurrect the healed row.
            _cache.Remove(SessionId.From(sessionId));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Dangling-conversation self-heal (#2188): quarantined {Count} unrecoverable session(s) whose conversation no longer exists: {SessionIds}.",
            danglingSessionIds.Count,
            string.Join(", ", danglingSessionIds));
    }

    /// <summary>
    /// P9-F (#657): one-shot startup backfill that forwards legacy
    /// <c>sessions.participants_json</c> entries into the conversation store's normalised
    /// participant set. Sessions are grouped by <c>conversation_id</c> and each group is
    /// dispatched to <see cref="IConversationStore.AddParticipantsAsync"/> — idempotent
    /// (<c>INSERT OR IGNORE</c>) so safe to re-run on every startup. Sessions with a NULL
    /// <c>conversation_id</c> are skipped here because the orphan migration immediately
    /// preceding this call already stamps a legacy conversation onto them, so by the time
    /// this scan runs every row that has participants has a destination.
    /// </summary>
    private async Task BackfillParticipantsToConversationsAsync(
        SqliteConnection connection,
        IConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        await using var scanCmd = connection.CreateCommand();
        scanCmd.CommandText = """
            SELECT conversation_id, participants_json
            FROM sessions
            WHERE participants_json IS NOT NULL
              AND participants_json != ''
              AND participants_json != '[]'
              AND conversation_id IS NOT NULL
            """;

        var byConversation = new Dictionary<string, List<SessionParticipant>>(StringComparer.Ordinal);
        await using (var reader = await scanCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var convId = reader.GetString(0);
                var json = reader.GetString(1);
                List<SessionParticipant> participants;
                try
                {
                    participants = DeserializeParticipants(json);
                }
                catch (JsonException ex)
                {
                    // #1751: skip a single corrupt participants_json row rather than aborting the
                    // whole backfill scan. The other valid rows must still be forwarded.
                    _logger.LogWarning(
                        ex,
                        "Skipping corrupt participants_json while backfilling participants for conversation '{ConversationId}'.",
                        convId);
                    continue;
                }
                if (participants.Count == 0)
                    continue;

                if (!byConversation.TryGetValue(convId, out var list))
                {
                    list = [];
                    byConversation[convId] = list;
                }
                list.AddRange(participants);
            }
        }

        if (byConversation.Count == 0)
            return;

        var totalRows = 0;
        foreach (var (convIdString, participants) in byConversation)
        {
            var convId = ConversationId.From(convIdString);
            // Pre-dedupe by CitizenId. AddParticipantsAsync dedupes again, but a smaller
            // payload keeps the conversation-store transaction lighter.
            var deduped = participants
                .GroupBy(p => p.CitizenId)
                .Select(g => g.First())
                .ToList();
            await conversationStore.AddParticipantsAsync(convId, deduped, cancellationToken).ConfigureAwait(false);
            totalRows += deduped.Count;
        }

        _logger.LogInformation(
            "Participant backfill (Sqlite session store): forwarded {Count} participant entries across {ConvCount} conversation(s).",
            totalRows, byConversation.Count);
    }

    /// <summary>
    /// Defensively backfills a session whose <see cref="Session.ConversationId"/> is null
    /// at save time by stamping it with the agent's legacy conversation. This catches the
    /// narrow window between <see cref="EnsureCreatedAsync"/> startup migration completing
    /// and a caller saving a fresh orphan-shaped session (e.g. a test fixture, a code path
    /// that constructs a <see cref="Session"/> without binding it first). Without the
    /// stamp the row would land as <c>conversation_id IS NULL</c> and only be caught on the
    /// next process restart.
    /// </summary>
    /// <remarks>
    /// Mutates <paramref name="session"/> in place so the caller observes the stamped
    /// value and downstream readers see a consistent view. Returns silently when no
    /// conversation store is configured (back-compat for test composition roots that
    /// register a session store without a conversation store).
    /// </remarks>
    private async Task EnsureConversationIdStampedAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (session.ConversationId.IsInitialized())
            return;
        if (_legacyResolver is null)
            return;

        var legacy = await _legacyResolver.ResolveAsync(session.AgentId, cancellationToken).ConfigureAwait(false);
        session.ConversationId = legacy.ConversationId;

        // If this is the current active session, also bind it as the conversation's
        // active session pointer so the canonical reset path (DefaultConversationResetService)
        // can find it. Sealed/Suspended/Expired sessions are not bound.
        if (session.Status == SessionStatus.Active)
        {
            await _legacyResolver.BindActiveSessionIfNoneAsync(legacy, session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Session {SessionId} for agent {AgentId} was saved with no ConversationId; stamped legacy conversation {LegacyConversationId}.",
            session.SessionId, session.AgentId, legacy.ConversationId);
    }

    /// <summary>
    /// P9-I (#674): hydrates <see cref="GatewaySession.AgentId"/> on a loaded session
    /// from <see cref="Conversation.AgentId"/>. Replaces the deleted load-time read of
    /// the legacy <c>sessions.agent_id</c> column. Throws <see cref="InvalidOperationException"/>
    /// when <c>ConversationId</c> is unset (data corruption — the orphan migration should
    /// have stamped every row) or the conversation does not exist in the conversation
    /// store (also corruption — the conversation row was deleted out from under the
    /// session).
    /// </summary>
    /// <remarks>
    /// Uses a positive-only <see cref="BoundedLruCache{TKey,TValue}"/> cache keyed by
    /// <see cref="ConversationId"/>. The cache is safe because <see cref="Conversation.AgentId"/>
    /// is init-only (verified by <c>ConversationAgentIdImmutabilityArchitectureTests</c>).
    /// Negative results are not cached so a create-then-resolve race never observes a
    /// sticky null.
    /// </remarks>
    private async Task HydrateAgentIdAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (!session.ConversationId.IsInitialized())
        {
            throw new UnrecoverableSessionException(
                session.SessionId.Value,
                $"Session '{session.SessionId.Value}' has an unset ConversationId after load. " +
                "Post-P9-I, every session row is guaranteed to carry a non-null conversation_id " +
                "(the orphan migration runs on every startup before this load could be served). " +
                "Either the database was modified externally or the migration failed silently — " +
                "inspect SqliteSessionStore logs at startup.");
        }

        if (_agentIdCache.TryGet(session.ConversationId, out var cached))
        {
            session.HydrateAgentId(cached);
            return;
        }

        var conversation = await _conversationStore.GetAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            throw new UnrecoverableSessionException(
                session.SessionId.Value,
                $"Session '{session.SessionId.Value}' references conversation '{session.ConversationId.Value}' " +
                "which does not exist in the conversation store. AgentId cannot be hydrated. " +
                "This indicates that the conversation was deleted while the session row remained — " +
                "the session is unrecoverable.");
        }

        _agentIdCache.Set(session.ConversationId, conversation.AgentId);
        session.HydrateAgentId(conversation.AgentId);
    }

    private async Task<GatewaySession?> LoadSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewaySession?> LoadSessionAsync(SqliteConnection connection, SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var sessionCommand = connection.CreateCommand();
        // P9-I (#674): agent_id column removed from SELECT — AgentId is hydrated via
        // HydrateAgentIdAsync below from Conversation.AgentId. The column itself is
        // dropped from the schema by DropLegacyAgentIdColumnAsync at startup.
        sessionCommand.CommandText = """
            SELECT id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id
            FROM sessions
            WHERE id = $sessionId
            """;
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var reader = await sessionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        // #1627: the per-session row ordinals live in SessionRowMapper.MapSession - the single
        // source of truth for this shape. participants_json is read-and-discarded there (P9-F);
        // a NULL conversation_id leaves Session.ConversationId at its uninitialized sentinel.
        var mapped = SessionRowMapper.MapSession(reader);
        var session = new GatewaySession(mapped.Session, _redactor)
        {
            // P9-I (#674): AgentId is no longer sourced from a legacy column on the
            // session row - it's hydrated immediately below via HydrateAgentIdAsync
            // from Conversation.AgentId. We construct GatewaySession with the
            // CallerId from the row and leave AgentId to HydrateAgentId(...).
            CallerId = mapped.CallerId
        };


        await reader.DisposeAsync().ConfigureAwait(false);

        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = """
            SELECT role, content, timestamp, tool_name, tool_call_id, is_compaction_summary, tool_args, tool_is_error, is_crash_sentinel, is_history, trigger_type, thinking_content
            FROM session_history
            WHERE session_id = $sessionId
            ORDER BY id ASC
            """;
        historyCommand.Parameters.AddWithValue("$sessionId", sessionId.Value);

        await using var historyReader = await historyCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<SessionEntry>();
        while (await historyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // #1627: all 12 history-column ordinals live in SessionRowMapper.MapHistoryEntry.
            entries.Add(SessionRowMapper.MapHistoryEntry(historyReader));

        }

        // Phase 3a (#531): the full transcript is preserved in storage; the LLM-visible
        // projection is computed at runtime via IsHistory/IsCrashSentinel flags. Legacy
        // pre-Phase-3a databases may contain multiple IsCompactionSummary rows all with
        // IsHistory=false because the old code applied a load-time slice to hide them;
        // collapse those forward so only the latest summary is LLM-visible after migration.
        entries = SessionCompaction.ApplyLegacyHistoryProjection(entries);

        if (entries.Count > 0)
            session.AddEntries(entries);

        // #1627: re-stamp the persisted UpdatedAt after AddEntries so adding history rows
        // does not bump it past the value loaded from the sessions row. SessionRow surfaces the
        // row's updated_at directly (mapped.UpdatedAt) so we read it without reaching through to
        // the inner record's Session.UpdatedAt - that reach-through is banned by F-9 / Phase 7.
        session.UpdatedAt = mapped.UpdatedAt;

        // P9-I (#674): hydrate AgentId from Conversation.AgentId before returning. Every
        // load path (GetAsync, GetOrCreateAsync, EnumerateSessionsAsync, ListByConversationAsync)
        // routes through this method so callers always observe a hydrated AgentId.
        await HydrateAgentIdAsync(session, cancellationToken).ConfigureAwait(false);

        return session;
    }

    private async Task UpsertSessionAsync(SqliteConnection connection, GatewaySession session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // P9-I (#674): agent_id column removed — AgentId lives on Conversation.AgentId
        // and is hydrated on load via IAgentIdentityResolver. Writing it here is a no-op
        // post-migration because the column has been ALTER TABLE DROP COLUMN'd.
        command.CommandText = """
            INSERT INTO sessions (id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id)
            VALUES ($id, $channelType, $callerId, $sessionType, $participantsJson, $status, $metadata, $createdAt, $updatedAt, $conversationId)
            ON CONFLICT(id) DO UPDATE SET
                channel_type = excluded.channel_type,
                caller_id = excluded.caller_id,
                session_type = excluded.session_type,
                participants_json = excluded.participants_json,
                status = excluded.status,
                metadata = excluded.metadata,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                conversation_id = excluded.conversation_id
            """;
        command.Parameters.AddWithValue("$id", session.SessionId.Value);
        command.Parameters.AddWithValue("$channelType", session.ChannelType.HasValue ? session.ChannelType.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$callerId", (object?)session.CallerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionType", session.SessionType.Value);
        // P9-F (#657): write `[]` so the backfill scan's "non-empty" filter excludes
        // new rows. The column is retained for one phase as a rollback source.
        command.Parameters.AddWithValue("$participantsJson", "[]");
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(session.Metadata, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", session.UpdatedAt.ToString("O"));
        // Phase 9 / P9-B-2 (#627): the save-time backfill (EnsureConversationIdStampedAsync,
        // line 136) is responsible for guaranteeing a non-default ConversationId by the
        // time we reach this writer. Fail loud here instead of silently writing a NULL
        // row — a NULL row would re-introduce the orphan condition the non-null flip is
        // supposed to prevent.
        if (!session.ConversationId.IsInitialized())
            throw new InvalidOperationException(
                $"Refusing to persist session '{session.SessionId.Value}' for agent " +
                $"'{session.AgentId.Value}' with an unset ConversationId. Save-time " +
                $"backfill (EnsureConversationIdStampedAsync) should have stamped this " +
                $"before reaching the writer. Either register an IConversationStore on " +
                $"the SqliteSessionStore constructor or set ConversationId explicitly.");
        command.Parameters.AddWithValue("$conversationId", session.ConversationId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceHistoryAsync(SqliteConnection connection, GatewaySession session, CancellationToken cancellationToken)
    {
        await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)dbTransaction;

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM session_history WHERE session_id = $sessionId";
        deleteCommand.Parameters.AddWithValue("$sessionId", session.SessionId.Value);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // #1628: prepare the INSERT command + its parameters ONCE instead of recreating a
        // fresh SqliteCommand and re-adding all 13 parameters per row. Behaviour is identical
        // (same SQL, same parameter values, same order, same transaction + commit); only the
        // per-row .Value is reset inside the loop. $sessionId is constant for the whole call,
        // so it is bound once before the loop.
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO session_history (session_id, role, content, timestamp, tool_name, tool_call_id, is_compaction_summary, tool_args, tool_is_error, is_crash_sentinel, is_history, trigger_type, thinking_content)
            VALUES ($sessionId, $role, $content, $timestamp, $toolName, $toolCallId, $isCompactionSummary, $toolArgs, $toolIsError, $isCrashSentinel, $isHistory, $triggerType, $thinkingContent)
            """;
        insertCommand.Parameters.AddWithValue("$sessionId", session.SessionId.Value);
        var pRole = insertCommand.Parameters.AddWithValue("$role", string.Empty);
        var pContent = insertCommand.Parameters.AddWithValue("$content", string.Empty);
        var pTimestamp = insertCommand.Parameters.AddWithValue("$timestamp", string.Empty);
        var pToolName = insertCommand.Parameters.AddWithValue("$toolName", DBNull.Value);
        var pToolCallId = insertCommand.Parameters.AddWithValue("$toolCallId", DBNull.Value);
        var pIsCompactionSummary = insertCommand.Parameters.AddWithValue("$isCompactionSummary", 0);
        var pToolArgs = insertCommand.Parameters.AddWithValue("$toolArgs", DBNull.Value);
        var pToolIsError = insertCommand.Parameters.AddWithValue("$toolIsError", 0);
        var pIsCrashSentinel = insertCommand.Parameters.AddWithValue("$isCrashSentinel", 0);
        var pIsHistory = insertCommand.Parameters.AddWithValue("$isHistory", 0);
        var pTriggerType = insertCommand.Parameters.AddWithValue("$triggerType", DBNull.Value);
        var pThinkingContent = insertCommand.Parameters.AddWithValue("$thinkingContent", DBNull.Value);

        foreach (var entry in session.GetHistorySnapshot())
        {
            pRole.Value = entry.Role.Value;
            pContent.Value = entry.Content;
            pTimestamp.Value = entry.Timestamp.ToString("O");
            pToolName.Value = (object?)entry.ToolName ?? DBNull.Value;
            pToolCallId.Value = (object?)entry.ToolCallId ?? DBNull.Value;
            pIsCompactionSummary.Value = entry.IsCompactionSummary ? 1 : 0;
            pToolArgs.Value = (object?)entry.ToolArgs ?? DBNull.Value;
            pToolIsError.Value = entry.ToolIsError ? 1 : 0;
            pIsCrashSentinel.Value = entry.IsCrashSentinel ? 1 : 0;
            pIsHistory.Value = entry.IsHistory ? 1 : 0;
            pTriggerType.Value = (object?)entry.Trigger?.Value ?? DBNull.Value;
            pThinkingContent.Value = (object?)entry.ThinkingContent ?? DBNull.Value;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    // SQLite error codes that indicate a transient condition safe to retry.
    private static readonly int[] TransientSqliteErrorCodes = [5 /* BUSY */, 10 /* IOERR */];

    /// <summary>
    /// Returns true if the exception represents a transient SQLite condition safe to retry.
    /// Covers BUSY (5), IOERR (10), and the "cannot rollback" phantom transaction error.
    /// </summary>
    private static bool IsTransientSqliteException(SqliteException ex)
        => TransientSqliteErrorCodes.Contains(ex.SqliteErrorCode)
           || (ex.SqliteErrorCode == 1 && ex.Message.Contains("cannot rollback", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Executes <paramref name="operation"/> with up to <paramref name="maxAttempts"/> retries
    /// on transient SQLite errors (BUSY=5, IOERR=10, and phantom rollback errors).
    /// Waits ~50ms × 2^attempt between retries.
    /// After all retries are exhausted, wraps the last exception in
    /// <see cref="SessionStoreUnavailableException"/>.
    /// </summary>
    private static async Task<T> RetryOnTransientAsync<T>(
        Func<Task<T>> operation,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        Exception? lastEx = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransientSqliteException(ex))
            {
                lastEx = ex;
                var delayMs = 50 * (1 << attempt); // 50ms, 100ms, 200ms
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new SessionStoreUnavailableException(
            $"Session store unavailable after {maxAttempts} attempts.", lastEx!);
    }

    private static async Task RetryOnTransientAsync(
        Func<Task> operation,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
        => await RetryOnTransientAsync(
            async () => { await operation().ConfigureAwait(false); return 0; },
            maxAttempts,
            cancellationToken).ConfigureAwait(false);


    private static SessionStatus ParseStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "closed" => SessionStatus.Sealed,
            _ when Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var parsed) => parsed,
            _ => SessionStatus.Active
        };


    private static List<SessionParticipant> DeserializeParticipants(string? participantsJson)
    {
        if (string.IsNullOrWhiteSpace(participantsJson))
            return [];

        return JsonSerializer.Deserialize<List<SessionParticipant>>(participantsJson, JsonOptions) ?? [];
    }


    /// <inheritdoc />
    public override async Task SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO sub_agent_sessions
                (id, parent_session_id, parent_agent_id, child_agent_id, archetype, started_at, ended_at, status)
            VALUES
                (@id, @parentSessionId, @parentAgentId, @childAgentId, @archetype, @startedAt, NULL, 'Active')
            """;
        cmd.Parameters.AddWithValue("@id", info.SubAgentId);
        cmd.Parameters.AddWithValue("@parentSessionId", info.ParentSessionId.Value);
        cmd.Parameters.AddWithValue("@parentAgentId", info.ParentAgentId ?? string.Empty);
        cmd.Parameters.AddWithValue("@childAgentId", info.ChildAgentId ?? string.Empty);
        cmd.Parameters.AddWithValue("@archetype", info.Archetype.ToString());
        cmd.Parameters.AddWithValue("@startedAt", info.StartedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task UpdateSubAgentSessionAsync(
        string subAgentId,
        DateTimeOffset endedAt,
        string status,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sub_agent_sessions
            SET ended_at = @endedAt, status = @status
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", subAgentId);
        cmd.Parameters.AddWithValue("@endedAt", endedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubAgentSessionSummary>> ListSubAgentSessionsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, parent_session_id, parent_agent_id, child_agent_id,
                   archetype, started_at, ended_at, status
            FROM sub_agent_sessions
            WHERE parent_session_id = $parentSessionId
            ORDER BY started_at ASC
            """;
        command.Parameters.AddWithValue("$parentSessionId", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SubAgentSessionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // #1627: the sub_agent_sessions ordinals live in SessionRowMapper.MapSubAgentSession.
            results.Add(SessionRowMapper.MapSubAgentSession(reader));

        }
        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// #1941: platform-wide observability read. Opens the existing <c>sub_agent_sessions</c> table
    /// read-only (no schema change, no write path) and returns rows across all parent sessions,
    /// newest-started first, with an optional case-insensitive status filter and a bounded row cap.
    /// The <c>started_at</c> ordinals map through <see cref="SessionRowMapper.MapSubAgentSession"/>,
    /// the same projection used by the parent-scoped <see cref="ListSubAgentSessionsAsync"/>.
    /// </remarks>
    public override async Task<IReadOnlyList<SubAgentSessionSummary>> ListAllSubAgentSessionsAsync(
        string? status = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var hasStatus = !string.IsNullOrWhiteSpace(status);
        command.CommandText =
            $"""
            SELECT id, parent_session_id, parent_agent_id, child_agent_id,
                   archetype, started_at, ended_at, status
            FROM sub_agent_sessions
            {(hasStatus ? "WHERE lower(status) = lower($status)" : string.Empty)}
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        if (hasStatus)
            command.Parameters.AddWithValue("$status", status!);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SubAgentSessionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // #1627: the sub_agent_sessions ordinals live in SessionRowMapper.MapSubAgentSession.
            results.Add(SessionRowMapper.MapSubAgentSession(reader));
        }
        return results;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// #1379 Finding 3: this is a diagnostics/counting endpoint, so it must NOT pay the
    /// full list-everything cost. The previous implementation called <c>ListAsync</c>,
    /// which materializes a <see cref="GatewaySession"/> per row through the N+1
    /// <c>LoadSessionAsync</c> path (row + history JSONL) and then runs a cross-store
    /// <c>HydrateAgentIdAsync</c> lookup per session — solely to produce three integer
    /// histograms. Instead we compute counts with a single <c>GROUP BY</c> SQL aggregate
    /// over the lightweight <c>sessions</c> table (no history, no domain objects), then
    /// resolve <c>AgentId</c> once per distinct <c>conversation_id</c> via the
    /// conversation store (cached in <c>_agentIdCache</c>). Cost drops from
    /// O(sessions × (load + hydrate)) to O(1 aggregate query + distinct-conversation lookups).
    /// <para>
    /// Post-P9-I (#674) the <c>sessions.agent_id</c> column is dropped, so the agent
    /// cannot be grouped purely in SQL — the authoritative owner is
    /// <c>Conversation.AgentId</c>. We therefore group by <c>(status, conversation_id)</c>
    /// and fold the conversation→agent mapping in memory. The optional <c>agentId</c>
    /// filter is applied against that resolved agent, preserving the prior semantics.
    /// </para>
    /// </remarks>
    public async Task<SessionStats?> GetStatsAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        return await RetryOnTransientAsync(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // One aggregate query: per (status, conversation) session counts. Reading
            // conversation_id lets us resolve the authoritative AgentId (Conversation.AgentId)
            // without loading any GatewaySession or its history.
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, conversation_id, COUNT(*) AS cnt
                FROM sessions
                GROUP BY status, conversation_id
                """;

            var groups = new List<(string? Status, string? ConversationId, int Count)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var status = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var conversationId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var count = reader.GetInt32(2);
                    groups.Add((status, conversationId, count));
                }
            }

            var byStatus = new Dictionary<string, int>();
            var byAgentCounts = new Dictionary<string, int>();
            var total = 0;

            foreach (var (status, conversationId, count) in groups)
            {
                var resolvedAgent = await ResolveAgentForConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);

                // Apply the optional agent filter against the authoritative owner.
                if (agentId is not null && resolvedAgent != agentId.Value.Value)
                {
                    continue;
                }

                total += count;

                // Normalize the raw status string through the same ParseStatus mapping the
                // load path uses, so the dictionary keys are byte-for-byte equivalent to the
                // previous s.Status.ToString() projection (e.g. legacy "closed" -> "Sealed",
                // case-insensitive parse, null/unknown -> Active).
                var statusKey = ParseStatus(status).ToString();
                byStatus[statusKey] = byStatus.GetValueOrDefault(statusKey) + count;

                if (resolvedAgent is not null)
                {
                    byAgentCounts[resolvedAgent] = byAgentCounts.GetValueOrDefault(resolvedAgent) + count;
                }
            }

            var byAgent = byAgentCounts
                .Select(kvp => new AgentSessionCount(kvp.Key, kvp.Value))
                .OrderByDescending(a => a.Count)
                .ToList();

            return new SessionStats
            {
                TotalSessions = total,
                ByStatus = byStatus,
                ByAgent = byAgent,
                Compaction = new CompactionStats(0, total),
                GeneratedAt = DateTimeOffset.UtcNow
            };
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the authoritative <c>AgentId</c> string for a session's
    /// <c>conversation_id</c> via the conversation store, caching the result in
    /// <c>_agentIdCache</c> (shared with the per-session hydration path). Returns
    /// <see langword="null"/> when the conversation_id is null/blank or the
    /// conversation no longer exists (an orphaned session row) — stats must not throw
    /// on a single orphan, so such rows are counted toward totals/status but contribute
    /// no agent bucket.
    /// </summary>
    private async Task<string?> ResolveAgentForConversationAsync(string? conversationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return null;
        }

        var convId = ConversationId.From(conversationId);
        if (_agentIdCache.TryGet(convId, out var cached))
        {
            return cached.Value;
        }

        var conversation = await _conversationStore.GetAsync(convId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        _agentIdCache.Set(convId, conversation.AgentId);
        return conversation.AgentId.Value;
    }
}
