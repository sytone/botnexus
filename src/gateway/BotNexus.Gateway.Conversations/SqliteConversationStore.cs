using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteConversationStore> _logger;
    private readonly IWorldContext? _worldContext;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Conversation> _cache = new(StringComparer.Ordinal);
    private bool _initialized;

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
    public SqliteConversationStore(string connectionString, ILogger<SqliteConversationStore> logger, IWorldContext? worldContext)
    {
        _connectionString = connectionString;
        _logger = logger;
        _worldContext = worldContext;
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
        var conversationLock = GetConversationLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(conversationId.Value, out var cached))
                return BackfillWorldId(CloneConversation(cached));

            var loaded = await LoadConversationAsync(conversationId, ct).ConfigureAwait(false);
            if (loaded is not null)
                _cache[conversationId.Value] = CloneConversation(loaded);

            return BackfillWorldId(loaded);
        }
        finally
        {
            conversationLock.Release();
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

        var conversations = new List<Conversation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            Conversation conversation;
            if (_cache.TryGetValue(id, out var cached))
            {
                conversation = CloneConversation(cached);
            }
            else
            {
                conversation = await LoadConversationAsync(connection, ConversationId.From(id), ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Conversation '{id}' disappeared during enumeration.");
                _cache[id] = CloneConversation(conversation);
            }

            conversations.Add(BackfillWorldId(conversation)!);
        }

        return conversations;
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
        // the conversation_participants set. The DISTINCT collapses duplicates when a citizen
        // matches under more than one criterion (e.g. an agent that owns + participates).
        command.CommandText = """
            SELECT DISTINCT c.id
            FROM conversations c
            LEFT JOIN conversation_participants p ON p.conversation_id = c.id
            WHERE c.initiator = $initiator
               OR ($isAgent = 1 AND c.agent_id = $agentMatch)
               OR (p.citizen_kind = $citizenKind AND p.citizen_id = $citizenIdValue)
            ORDER BY c.updated_at DESC
            """;
        command.Parameters.AddWithValue("$initiator", citizen.ToString());
        command.Parameters.AddWithValue("$isAgent", citizen.Kind == CitizenKind.Agent ? 1 : 0);
        command.Parameters.AddWithValue("$agentMatch",
            citizen.Kind == CitizenKind.Agent ? (object)citizen.AsAgent!.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("$citizenKind", citizen.Kind.ToString());
        command.Parameters.AddWithValue("$citizenIdValue", citizen.Value);

        var conversations = new List<Conversation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            Conversation conversation;
            if (_cache.TryGetValue(id, out var cached))
            {
                conversation = CloneConversation(cached);
            }
            else
            {
                conversation = await LoadConversationAsync(connection, ConversationId.From(id), ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Conversation '{id}' disappeared during enumeration.");
                _cache[id] = CloneConversation(conversation);
            }

            conversations.Add(BackfillWorldId(conversation)!);
        }

        return conversations;
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
        var conversationLock = GetConversationLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            foreach (var participant in snapshot)
            {
                if (!participant.CitizenId.IsValid)
                    continue;

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
                insertCommand.Parameters.AddWithValue("$citizenKind", participant.CitizenId.Kind.ToString());
                insertCommand.Parameters.AddWithValue("$citizenId", participant.CitizenId.Value);
                insertCommand.Parameters.AddWithValue("$role", (object?)participant.Role ?? DBNull.Value);
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            // Invalidate cache entry — the next read will repopulate Participants via the
            // LoadParticipantsAsync join. Cheaper than mutating the cached list in place.
            _cache.TryRemove(conversationId.Value, out _);
        }
        finally
        {
            conversationLock.Release();
        }
    }

    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.create", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversation.ConversationId.Value);
        activity?.SetTag("botnexus.agent.id", conversation.AgentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = GetConversationLock(conversation.ConversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.ContainsKey(conversation.ConversationId.Value))
                throw new InvalidOperationException($"A conversation with id '{conversation.ConversationId}' already exists.");

            StampWorldId(conversation);
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SaveConversationAsync(connection, conversation, upsert: false, ct).ConfigureAwait(false);
            _cache[conversation.ConversationId.Value] = CloneConversation(conversation);
            return CloneConversation(conversation);
        }
        finally
        {
            conversationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.save", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversation.ConversationId.Value);
        activity?.SetTag("botnexus.agent.id", conversation.AgentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = GetConversationLock(conversation.ConversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = CloneConversation(conversation);
            updated.UpdatedAt = DateTimeOffset.UtcNow;
            StampWorldId(updated);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SaveConversationAsync(connection, updated, upsert: true, ct).ConfigureAwait(false);
            _cache[updated.ConversationId.Value] = CloneConversation(updated);
        }
        finally
        {
            conversationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.archive", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = GetConversationLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
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

            if (_cache.TryGetValue(conversationId.Value, out var cached))
            {
                var archived = CloneConversation(cached);
                archived.Status = ConversationStatus.Archived;
                archived.ActiveSessionId = null;
                archived.UpdatedAt = updatedAt;
                _cache[conversationId.Value] = archived;
            }
        }
        finally
        {
            conversationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.touch", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = GetConversationLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
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
            if (_cache.TryGetValue(conversationId.Value, out var cached))
            {
                var touched = CloneConversation(cached);
                touched.UpdatedAt = updatedAt;
                _cache[conversationId.Value] = touched;
            }
        }
        finally
        {
            conversationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.pin", ActivityKind.Internal);
        activity?.SetTag("botnexus.conversation.id", conversationId.Value);
        activity?.SetTag("botnexus.conversation.pin", pin);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var conversationLock = GetConversationLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
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

            if (_cache.TryGetValue(conversationId.Value, out var cached))
            {
                var updated = CloneConversation(cached);
                updated.IsPinned = pin;
                updated.PinnedAt = pinnedAt;
                updated.UpdatedAt = now;
                _cache[conversationId.Value] = updated;
            }
        }
        finally
        {
            conversationLock.Release();
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
                COUNT(b.binding_id),
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
            summaries.Add(new ConversationSummary(
                conversationId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(4) != 0,
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                checked((int)reader.GetInt64(9)),
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.FieldCount > 11 && !reader.IsDBNull(11)
                    ? reader.GetString(11)
                    : ConversationKind.HumanAgent.ToString(),
                reader.FieldCount > 12 && !reader.IsDBNull(12) && reader.GetInt64(12) != 0,
                reader.FieldCount > 13 && !reader.IsDBNull(13) ? ParseTimestamp(reader.GetString(13)) : null,
                // #1427: attach the participant roster so SQLite-backed listings match the
                // InMemory reference shape. Resolved once via a single batch query above to
                // avoid an N+1 per-conversation participant lookup.
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
            var kind = reader.GetString(1);
            var id = reader.GetString(2);
            var role = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (!rosters.TryGetValue(conversationId, out var list))
            {
                list = [];
                rosters[conversationId] = list;
            }
            list.Add(new ParticipantSummary(kind, id, role));
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
                    world_id TEXT NOT NULL DEFAULT ''
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

            await EnsurePurposeColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsureInstructionsColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsureCanvasHtmlColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsureInitiatorColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsureKindColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsureWorldIdColumnAsync(connection, ct).ConfigureAwait(false);
            await EnsurePinColumnsAsync(connection, ct).ConfigureAwait(false);
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

    private SemaphoreSlim GetConversationLock(string conversationId)
        => _conversationLocks.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));

    private static async Task EnsurePurposeColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasPurposeColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "purpose", StringComparison.OrdinalIgnoreCase))
                {
                    hasPurposeColumn = true;
                    break;
                }
            }
        }

        if (hasPurposeColumn)
            return;

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN purpose TEXT;";
        await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureInstructionsColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "instructions", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN instructions TEXT;";
        await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureCanvasHtmlColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand2 = connection.CreateCommand();
        tableInfoCommand2.CommandText = "PRAGMA table_info(conversations);";

        var hasCanvasColumn = false;
        await using (var reader2 = await tableInfoCommand2.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader2.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader2.GetString(1), "canvas_html", StringComparison.OrdinalIgnoreCase))
                {
                    hasCanvasColumn = true;
                    break;
                }
            }
        }

        if (hasCanvasColumn)
            return;

        await using var canvasAlterCommand = connection.CreateCommand();
        canvasAlterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN canvas_html TEXT;";
        await canvasAlterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureInitiatorColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "initiator", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN initiator TEXT;";
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Always ensure the index exists (cheap when present).
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_conversations_initiator ON conversations(initiator);";
        await indexCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Adds the `kind` column to existing databases for the Phase 4 / F-3 discriminator.
    // Pre-Phase-4 rows have NULL here; the loader maps NULL to ConversationKind.HumanAgent
    // (the historical default), so back-compat is preserved automatically.
    private static async Task EnsureKindColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "kind", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN kind TEXT;";
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // Adds the `world_id` column to existing databases for the Phase 9 / P9-A discriminator
    // (issue #613). Pre-Phase-9 rows have empty-string here; the loader lazy-backfills empty
    // values to the current world id via BackfillWorldId on read, so back-compat is automatic.
    // The NOT NULL DEFAULT '' constraint keeps the column safe to leave unbound in legacy
    // INSERT statements that haven't yet been migrated.
    private static async Task EnsureWorldIdColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasColumn = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "world_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN world_id TEXT NOT NULL DEFAULT '';";
        try
        {
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                         ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cross-process race: another gateway instance hit EnsureCreatedAsync between our
            // PRAGMA-table_info read and our ALTER TABLE. _initLock only serialises within one
            // process; the loser of the race observes the column missing, races to ALTER, and
            // gets back SQLite generic error 1 with "duplicate column name: world_id". The
            // column already exists with the schema we'd have created (NOT NULL DEFAULT ''),
            // so swallow and continue. Same shape used by EnsureInitiatorColumnAsync should it
            // ever hit the same race — kept inline (not extracted to a helper) because the
            // duplicate-column tolerance is the only thing different from a plain ALTER.
        }
    }

    private static async Task EnsurePinColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = "PRAGMA table_info(conversations);";

        var hasIsPinned = false;
        var hasPinnedAt = false;
        await using (var reader = await tableInfoCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(1);
                if (string.Equals(name, "is_pinned", StringComparison.OrdinalIgnoreCase))
                    hasIsPinned = true;
                else if (string.Equals(name, "pinned_at", StringComparison.OrdinalIgnoreCase))
                    hasPinnedAt = true;
            }
        }

        if (!hasIsPinned)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0";
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!hasPinnedAt)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE conversations ADD COLUMN pinned_at TEXT";
            await alterCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static CitizenId? DeserializeInitiator(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return CitizenId.TryParse(raw, out var citizen) ? citizen : null;
    }

    private async Task<Conversation?> LoadConversationAsync(ConversationId conversationId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await LoadConversationAsync(connection, conversationId, ct).ConfigureAwait(false);
    }

    private static async Task<Conversation?> LoadConversationAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at
            FROM conversations
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", conversationId.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From(reader.GetString(0)),
            AgentId = AgentId.From(reader.GetString(1)),
            Title = reader.GetString(2),
            Purpose = reader.IsDBNull(3) ? null : reader.GetString(3),
            IsDefault = reader.GetInt64(4) != 0,
            Status = ParseConversationStatus(reader.GetString(5)),
            Metadata = DeserializeMetadata(reader.IsDBNull(7) ? null : reader.GetString(7)),
            CreatedAt = ParseTimestamp(reader.GetString(8)),
            UpdatedAt = ParseTimestamp(reader.GetString(9)),
            Instructions = reader.IsDBNull(10) ? null : reader.GetString(10),
            CanvasHtml = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
            Initiator = reader.FieldCount > 12 ? DeserializeInitiator(reader.IsDBNull(12) ? null : reader.GetString(12)) : null,
            Kind = reader.FieldCount > 13 ? ParseConversationKind(reader.IsDBNull(13) ? null : reader.GetString(13)) : ConversationKind.HumanAgent,
            WorldId = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : string.Empty,
            IsPinned = reader.FieldCount > 15 && !reader.IsDBNull(15) && reader.GetInt64(15) != 0,
            PinnedAt = reader.FieldCount > 16 && !reader.IsDBNull(16) ? ParseTimestamp(reader.GetString(16)) : null
        };
        if (!reader.IsDBNull(6))
            conversation.ActiveSessionId = SessionId.From(reader.GetString(6));

        await reader.DisposeAsync().ConfigureAwait(false);
        conversation.ChannelBindings = await LoadBindingsAsync(connection, conversation.ConversationId, ct).ConfigureAwait(false);
        conversation.Participants = await LoadParticipantsAsync(connection, conversation.ConversationId, ct).ConfigureAwait(false);
        return conversation;
    }

    private static async Task<List<SessionParticipant>> LoadParticipantsAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct)
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
            var kindRaw = reader.GetString(0);
            var idValue = reader.GetString(1);
            var role = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!TryComposeCitizen(kindRaw, idValue, out var citizen))
                continue;
            participants.Add(new SessionParticipant
            {
                CitizenId = citizen,
                Role = role
            });
        }

        return participants;
    }

    private static bool TryComposeCitizen(string kindRaw, string idValue, out CitizenId citizen)
    {
        citizen = default;
        if (!Enum.TryParse<CitizenKind>(kindRaw, ignoreCase: true, out var kind))
            return false;
        switch (kind)
        {
            case CitizenKind.User:
                citizen = CitizenId.Of(UserId.From(idValue));
                return true;
            case CitizenKind.Agent:
                citizen = CitizenId.Of(AgentId.From(idValue));
                return true;
            default:
                return false;
        }
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
            bindings.Add(new ChannelBinding
            {
                BindingId = BindingId.From(reader.GetString(0)),
                ChannelType = ChannelKey.From(reader.GetString(1)),
                ChannelAddress = ChannelAddress.From(reader.GetString(2)),
                Mode = ParseBindingMode(reader.GetString(3)),
                ThreadingMode = ParseThreadingMode(reader.GetString(4)),
                DisplayPrefix = reader.IsDBNull(5) ? null : reader.GetString(5),
                BoundAt = ParseTimestamp(reader.GetString(6)),
                LastInboundAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
                LastOutboundAt = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8))
            });
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
                INSERT INTO conversations (id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at)
                VALUES ($id, $agentId, $title, $purpose, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt, $instructions, $canvasHtml, $initiator, $kind, $worldId, $isPinned, $pinnedAt)
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
                    pinned_at = excluded.pinned_at
                """
            : """
                INSERT INTO conversations (id, agent_id, title, purpose, is_default, status, active_session_id, metadata, created_at, updated_at, instructions, canvas_html, initiator, kind, world_id, is_pinned, pinned_at)
                VALUES ($id, $agentId, $title, $purpose, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt, $instructions, $canvasHtml, $initiator, $kind, $worldId, $isPinned, $pinnedAt)
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
    {
        var connection = new SqliteConnection(_connectionString);
        // busy_timeout is per-connection and resets to 0 on every open, so it must be applied on
        // EVERY connection (this store opens a fresh connection per operation) — not just at init
        // like the database-level journal_mode=WAL. Without it a concurrent cross-process writer
        // hits SQLITE_BUSY immediately instead of waiting briefly for the lock to clear (#1450).
        // The handler fires on OpenAsync (before the init WAL pragma runs), so it also covers the
        // initialization connection. SqliteConversationStore was the one store #1451 could not
        // include because this file was locked at the time; this closes that gap with the same
        // pattern the other fresh-connection stores use.
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Open)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
        };

        return connection;
    }

    private static DateTimeOffset ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static ConversationStatus ParseConversationStatus(string? value)
        => Enum.TryParse<ConversationStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationStatus.Active;

    private static ConversationKind ParseConversationKind(string? value)
        => Enum.TryParse<ConversationKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationKind.HumanAgent;

    private static BindingMode ParseBindingMode(string? value)
        => Enum.TryParse<BindingMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : BindingMode.Interactive;

    private static ThreadingMode ParseThreadingMode(string? value)
        => Enum.TryParse<ThreadingMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ThreadingMode.Single;

    private static Dictionary<string, object?> DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions) ?? [];
    }

    private static Conversation CloneConversation(Conversation conversation)
        => new()
        {
            ConversationId = conversation.ConversationId,
            AgentId = conversation.AgentId,
            Title = conversation.Title,
            Purpose = conversation.Purpose,
            Instructions = conversation.Instructions,
            CanvasHtml = conversation.CanvasHtml,
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
            Metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(conversation.Metadata, JsonOptions), JsonOptions) ?? [],
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
}
