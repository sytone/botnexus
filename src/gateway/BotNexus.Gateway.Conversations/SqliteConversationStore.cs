using System.Diagnostics;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;
using BotNexus.Persistence.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// SQLite-backed conversation store for persistent gateway conversations.
/// Uses WAL journal mode, a one-time schema initialisation lock, and per-conversation
/// <see cref="SemaphoreSlim"/> instances so independent conversations can be accessed concurrently.
/// Conversations are cached in memory after the first successful load.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private static readonly ActivitySource ActivitySource = new("BotNexus.Gateway");
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteConversationStore> _logger;
    private readonly IWorldContext? _worldContext;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // Striped write locks: a fixed pool hashed by conversation id. Bounds the number
    // of sync primitives (no per-conversation SemaphoreSlim leak over the process
    // lifetime) and cannot strand a lock on an exception (the stripe is pool-owned).
    private readonly StripedAsyncLock _conversationLocks = new();
    // Bounded read-through cache capped by LRU so a long-running gateway does not
    // retain every conversation ever touched. Cold reads fall through to SQLite.
    private readonly BoundedLruCache<string, Conversation> _cache;
    private bool _initialized;

    // Read round-trip counter (issue #1626). Incremented once per database query issued by the
    // batched list path of THIS store instance, so the N+1 regression guard can assert that a
    // list of N conversations does not fan out per-row. Instance-scoped (not static) so parallel
    // test classes never interfere; best-effort (Interlocked, no ordering needs) and negligible
    // cost. Only inspected by tests via the internal hooks below.
    private long _readRoundTrips;

    /// <summary>
    /// Test hook (issue #1626): number of database read round-trips issued by this store's batched
    /// list loader since construction (or the last <see cref="ResetReadRoundTripCount"/>). Used by
    /// the N+1 regression guard to assert list endpoints do not issue a per-row query fan-out.
    /// </summary>
    internal long ReadRoundTripCount => Interlocked.Read(ref _readRoundTrips);

    /// <summary>Test hook (issue #1626): resets <see cref="ReadRoundTripCount"/> to zero.</summary>
    internal void ResetReadRoundTripCount() => Interlocked.Exchange(ref _readRoundTrips, 0);

    /// <summary>
    /// Test hook: runs the shared <see cref="MaterializeOrderedAsync"/> hydrate pass against an
    /// explicit, caller-supplied ordered id set so tests can reproduce the concurrent-delete race
    /// deterministically — an id captured by a list select that is then deleted before hydration
    /// (e.g. the cron noop-session prune, issue #1754) must be omitted from the result rather than
    /// throwing a 500 (regression for the enumeration-race fix).
    /// </summary>
    internal async Task<IReadOnlyList<Conversation>> MaterializeOrderedForTestAsync(
        IReadOnlyList<string> orderedIds,
        CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await MaterializeOrderedAsync(connection, orderedIds, ct).ConfigureAwait(false);
    }

    /// <summary>Default bound for the in-memory conversation cache (entries).</summary>
    public const int DefaultConversationCacheCapacity = 1000;

    /// <summary>
    /// Initialises a new instance of the <see cref="SqliteConversationStore"/> class without
    /// world stamping. Kept for tests and bare wire-ups; production callers should use the
    /// world-aware overload so <see cref="Conversation.WorldId"/> is stamped on persistence.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="logger">Logger instance.</param>
    public SqliteConversationStore(string connectionString, ILogger<SqliteConversationStore> logger)
        : this(connectionString, logger, worldContext: null)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="SqliteConversationStore"/> class that
    /// stamps the current world id on persisted conversations and lazy-backfills the field for
    /// pre-Phase-9 rows loaded with an empty <see cref="Conversation.WorldId"/>.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="worldContext">Resolves the gateway's current world identity; <c>null</c> disables stamping.</param>
    /// <param name="cacheCapacity">
    /// Maximum number of conversations retained in the in-memory cache. Older entries are
    /// evicted by LRU; cold reads fall through to SQLite. Defaults to
    /// <see cref="DefaultConversationCacheCapacity"/>.
    /// </param>
    public SqliteConversationStore(string connectionString, ILogger<SqliteConversationStore> logger, IWorldContext? worldContext, int cacheCapacity = DefaultConversationCacheCapacity)
    {
        _connectionString = connectionString;
        _logger = logger;
        _worldContext = worldContext;
        _cache = new BoundedLruCache<string, Conversation>(cacheCapacity, StringComparer.Ordinal);
    }

    // World-id stamping/back-fill is shared across all three conversation stores — see
    // ConversationStoreShared (#1383). These forwarders thread this store's world context
    // into the shared logic while keeping the existing call-site signatures unchanged.
    // (Sqlite scopes ListForCitizen with a SQL predicate rather than the shared MatchesCitizen.)
    private void StampWorldId(Conversation conversation)
        => ConversationStoreShared.StampWorldId(conversation, _worldContext);

    private Conversation? BackfillWorldId(Conversation? conversation)
        => ConversationStoreShared.BackfillWorldId(conversation, _worldContext);

    /// <inheritdoc />
    public async Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.get", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(conversationId.Value, out var cached))
                return BackfillWorldId(CloneConversation(cached));

            var loaded = await LoadConversationAsync(conversationId, ct).ConfigureAwait(false);
            if (loaded is not null)
                _cache.Set(conversationId.Value, CloneConversation(loaded));

            return BackfillWorldId(loaded);
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.list", ActivityKind.Internal);
        if (agentId is not null)
            activity?.SetTag("botnexus.agent.id", agentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = agentId is null
            ? "SELECT id FROM conversations ORDER BY updated_at DESC"
            : "SELECT id FROM conversations WHERE agent_id = $agentId ORDER BY updated_at DESC";
        if (agentId is not null)
            command.Parameters.AddWithValue("$agentId", agentId.Value.Value);

        var orderedIds = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                orderedIds.Add(reader.GetString(0));
        }

        return await MaterializeOrderedAsync(connection, orderedIds, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
    {
        if (!citizen.IsValid)
            throw new ArgumentException("Citizen must be a valid (non-default) CitizenId.", nameof(citizen));

        using var activity = ActivitySource.StartActivity("conversation.list_for_citizen", ActivityKind.Internal);
        activity?.SetTag("botnexus.citizen.kind", citizen.Kind.ToString());
        activity?.SetTag("botnexus.citizen", citizen.ToString());

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Union semantics (P9-F, issue #657): rows whose initiator matches, plus (only for
        // Agent species) rows whose owning agent matches, plus rows where the citizen is in
        // the conversation_participants set.
        //
        // This is authored as a SQL UNION of three single-table (sargable) branches rather than
        // the earlier `LEFT JOIN ... WHERE a OR b OR c` shape. SQLite cannot use an index for a
        // WHERE disjunction whose terms span both joined tables, so the OR form forced a full
        // `SCAN conversations` + a per-row participant join + a TEMP B-TREE for DISTINCT — an
        // O(conversations x participants) plan that degraded super-linearly as the store grew and
        // produced multi-second GET /api/conversations latencies under load (the same call site
        // observed taking ~145s before its cancellation token fired). Splitting into a UNION lets
        // each branch seek its own index — idx_conversations_initiator, idx_conversations_agent_id,
        // and idx_conversation_participants_citizen respectively — and UNION supplies the same
        // duplicate collapse the DISTINCT did. Verified via EXPLAIN QUERY PLAN: full SCAN -> three
        // index SEARCHes. The agent-match branch is only meaningful for Agent-kind citizens; the
        // $isAgent = 0 constant makes its WHERE unsatisfiable so it contributes no rows for
        // non-agent citizens (no wasted scan). Ordering is applied once on the unioned result.
        command.CommandText = """
            SELECT id FROM (
                SELECT c.id AS id, c.updated_at AS updated_at
                FROM conversations c
                WHERE c.initiator = $initiator
              UNION
                SELECT c.id AS id, c.updated_at AS updated_at
                FROM conversations c
                WHERE $isAgent = 1 AND c.agent_id = $agentMatch
              UNION
                SELECT c.id AS id, c.updated_at AS updated_at
                FROM conversations c
                INNER JOIN conversation_participants p ON p.conversation_id = c.id
                WHERE p.citizen_kind = $citizenKind AND p.citizen_id = $citizenIdValue
            )
            ORDER BY updated_at DESC
            """;
        command.Parameters.AddWithValue("$initiator", citizen.ToString());
        command.Parameters.AddWithValue("$isAgent", citizen.Kind == CitizenKind.Agent ? 1 : 0);
        command.Parameters.AddWithValue("$agentMatch",
            citizen.Kind == CitizenKind.Agent ? (object)citizen.AsAgent!.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$citizenKind", citizen.Kind.ToString());
        command.Parameters.AddWithValue("$citizenIdValue", citizen.Value);

        var orderedIds = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                orderedIds.Add(reader.GetString(0));
        }

        return await MaterializeOrderedAsync(connection, orderedIds, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddParticipantsAsync(
        ConversationId conversationId,
        IEnumerable<SessionParticipant> participants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participants);

        // Snapshot once so a streaming enumerable isn't reread under the lock.
        var snapshot = participants as IReadOnlyCollection<SessionParticipant> ?? participants.ToArray();
        if (snapshot.Count == 0)
            return;

        using var activity = ActivitySource.StartActivity("conversation.add_participants", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);
        activity?.SetTag("botnexus.participants.count", snapshot.Count);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // #1628: prepare the INSERT command + its parameters ONCE instead of recreating a
            // fresh SqliteCommand and re-adding all 4 parameters per row. Behaviour is identical
            // (same SQL, same parameter values, same order, same transaction + commit, same
            // INSERT OR IGNORE first-add-wins, same invalid-citizen skip); only the per-row
            // .Value is reset inside the loop. $conversationId is constant for the whole call,
            // so it is bound once before the loop.
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            // INSERT OR IGNORE preserves the existing role label when a citizen is already
            // registered as a participant (first-add wins). New citizens are inserted with
            // the supplied role.
            insertCommand.CommandText = """
                INSERT OR IGNORE INTO conversation_participants
                    (conversation_id, citizen_kind, citizen_id, role)
                VALUES ($conversationId, $citizenKind, $citizenId, $role)
                """;
            insertCommand.Parameters.AddWithValue("$conversationId", conversationId.Value);
            var pCitizenKind = insertCommand.Parameters.AddWithValue("$citizenKind", string.Empty);
            var pCitizenId = insertCommand.Parameters.AddWithValue("$citizenId", string.Empty);
            var pRole = insertCommand.Parameters.AddWithValue("$role", DBNull.Value);

            foreach (var participant in snapshot)
            {
                if (!participant.CitizenId.IsValid)
                    continue;

                pCitizenKind.Value = participant.CitizenId.Kind.ToString();
                pCitizenId.Value = participant.CitizenId.Value;
                pRole.Value = (object?)participant.Role ?? DBNull.Value;
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            // Invalidate cache entry — the next read will repopulate Participants via the
            // LoadParticipantsAsync join. Cheaper than mutating the cached list in place.
            _cache.Remove(conversationId.Value);
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.create", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversation.ConversationId.Value);
        activity?.SetTag("botnexus.agent.id", conversation.AgentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversation.ConversationId.Value, ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(conversation.ConversationId.Value, out _))
                throw new InvalidOperationException($"A conversation with id '{conversation.ConversationId}' already exists.");

            StampWorldId(conversation);
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SaveConversationAsync(connection, conversation, upsert: false, ct).ConfigureAwait(false);
            _cache.Set(conversation.ConversationId.Value, CloneConversation(conversation));
            return CloneConversation(conversation);
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        ValidateLifecycleState(conversation);
        using var activity = ActivitySource.StartActivity("conversation.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversation.ConversationId.Value);
        activity?.SetTag("botnexus.agent.id", conversation.AgentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversation.ConversationId.Value, ct).ConfigureAwait(false);
        try
        {
            var updated = CloneConversation(conversation);
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            StampWorldId(updated);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SaveConversationAsync(connection, updated, upsert: true, ct).ConfigureAwait(false);
            _cache.Set(updated.ConversationId.Value, CloneConversation(updated));
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.archive", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var updatedAt = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversations
                SET status = $status,
                    active_session_id = NULL,
                    updated_at = $updatedAt
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$status", ConversationStatus.Archived.ToString());
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", conversationId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (_cache.TryGet(conversationId.Value, out var cached))
            {
                var archived = CloneConversation(cached);
                archived.Status = ConversationStatus.Archived;
                archived.ActiveSessionId = null;
                archived.UpdatedAt = updatedAt;
                _cache.Set(conversationId.Value, archived);
            }
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(
        ConversationId conversationId,
        string source,
        string? correlationId,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        using var activity = ActivitySource.StartActivity("conversation.archive", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);
        activity?.SetTag("botnexus.conversation.archive_source", source);
        activity?.SetTag("botnexus.conversation.correlation_id", correlationId);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureAuditSchemaAsync(connection, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var updatedAt = DateTimeOffset.UtcNow;

            await using (var archive = connection.CreateCommand())
            {
                archive.Transaction = transaction;
                archive.CommandText = """
                    UPDATE conversations
                    SET status = $status,
                        active_session_id = NULL,
                        updated_at = $updatedAt
                    WHERE id = $id
                    """;
                archive.Parameters.AddWithValue("$status", ConversationStatus.Archived.ToString());
                archive.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
                archive.Parameters.AddWithValue("$id", conversationId.Value);
                await archive.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText = """
                    INSERT INTO conversation_audit
                        (conversation_id, action, actor, source, correlation_id, previous_value, new_value, timestamp)
                    VALUES ($conversationId, 'archived', $actor, $source, $correlationId, NULL, NULL, $timestamp)
                    """;
                audit.Parameters.AddWithValue("$conversationId", conversationId.Value);
                audit.Parameters.AddWithValue("$actor", actor);
                audit.Parameters.AddWithValue("$source", source);
                audit.Parameters.AddWithValue("$correlationId", (object?)correlationId ?? DBNull.Value);
                audit.Parameters.AddWithValue("$timestamp", updatedAt.ToString("O"));
                await audit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (_cache.TryGet(conversationId.Value, out var cached))
            {
                var archived = CloneConversation(cached);
                archived.Status = ConversationStatus.Archived;
                archived.ActiveSessionId = null;
                archived.UpdatedAt = updatedAt;
                _cache.Set(conversationId.Value, archived);
            }
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    private static async Task EnsureAuditSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                action TEXT NOT NULL,
                actor TEXT NOT NULL,
                source TEXT NOT NULL,
                correlation_id TEXT,
                previous_value TEXT,
                new_value TEXT,
                timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_conversation_audit_conversation_id
                ON conversation_audit(conversation_id, timestamp DESC);
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info(conversation_audit);";
        var hasCorrelationId = false;
        await using (var reader = await tableInfo.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                hasCorrelationId |= string.Equals(reader.GetString(1), "correlation_id", StringComparison.OrdinalIgnoreCase);
        }

        if (hasCorrelationId)
            return;

        await using var migration = connection.CreateCommand();
        migration.CommandText = "ALTER TABLE conversation_audit ADD COLUMN correlation_id TEXT";
        try
        {
            await migration.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Another process completed the additive migration after the schema probe.
        }
    }

    /// <inheritdoc />
    public async Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.touch", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            var updatedAt = DateTimeOffset.UtcNow;
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversations
                SET updated_at = $updatedAt
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", conversationId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Keep the in-memory cache consistent so subsequent GetAsync / ListAsync
            // calls return the updated timestamp without a disk round-trip.
            if (_cache.TryGet(conversationId.Value, out var cached))
            {
                var touched = CloneConversation(cached);
                touched.UpdatedAt = updatedAt;
                _cache.Set(conversationId.Value, touched);
            }
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.pin", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);
        activity?.SetTag("botnexus.conversation.pin", pin);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = await AcquireConversationLockAsync(conversationId.Value, ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var pinnedAt = pin ? now : (DateTimeOffset?)null;

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversations
                SET is_pinned = $pin,
                    pinned_at = $pinnedAt,
                    updated_at = $now
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$pin", pin ? 1 : 0);
            command.Parameters.AddWithValue("$pinnedAt", pinnedAt.HasValue ? (object)pinnedAt.Value.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", conversationId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (_cache.TryGet(conversationId.Value, out var cached))
            {
                var updated = CloneConversation(cached);
                updated.IsPinned = pin;
                updated.PinnedAt = pinnedAt;
                updated.UpdatedAt = now;
                _cache.Set(conversationId.Value, updated);
            }
        }
        finally
        {
            conversationLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.resolve_by_binding", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", agentId.Value);
        activity?.SetTag("botnexus.channel.type", channelType.Value);
        activity?.SetTag("botnexus.channel.address", channelAddress.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Match binding by (channel_type, channel_address). Native sub-addresses (e.g.
        // Telegram forum topics) are encoded into channel_address by the adapter, so the
        // store treats the address as opaque.
        command.CommandText = """
                SELECT c.id
                FROM conversations c
                INNER JOIN conversation_bindings b ON b.conversation_id = c.id
                WHERE c.agent_id = $agentId
                  AND c.status = $status
                  AND b.channel_type = $channelType
                  AND lower(b.channel_address) = lower($channelAddress)
                ORDER BY c.updated_at DESC
                LIMIT 1
                """;
        command.Parameters.AddWithValue("$agentId", agentId.Value);
        command.Parameters.AddWithValue("$status", ConversationStatus.Active.ToString());
        command.Parameters.AddWithValue("$channelType", channelType.Value);
        command.Parameters.AddWithValue("$channelAddress", channelAddress.Value);
        var id = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(id)
            ? null
            : await GetAsync(ConversationId.From(id), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.get_summaries", ActivityKind.Internal);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.id,
                c.agent_id,
                c.title,
                c.purpose,
                c.is_default,
                c.status,
                c.active_session_id,
                c.created_at,
                c.updated_at,
                COUNT(b.binding_id) AS binding_count,
                c.instructions,
                c.kind,
                c.is_pinned,
                c.pinned_at
            FROM conversations c
            LEFT JOIN conversation_bindings b ON b.conversation_id = c.id
            WHERE c.status = 'Active'
            GROUP BY c.id, c.agent_id, c.title, c.purpose, c.is_default, c.status, c.active_session_id, c.created_at, c.updated_at, c.instructions, c.kind, c.is_pinned, c.pinned_at
            ORDER BY c.is_pinned DESC, c.pinned_at DESC, c.updated_at DESC
            """;

        var summaries = new List<ConversationSummary>();
        var rosters = await LoadActiveParticipantRostersAsync(connection, ct).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var conversationId = reader.GetString(0);
            // #1627: the summary row ordinals live in ConversationRowMapper.MapSummary - the
            // single source of truth shared with the full-conversation mapper. The roster is
            // resolved once via the batch query above (#1427) to avoid an N+1 participant lookup.
            summaries.Add(ConversationRowMapper.MapSummary(
                reader,
                rosters.TryGetValue(conversationId, out var roster) ? roster : []));
        }

        return summaries;
    }

    /// <summary>
    /// Loads the participant rosters for every active conversation in a single query so
    /// <see cref="GetSummariesAsync"/> can attach them without an N+1 per-conversation lookup.
    /// Returns a map of conversation id to its ordered <see cref="ParticipantSummary"/> list.
    /// </summary>
    private static async Task<Dictionary<string, IReadOnlyList<ParticipantSummary>>> LoadActiveParticipantRostersAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.conversation_id, p.citizen_kind, p.citizen_id, p.role
            FROM conversation_participants p
            INNER JOIN conversations c ON c.id = p.conversation_id
            WHERE c.status = 'Active'
            ORDER BY p.citizen_kind ASC, p.citizen_id ASC
            """;

        var rosters = new Dictionary<string, List<ParticipantSummary>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var conversationId = reader.GetString(0);
            if (!rosters.TryGetValue(conversationId, out var list))
            {
                list = [];
                rosters[conversationId] = list;
            }
            // #1627: kind/id/role ordinals (offset past the leading conversation_id) live in
            // ConversationRowMapper.MapParticipantSummary - one source of truth for this shape.
            list.Add(ConversationRowMapper.MapParticipantSummary(reader, offset: 1));
        }

        return rosters.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ParticipantSummary>)kvp.Value,
            StringComparer.Ordinal);
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    purpose TEXT,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Active',
                    active_session_id TEXT,
                    metadata TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    world_id TEXT NOT NULL DEFAULT '',
                    model_override TEXT,
                    thinking_override TEXT,
                    context_window_override INTEGER
                );

                CREATE TABLE IF NOT EXISTS conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                );

                CREATE TABLE IF NOT EXISTS conversation_participants (
                    conversation_id TEXT NOT NULL,
                    citizen_kind TEXT NOT NULL,
                    citizen_id TEXT NOT NULL,
                    role TEXT,
                    PRIMARY KEY (conversation_id, citizen_kind, citizen_id),
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                );

                CREATE INDEX IF NOT EXISTS idx_conversations_agent_id ON conversations(agent_id);
                CREATE INDEX IF NOT EXISTS idx_bindings_conversation_id ON conversation_bindings(conversation_id);
                CREATE INDEX IF NOT EXISTS idx_bindings_lookup ON conversation_bindings(channel_type, channel_address);
                CREATE INDEX IF NOT EXISTS idx_conversation_participants_citizen ON conversation_participants(citizen_kind, citizen_id);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await EnsureConversationColumnsAsync(connection, ct).ConfigureAwait(false);
            await EnsureCanvasStateTableAsync(connection, ct).ConfigureAwait(false);
            await MigrateThreadIdIntoChannelAddressAsync(connection, ct).ConfigureAwait(false);

            // One-time migration: archive stale signalr:connection-id conversations created
            // before binding-first routing (#148). Those conversations have a title matching
            // 'signalr:<32-hex-chars>' — a connection ID, not an agent ID.
            await using var archiveStaleMigration = connection.CreateCommand();
            archiveStaleMigration.CommandText = """
                UPDATE conversations
                SET status = 'Archived', updated_at = $now
                WHERE status = 'Active'
                  AND title LIKE 'signalr:%'
                  AND length(replace(substr(title, 9), '-', '')) = 32
                  AND substr(title, 9) NOT GLOB '*[^0-9a-fA-F-]*'
                """;
            archiveStaleMigration.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            var archived = await archiveStaleMigration.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (archived > 0)
                _logger.LogInformation("Archived {Count} stale signalr:connection-id conversations (pre-v0.1.3 cleanup)", archived);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task<IDisposable> AcquireConversationLockAsync(string conversationId, CancellationToken cancellationToken)
        => _conversationLocks.AcquireAsync(conversationId, cancellationToken);

    // Additive column migrations for the `conversations` table. Table-driven and race-tolerant:
    // each entry is applied with a plain ALTER TABLE ... ADD COLUMN, and the "duplicate column
    // name" SQLite error (generic error code 1) is swallowed. This gives every column the same
    // cross-process first-boot concurrency safety that only world_id previously had (#1885,
    // #1383 Finding 2): _initLock only serialises within one process, so when two gateway
    // instances open a fresh database concurrently the loser of the PRAGMA-then-ALTER race would
    // otherwise throw. Modelled on SqliteSessionStore.MigrateAsync, which is already array-driven.
    // Column set and DDL are identical to the prior hand-rolled Ensure*ColumnAsync methods; this
    // is a pure refactor plus adding the race tolerance to the seven columns that lacked it.
    private static async Task EnsureConversationColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var (column, ddl) in ConversationColumnMigrations)
            await EnsureColumnAsync(connection, column, ddl, ct).ConfigureAwait(false);

        // Indexes are cheap when already present and safe to run every boot. Kept out of the
        // column loop because they are not ADD COLUMN statements (initiator gained an index in
        // the pre-refactor EnsureInitiatorColumnAsync).
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_conversations_initiator ON conversations(initiator);";
        await indexCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Additive `conversations` columns in application order. The `kind` column maps NULL to
    // ConversationKind.HumanAgent on load; `world_id` is NOT NULL DEFAULT '' and lazy-backfilled
    // on read (#613); `initiator` also gains an index (see EnsureConversationColumnsAsync).
    private static readonly (string Column, string Ddl)[] ConversationColumnMigrations =
    {
        ("purpose", "ALTER TABLE conversations ADD COLUMN purpose TEXT;"),
        ("instructions", "ALTER TABLE conversations ADD COLUMN instructions TEXT;"),
        ("canvas_html", "ALTER TABLE conversations ADD COLUMN canvas_html TEXT;"),
        ("todo_json", "ALTER TABLE conversations ADD COLUMN todo_json TEXT;"),
        ("pending_ask_user_json", "ALTER TABLE conversations ADD COLUMN pending_ask_user_json TEXT;"),
        // #1706: per-conversation model / thinking / context overrides.
        ("model_override", "ALTER TABLE conversations ADD COLUMN model_override TEXT;"),
        ("thinking_override", "ALTER TABLE conversations ADD COLUMN thinking_override TEXT;"),
        ("context_window_override", "ALTER TABLE conversations ADD COLUMN context_window_override INTEGER;"),
        ("initiator", "ALTER TABLE conversations ADD COLUMN initiator TEXT;"),
        // Phase 4 / F-3 discriminator: NULL maps to ConversationKind.HumanAgent on load.
        ("kind", "ALTER TABLE conversations ADD COLUMN kind TEXT;"),
        // Phase 9 / P9-A world discriminator (#613): NOT NULL DEFAULT '' so legacy INSERTs stay
        // valid; empty values are lazy-backfilled to the current world id on read.
        ("world_id", "ALTER TABLE conversations ADD COLUMN world_id TEXT NOT NULL DEFAULT '';"),
        ("is_pinned", "ALTER TABLE conversations ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;"),
        ("pinned_at", "ALTER TABLE conversations ADD COLUMN pinned_at TEXT;")
    };

    // Race-tolerant single-column migration. Probes PRAGMA table_info first (cheap, avoids a
    // guaranteed exception on the common already-migrated path), then ALTERs. If a concurrent
    // process wins the race between the probe and the ALTER, SQLite returns generic error code 1
    // with "duplicate column name: <col>"; the column already exists with the schema we'd have
    // created, so we swallow and continue. Any other SqliteException propagates.
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string column,
        string alterDdl,
        CancellationToken ct)
    {
        await using (var tableInfoCommand = connection.CreateCommand())
        {
            tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";
            await using var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = alterDdl;
        try
        {
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                         ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cross-process first-boot race: another gateway instance ran EnsureCreatedAsync
            // between our PRAGMA-table_info read and this ALTER. _initLock only serialises within
            // one process. The column already exists with the schema we intended, so continue.
        }
    }

    // Migrates legacy conversation_bindings rows with a thread_id column into the composite
    // channel_address scheme adopted in PR #512 (refactor: drop ThreadId from core contracts).
    // Idempotent: if the thread_id column has already been dropped, the method is a no-op.
    // Strategy: detect the old column → BEGIN IMMEDIATE → drop the lookup index (which
    // references thread_id) → rewrite addresses with thread_id appended as "/topic:<value>" →
    // rebuild the table without the column (table-rebuild is more portable than DROP COLUMN) →
    // recreate the lookup index without thread_id → COMMIT → clear the cache (cached
    // ChannelBinding instances would otherwise still hold the deleted property).
    private async Task MigrateThreadIdIntoChannelAddressAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversation_bindings);";

        var hasThreadIdColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "thread_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasThreadIdColumn = true;
                    break;
                }
            }
        }

        if (!hasThreadIdColumn)
            return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Drop the lookup index first — it references thread_id and would block the table rebuild.
        await using (var dropIndex = connection.CreateCommand())
        {
            dropIndex.Transaction = transaction;
            dropIndex.CommandText = "DROP INDEX IF EXISTS idx_bindings_lookup;";
            await dropIndex.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Fold legacy thread_id values into channel_address using the composite encoding
        // (adapter-owned; the store treats it as opaque). Non-numeric thread_id values (legacy
        // REST seed rows accepted any string) are preserved verbatim — the originating adapter
        // is responsible for parsing them back, and a malformed value fails the adapter's
        // decode just like any other unrecognised address.
        await using (var rewrite = connection.CreateCommand())
        {
            rewrite.Transaction = transaction;
            rewrite.CommandText = """
                UPDATE conversation_bindings
                SET channel_address = channel_address || '/topic:' || thread_id
                WHERE thread_id IS NOT NULL;
                """;
            await rewrite.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Table rebuild — portable across all SQLite versions (ALTER TABLE DROP COLUMN is
        // only available on 3.35+).
        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                CREATE TABLE conversation_bindings_new (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                );

                INSERT INTO conversation_bindings_new
                    (binding_id, conversation_id, channel_type, channel_address, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at)
                SELECT binding_id, conversation_id, channel_type, channel_address, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at
                FROM conversation_bindings;

                DROP TABLE conversation_bindings;
                ALTER TABLE conversation_bindings_new RENAME TO conversation_bindings;

                CREATE INDEX IF NOT EXISTS idx_bindings_conversation_id ON conversation_bindings(conversation_id);
                CREATE INDEX IF NOT EXISTS idx_bindings_lookup ON conversation_bindings(channel_type, channel_address);
                """;
            await rebuild.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        // Invalidate cached ChannelBinding instances — they were loaded under the old schema.
        _cache.Clear();

        _logger.LogInformation(
            "Migrated conversation_bindings.thread_id column into composite channel_address (PR #512).");
    }

    /// <summary>
    /// Persistence form for <see cref="Conversation.Initiator"/>. <c>null</c> in returns <c>null</c>;
    /// a present-but-invalid <see cref="CitizenId"/> (i.e. <c>default(CitizenId)</c>) is a programming
    /// error and throws — the router contract requires either <c>null</c> or a well-formed citizen.
    /// </summary>
    internal static string? SerializeInitiator(CitizenId? initiator)
    {
        if (initiator is null)
            return null;
        if (!initiator.Value.IsValid)
            throw new InvalidOperationException("Conversation.Initiator must be null or a valid CitizenId; got default(CitizenId).");
        return initiator.Value.ToString();
    }

    private async Task<Conversation?> LoadConversationAsync(ConversationId conversationId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await LoadConversationAsync(connection, conversationId, ct, _logger).ConfigureAwait(false);
    }

    private static async Task<Conversation?> LoadConversationAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct, ILogger? logger = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at, todo_json, pending_ask_user_json, model_override, thinking_override, context_window_override
            FROM conversations
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", conversationId.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var conversation = ConversationRowMapper.MapConversation(reader, logger);

        await reader.DisposeAsync().ConfigureAwait(false);
        conversation.ChannelBindings = await LoadBindingsAsync(connection, conversation.ConversationId, ct).ConfigureAwait(false);
        conversation.Participants = await LoadParticipantsAsync(connection, conversation.ConversationId, logger, ct).ConfigureAwait(false);
        return conversation;
    }


    /// <summary>
    /// Resolves an ordered list of conversation ids to fully-hydrated <see cref="Conversation"/>
    /// objects, consulting the read-through cache first and batch-loading only the misses in a
    /// bounded number of queries (issue #1626). Preserves the supplied id order and applies
    /// <see cref="BackfillWorldId"/> to each result. This is the single seam shared by
    /// <see cref="ListAsync"/> and <see cref="ListForCitizenAsync"/> so the read-through-cache
    /// enumeration is not duplicated across them.
    /// </summary>
    private async Task<List<Conversation>> MaterializeOrderedAsync(
        SqliteConnection connection,
        IReadOnlyList<string> orderedIds,
        CancellationToken ct)
    {
        if (orderedIds.Count == 0)
            return [];

        // Split cache hits from misses up front; only the misses hit the database, and they do
        // so in one batched pass rather than a per-row LoadConversationAsync fan-out.
        //
        // The materialised result is assembled into a LOCAL map (`resolved`) that is independent of
        // the LRU cache's capacity — NOT re-read out of `_cache` after warming it (issue #2226). The
        // cache (BoundedLruCache, DefaultConversationCacheCapacity = 1000) is a warm read-through, not
        // a buffer sized to hold an entire result set. When `orderedIds.Count` exceeds the cache
        // capacity (or the LRU churns under concurrent reads), each `_cache.Set` for a later id can
        // evict an earlier id; a subsequent `_cache.TryGet(id)` rebuild loop then misses those
        // evicted ids and silently drops them, so conversations past the ~1000th position flickered
        // in and out of GET /api/conversations non-deterministically. Keeping the loaded rows in a
        // local map decouples result membership from cache survival while still warming the cache.
        var resolved = new Dictionary<string, Conversation>(orderedIds.Count, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in orderedIds)
        {
            if (_cache.TryGet(id, out var cached))
                resolved[id] = cached;
            else
                missing.Add(id);
        }

        if (missing.Count > 0)
        {
            var loaded = await LoadConversationsByIdsAsync(connection, missing, ct).ConfigureAwait(false);
            foreach (var (id, conversation) in loaded)
            {
                _cache.Set(id, CloneConversation(conversation));
                resolved[id] = conversation;
            }
        }

        // An id present in the ordered set can legitimately vanish between the id-select in the
        // caller (ListAsync / ListForCitizenAsync) and this hydrate pass: a concurrent deleter —
        // notably the noop cron-session prune (issue #1754) or any DeleteAsync racing a live portal
        // poller — can remove the row after we captured its id but before LoadConversationsByIdsAsync
        // ran. That row is simply absent from the batch load and never enters `resolved`. Treat it as
        // a concurrent delete and omit it from the list rather than throwing: a hard throw here turns
        // the whole GET /api/conversations request into a 500 (issue #1642 originally introduced the
        // throw). Omitting the deleted conversation is the correct read-side semantics for a delete
        // that committed mid-enumeration.
        var result = new List<Conversation>(orderedIds.Count);
        foreach (var id in orderedIds)
        {
            if (!resolved.TryGetValue(id, out var conversation))
                continue;
            result.Add(BackfillWorldId(CloneConversation(conversation))!);
        }

        return result;
    }

    /// <summary>
    /// Batch-loads conversations for the supplied ids in a fixed number of queries
    /// (issue #1626): one for the conversation rows, one for all their channel bindings, and one
    /// for all their participants — grouping the child collections in memory by conversation id.
    /// Replaces the previous <c>1 + 3N</c> per-row <see cref="LoadConversationAsync(SqliteConnection, ConversationId, CancellationToken)"/>
    /// fan-out used by the list endpoints. Rows are returned keyed by id; missing ids are simply
    /// absent from the result. Hydration is identical to the single-row path (shared mappers).
    /// </summary>
    private async Task<Dictionary<string, Conversation>> LoadConversationsByIdsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var byId = new Dictionary<string, Conversation>(ids.Count);
        if (ids.Count == 0)
            return byId;

        // 1) Conversation rows.
        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildIdInClause(command, ids);
            command.CommandText = $"""
                SELECT id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at, todo_json, pending_ask_user_json, model_override, thinking_override, context_window_override
                FROM conversations
                WHERE id IN ({inClause})
                """;
            Interlocked.Increment(ref _readRoundTrips);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var conversation = ConversationRowMapper.MapConversation(reader, _logger);
                conversation.ChannelBindings = [];
                conversation.Participants = [];
                byId[conversation.ConversationId.Value] = conversation;
            }
        }

        if (byId.Count == 0)
            return byId;

        var presentIds = byId.Keys.ToList();

        // 2) All bindings for the present conversations, accumulated into per-conversation lists.
        //    The lists are built locally and assigned wholesale below; the entity's `.ChannelBindings`
        //    list is never mutated in place (mirrors the P9-F wholesale-assignment idiom that the
        //    per-conversation loader uses via LoadBindingsAsync -> `conversation.ChannelBindings = ...`).
        var bindingsById = new Dictionary<string, List<ChannelBinding>>(byId.Count);
        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildIdInClause(command, presentIds);
            command.CommandText = $"""
                SELECT conversation_id, binding_id, channel_type, channel_address, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at
                FROM conversation_bindings
                WHERE conversation_id IN ({inClause})
                ORDER BY conversation_id ASC, binding_id ASC
                """;
            Interlocked.Increment(ref _readRoundTrips);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var conversationId = reader.GetString(0);
                if (!byId.ContainsKey(conversationId))
                    continue;
                if (!bindingsById.TryGetValue(conversationId, out var list))
                {
                    list = [];
                    bindingsById[conversationId] = list;
                }
                list.Add(ConversationRowMapper.MapBinding(reader, offset: 1));
            }
        }

        // 3) All participants for the present conversations, accumulated into per-conversation lists.
        //    Same wholesale-assignment discipline as bindings above: build locally, assign once.
        //    Going through `conversation.Participants.Add(...)` would violate the P9-F atomic-merge
        //    contract (enforced by ConversationParticipantsMutationArchitectureTests); the
        //    single-conversation loader assigns the result of LoadParticipantsAsync wholesale.
        var participantsById = new Dictionary<string, List<SessionParticipant>>(byId.Count);
        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildIdInClause(command, presentIds);
            command.CommandText = $"""
                SELECT conversation_id, citizen_kind, citizen_id, role
                FROM conversation_participants
                WHERE conversation_id IN ({inClause})
                ORDER BY conversation_id ASC, citizen_kind ASC, citizen_id ASC
                """;
            Interlocked.Increment(ref _readRoundTrips);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var conversationId = reader.GetString(0);
                if (!byId.ContainsKey(conversationId)
                    || ConversationRowMapper.MapParticipant(reader, offset: 1, _logger) is not { } participant)
                {
                    continue;
                }
                if (!participantsById.TryGetValue(conversationId, out var list))
                {
                    list = [];
                    participantsById[conversationId] = list;
                }
                list.Add(participant);
            }
        }

        // 4) Assign the grouped child collections wholesale. Conversations with no bindings /
        //    participants keep the empty list assigned at materialisation (step 1).
        foreach (var (conversationId, conversation) in byId)
        {
            if (bindingsById.TryGetValue(conversationId, out var bindings))
                conversation.ChannelBindings = bindings;
            if (participantsById.TryGetValue(conversationId, out var participants))
                conversation.Participants = participants;
        }

        return byId;
    }

    /// <summary>
    /// Builds a parameterised <c>IN (...)</c> placeholder list for the supplied ids and binds each
    /// value on <paramref name="command"/>. Parameterised (never interpolated) so ids are never
    /// concatenated into SQL. Callers must ensure <paramref name="ids"/> is non-empty.
    /// </summary>
    private static string BuildIdInClause(SqliteCommand command, IReadOnlyList<string> ids)
    {
        var placeholders = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"$id{i}";
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, ids[i]);
        }

        return string.Join(", ", placeholders);
    }

    private static async Task<List<SessionParticipant>> LoadParticipantsAsync(SqliteConnection connection, ConversationId conversationId, ILogger? logger, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT citizen_kind, citizen_id, role
            FROM conversation_participants
            WHERE conversation_id = $conversationId
            ORDER BY citizen_kind ASC, citizen_id ASC
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.Value);

        var participants = new List<SessionParticipant>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (ConversationRowMapper.MapParticipant(reader, offset: 0, logger) is { } participant)
                participants.Add(participant);
        }

        return participants;
    }


    private static async Task<List<ChannelBinding>> LoadBindingsAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT binding_id, channel_type, channel_address, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at
            FROM conversation_bindings
            WHERE conversation_id = $conversationId
            ORDER BY binding_id ASC
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.Value);

        var bindings = new List<ChannelBinding>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            bindings.Add(ConversationRowMapper.MapBinding(reader, offset: 0));
        }

        return bindings;
    }


    private static async Task SaveConversationAsync(SqliteConnection connection, Conversation conversation, bool upsert, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var conversationCommand = connection.CreateCommand();
        conversationCommand.Transaction = transaction;
        conversationCommand.CommandText = upsert
            ? """
                INSERT INTO conversations (id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at, todo_json, pending_ask_user_json, model_override, thinking_override, context_window_override)
                VALUES ($id, $agentId, $title, $purpose, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt, $instructions, $canvasHtml, $initiator, $kind, $worldId, $isPinned, $pinnedAt, $todoJson, $pendingAskUserJson, $modelOverride, $thinkingOverride, $contextWindowOverride)
                ON CONFLICT(id) DO UPDATE SET
                    agent_id = excluded.agent_id,
                    title = excluded.title,
                    purpose = excluded.purpose,
                    is_default = excluded.is_default,
                    status = excluded.status,
                    active_session_id = excluded.active_session_id,
                    metadata = excluded.metadata,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at,
                    instructions = excluded.instructions,
                    canvas_html = excluded.canvas_html,
                    initiator = excluded.initiator,
                    kind = excluded.kind,
                    world_id = excluded.world_id,
                    is_pinned = excluded.is_pinned,
                    pinned_at = excluded.pinned_at,
                    todo_json = excluded.todo_json,
                    pending_ask_user_json = excluded.pending_ask_user_json,
                    model_override = excluded.model_override,
                    thinking_override = excluded.thinking_override,
                    context_window_override = excluded.context_window_override
                """
            : """
                INSERT INTO conversations (id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at, todo_json, pending_ask_user_json, model_override, thinking_override, context_window_override)
                VALUES ($id, $agentId, $title, $purpose, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt, $instructions, $canvasHtml, $initiator, $kind, $worldId, $isPinned, $pinnedAt, $todoJson, $pendingAskUserJson, $modelOverride, $thinkingOverride, $contextWindowOverride)
                """;
        conversationCommand.Parameters.AddWithValue("$id", conversation.ConversationId.Value);
        conversationCommand.Parameters.AddWithValue("$agentId", conversation.AgentId.Value);
        conversationCommand.Parameters.AddWithValue("$title", conversation.Title);
        conversationCommand.Parameters.AddWithValue("$purpose", conversation.Purpose is null or { Length: 0 } ? DBNull.Value : conversation.Purpose);
        conversationCommand.Parameters.AddWithValue("$isDefault", conversation.IsDefault ? 1 : 0);
        conversationCommand.Parameters.AddWithValue("$status", conversation.Status.ToString());
        conversationCommand.Parameters.AddWithValue("$activeSessionId", conversation.ActiveSessionId is null ? DBNull.Value : conversation.ActiveSessionId.Value.Value);
        conversationCommand.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(conversation.Metadata, JsonOptions));
        conversationCommand.Parameters.AddWithValue("$createdAt", conversation.CreatedAt.ToString("O"));
        conversationCommand.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAt.ToString("O"));
        conversationCommand.Parameters.AddWithValue("$instructions", conversation.Instructions is null ? (object)DBNull.Value : conversation.Instructions);
        conversationCommand.Parameters.AddWithValue("$canvasHtml", conversation.CanvasHtml is null ? (object)DBNull.Value : conversation.CanvasHtml);
        conversationCommand.Parameters.AddWithValue("$initiator", (object?)SerializeInitiator(conversation.Initiator) ?? DBNull.Value);
        conversationCommand.Parameters.AddWithValue("$kind", conversation.Kind.ToString());
        conversationCommand.Parameters.AddWithValue("$worldId", conversation.WorldId ?? string.Empty);
        conversationCommand.Parameters.AddWithValue("$isPinned", conversation.IsPinned ? 1 : 0);
        conversationCommand.Parameters.AddWithValue("$pinnedAt", conversation.PinnedAt.HasValue ? (object)conversation.PinnedAt.Value.ToString("O") : DBNull.Value);
        conversationCommand.Parameters.AddWithValue("$todoJson", conversation.TodoJson is null ? (object)DBNull.Value : conversation.TodoJson);
        conversationCommand.Parameters.AddWithValue("$pendingAskUserJson", conversation.PendingAskUserJson is null ? (object)DBNull.Value : conversation.PendingAskUserJson);
        conversationCommand.Parameters.AddWithValue("$modelOverride", conversation.ModelOverride is null ? (object)DBNull.Value : conversation.ModelOverride);
        conversationCommand.Parameters.AddWithValue("$thinkingOverride", conversation.ThinkingOverride is null ? (object)DBNull.Value : conversation.ThinkingOverride);
        conversationCommand.Parameters.AddWithValue("$contextWindowOverride", conversation.ContextWindowOverride.HasValue ? conversation.ContextWindowOverride.Value : (object)DBNull.Value);
        await conversationCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var deleteBindingsCommand = connection.CreateCommand();
        deleteBindingsCommand.Transaction = transaction;
        deleteBindingsCommand.CommandText = "DELETE FROM conversation_bindings WHERE conversation_id = $conversationId";
        deleteBindingsCommand.Parameters.AddWithValue("$conversationId", conversation.ConversationId.Value);
        await deleteBindingsCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        foreach (var binding in conversation.ChannelBindings)
        {
            await using var bindingCommand = connection.CreateCommand();
            bindingCommand.Transaction = transaction;
            bindingCommand.CommandText = """
                INSERT INTO conversation_bindings (binding_id, conversation_id, channel_type, channel_address, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at)
                VALUES ($bindingId, $conversationId, $channelType, $channelAddress, $mode, $threadingMode, $displayPrefix, $boundAt, $lastInboundAt, $lastOutboundAt)
                """;
            bindingCommand.Parameters.AddWithValue("$bindingId", binding.BindingId.Value);
            bindingCommand.Parameters.AddWithValue("$conversationId", conversation.ConversationId.Value);
            bindingCommand.Parameters.AddWithValue("$channelType", binding.ChannelType.Value);
            bindingCommand.Parameters.AddWithValue("$channelAddress", binding.ChannelAddress.Value);
            bindingCommand.Parameters.AddWithValue("$mode", binding.Mode.ToString());
            bindingCommand.Parameters.AddWithValue("$threadingMode", binding.ThreadingMode.ToString());
            bindingCommand.Parameters.AddWithValue("$displayPrefix", (object?)binding.DisplayPrefix ?? DBNull.Value);
            bindingCommand.Parameters.AddWithValue("$boundAt", binding.BoundAt.ToString("O"));
            bindingCommand.Parameters.AddWithValue("$lastInboundAt", binding.LastInboundAt?.ToString("O") ?? (object)DBNull.Value);
            bindingCommand.Parameters.AddWithValue("$lastOutboundAt", binding.LastOutboundAt?.ToString("O") ?? (object)DBNull.Value);
            await bindingCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    private static Conversation CloneConversation(Conversation conversation)
        => new()
        {
            ConversationId = conversation.ConversationId,
            AgentId = conversation.AgentId,
            Title = conversation.Title,
            Purpose = conversation.Purpose,
            Instructions = conversation.Instructions,
            CanvasHtml = conversation.CanvasHtml,
            TodoJson = conversation.TodoJson,
            PendingAskUserJson = conversation.PendingAskUserJson,
            Initiator = conversation.Initiator,
            Kind = conversation.Kind,
            WorldId = conversation.WorldId,
            IsPinned = conversation.IsPinned,
            PinnedAt = conversation.PinnedAt,
            IsDefault = conversation.IsDefault,
            Status = conversation.Status,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            ActiveSessionId = conversation.ActiveSessionId,
            Metadata = CloneMetadata(conversation.Metadata),
            ChannelBindings = conversation.ChannelBindings.Select(binding => new ChannelBinding
            {
                BindingId = binding.BindingId,
                ChannelType = binding.ChannelType,
                ChannelAddress = binding.ChannelAddress,
                Mode = binding.Mode,
                ThreadingMode = binding.ThreadingMode,
                DisplayPrefix = binding.DisplayPrefix,
                BoundAt = binding.BoundAt,
                LastInboundAt = binding.LastInboundAt,
                LastOutboundAt = binding.LastOutboundAt
            }).ToList(),
            // Participants are conversation state but are mutated exclusively through
            // AddParticipantsAsync — CloneConversation keeps the snapshot consistent so
            // callers reading a clone see the loaded list, but SaveAsync intentionally does
            // not write Participants back to disk (the conversation_participants table is the
            // authoritative store for them).
            Participants = conversation.Participants
                .Select(p => new SessionParticipant { CitizenId = p.CitizenId, Role = p.Role })
                .ToList()
        };

    // #1751: defensive guard for the deep-clone metadata round-trip. A round-trip of a well-formed
    // in-memory dictionary does not normally throw, but if a value cannot be re-materialised into a
    // Dictionary<string, object?> we fall back to an empty dictionary rather than letting the clone
    // (and therefore every list/cache-seed path that clones) abort. This method is static and has no
    // instance logger; the row is degraded silently to a safe default which is acceptable because the
    // authoritative on-disk read path (ConversationRowMapper.DeserializeMetadata) already logs.
    private static Dictionary<string, object?> CloneMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(metadata, JsonOptions), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── Canvas State ───────────────────────────────────────────────────────

    private static async Task EnsureCanvasStateTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS canvas_state (
                conversation_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (conversation_id, key),
                FOREIGN KEY (conversation_id) REFERENCES conversations(id)
            );
            CREATE INDEX IF NOT EXISTS idx_canvas_state_conversation ON canvas_state(conversation_id);
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Existence check is a cheap single-row probe, not a full conversation
        // hydrate. The previous GetAsync guard fanned out into 3 queries
        // (row + participants + bindings) and materialised a Conversation object
        // purely to answer "does it exist?" on a hot canvas-render path (#1387).
        if (!await ConversationExistsAsync(connection, conversationId, ct).ConfigureAwait(false))
            return null;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM canvas_state WHERE conversation_id = $id";
        command.Parameters.AddWithValue("$id", conversationId.Value);

        var state = new Dictionary<string, JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var valueJson = reader.GetString(1);
            state[key] = JsonDocument.Parse(valueJson).RootElement.Clone();
        }

        return state;
    }

    /// <inheritdoc />
    public async Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Cheap existence probe instead of a full GetAsync hydrate (#1387).
        if (!await ConversationExistsAsync(connection, conversationId, ct).ConfigureAwait(false))
            return false;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO canvas_state (conversation_id, key, value)
            VALUES ($id, $key, $value)
            ON CONFLICT (conversation_id, key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$id", conversationId.Value);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Cheap existence probe used by the canvas-state methods. Issues a single
    /// <c>SELECT 1 ... LIMIT 1</c> against the conversations table instead of
    /// the full <see cref="GetAsync"/> hydrate (which fans out into row +
    /// participants + bindings queries). Used purely to gate canvas reads/writes
    /// with the same null/false semantics as the previous guard.
    /// </summary>
    private static async Task<bool> ConversationExistsAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct)
    {
        await using var existsCmd = connection.CreateCommand();
        existsCmd.CommandText = "SELECT 1 FROM conversations WHERE id = $id LIMIT 1";
        existsCmd.Parameters.AddWithValue("$id", conversationId.Value);
        return await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM canvas_state WHERE conversation_id = $id AND key = $key";
        command.Parameters.AddWithValue("$id", conversationId.Value);
        command.Parameters.AddWithValue("$key", key);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM canvas_state WHERE conversation_id = $id";
        command.Parameters.AddWithValue("$id", conversationId.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private static void ValidateLifecycleState(Conversation conversation)
    {
        if (conversation.Status == ConversationStatus.Archived && conversation.ActiveSessionId is not null)
        {
            throw new InvalidOperationException(
                $"Conversation '{conversation.ConversationId}' cannot be archived while an active session is assigned.");
        }
    }
}
