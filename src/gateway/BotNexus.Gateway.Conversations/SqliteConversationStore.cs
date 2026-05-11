using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BotNexus.Domain.Primitives;
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Conversation> _cache = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>
    /// Initialises a new instance of the <see cref="SqliteConversationStore"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="logger">Logger instance.</param>
    public SqliteConversationStore(string connectionString, ILogger<SqliteConversationStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

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
                return CloneConversation(cached);

            var loaded = await LoadConversationAsync(conversationId, ct).ConfigureAwait(false);
            if (loaded is not null)
                _cache[conversationId.Value] = CloneConversation(loaded);

            return loaded;
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

            conversations.Add(conversation);
        }

        return conversations;
    }

    /// <inheritdoc />
    public async Task<Conversation> GetOrCreateDefaultAsync(AgentId agentId, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.get_or_create_default", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", agentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = """
            SELECT id
            FROM conversations
            WHERE agent_id = $agentId AND is_default = 1 AND status = $status
            ORDER BY created_at ASC
            LIMIT 1
            """;
        selectCommand.Parameters.AddWithValue("$agentId", agentId.Value);
        selectCommand.Parameters.AddWithValue("$status", ConversationStatus.Active.ToString());

        var existingId = (string?)await selectCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            var existing = await GetAsync(ConversationId.From(existingId), ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }
        await using var archivedCommand = connection.CreateCommand();
        archivedCommand.CommandText = """
            SELECT id
            FROM conversations
            WHERE agent_id = $agentId AND is_default = 1 AND status = $status
            ORDER BY updated_at DESC
            LIMIT 1
            """;
        archivedCommand.Parameters.AddWithValue("$agentId", agentId.Value);
        archivedCommand.Parameters.AddWithValue("$status", ConversationStatus.Archived.ToString());
        var archivedId = (string?)await archivedCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(archivedId))
        {
            var reopenedId = ConversationId.From(archivedId);
            var reopenedLock = GetConversationLock(reopenedId.Value);
            await reopenedLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var updatedAt = DateTimeOffset.UtcNow;
                await using var reopenCommand = connection.CreateCommand();
                reopenCommand.CommandText = """
                    UPDATE conversations
                    SET status = $status,
                        active_session_id = NULL,
                        updated_at = $updatedAt
                    WHERE id = $id
                    """;
                reopenCommand.Parameters.AddWithValue("$status", ConversationStatus.Active.ToString());
                reopenCommand.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
                reopenCommand.Parameters.AddWithValue("$id", reopenedId.Value);
                await reopenCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                _cache.TryRemove(reopenedId.Value, out _);
                var reopened = await LoadConversationAsync(connection, reopenedId, ct).ConfigureAwait(false);
                if (reopened is not null)
                {
                    _cache[reopened.ConversationId.Value] = CloneConversation(reopened);
                    return CloneConversation(reopened);
                }
            }
            finally
            {
                reopenedLock.Release();
            }
        }


        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "Default",
            IsDefault = true,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var conversationLock = GetConversationLock(conversation.ConversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var recheckCommand = connection.CreateCommand();
            recheckCommand.CommandText = """
                SELECT id
                FROM conversations
                WHERE agent_id = $agentId AND is_default = 1 AND status = $status
                ORDER BY created_at ASC
                LIMIT 1
                """;
            recheckCommand.Parameters.AddWithValue("$agentId", agentId.Value);
            recheckCommand.Parameters.AddWithValue("$status", ConversationStatus.Active.ToString());

            var recheckedId = (string?)await recheckCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(recheckedId))
            {
                var rechecked = await GetAsync(ConversationId.From(recheckedId), ct).ConfigureAwait(false);
                if (rechecked is not null)
                    return rechecked;
            }

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
    public async Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        ThreadId? threadId,
        CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.resolve_by_binding", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", agentId.Value);
        activity?.SetTag("botnexus.channel.type", channelType.Value);
        activity?.SetTag("botnexus.channel.address", channelAddress.Value);
        if (threadId is not null)
            activity?.SetTag("botnexus.channel.thread_id", threadId.Value.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Use a single query that matches thread_id exactly (including NULL = NULL).
        // The previous two-branch approach matched any binding when threadId was null,
        // causing root-chat messages to resolve into thread-specific conversations.
        command.CommandText = """
                SELECT c.id
                FROM conversations c
                INNER JOIN conversation_bindings b ON b.conversation_id = c.id
                WHERE c.agent_id = $agentId
                  AND c.status = $status
                  AND b.channel_type = $channelType
                  AND lower(b.channel_address) = lower($channelAddress)
                  AND (($threadId IS NULL AND b.thread_id IS NULL)
                       OR ($threadId IS NOT NULL AND lower(ifnull(b.thread_id, '')) = lower($threadId)))
                ORDER BY c.updated_at DESC
                LIMIT 1
                """;
        command.Parameters.AddWithValue("$threadId", (object?)threadId?.Value ?? DBNull.Value);
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
    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("conversation.get_summaries", ActivityKind.Internal);
        if (agentId is not null)
            activity?.SetTag("botnexus.agent.id", agentId.Value);

        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = agentId is null
            ? """
                SELECT
                    c.id,
                    c.agent_id,
                    c.title,
                    c.is_default,
                    c.status,
                    c.active_session_id,
                    c.created_at,
                    c.updated_at,
                    COUNT(b.binding_id)
                FROM conversations c
                LEFT JOIN conversation_bindings b ON b.conversation_id = c.id
                WHERE c.status = 'Active'
                GROUP BY c.id, c.agent_id, c.title, c.is_default, c.status, c.active_session_id, c.created_at, c.updated_at
                ORDER BY c.updated_at DESC
                """
            : """
                SELECT
                    c.id,
                    c.agent_id,
                    c.title,
                    c.is_default,
                    c.status,
                    c.active_session_id,
                    c.created_at,
                    c.updated_at,
                    COUNT(b.binding_id)
                FROM conversations c
                LEFT JOIN conversation_bindings b ON b.conversation_id = c.id
                WHERE c.agent_id = $agentId AND c.status = 'Active'
                GROUP BY c.id, c.agent_id, c.title, c.is_default, c.status, c.active_session_id, c.created_at, c.updated_at
                ORDER BY c.updated_at DESC
                """;
        if (agentId is not null)
            command.Parameters.AddWithValue("$agentId", agentId.Value.Value);

        var summaries = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            summaries.Add(new ConversationSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                checked((int)reader.GetInt64(8)),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7))));
        }

        return summaries;
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
                    is_default INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Active',
                    active_session_id TEXT,
                    metadata TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    thread_id TEXT,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                );

                CREATE INDEX IF NOT EXISTS idx_conversations_agent_id ON conversations(agent_id);
                CREATE INDEX IF NOT EXISTS idx_bindings_conversation_id ON conversation_bindings(conversation_id);
                CREATE INDEX IF NOT EXISTS idx_bindings_lookup ON conversation_bindings(channel_type, channel_address, thread_id);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

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
            SELECT id, agent_id, title, is_default, status, active_session_id, metadata, created_at, updated_at
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
            IsDefault = reader.GetInt64(3) != 0,
            Status = ParseConversationStatus(reader.GetString(4)),
            Metadata = DeserializeMetadata(reader.IsDBNull(6) ? null : reader.GetString(6)),
            CreatedAt = ParseTimestamp(reader.GetString(7)),
            UpdatedAt = ParseTimestamp(reader.GetString(8))
        };
        if (!reader.IsDBNull(5))
            conversation.ActiveSessionId = SessionId.From(reader.GetString(5));

        await reader.DisposeAsync().ConfigureAwait(false);
        conversation.ChannelBindings = await LoadBindingsAsync(connection, conversation.ConversationId, ct).ConfigureAwait(false);
        return conversation;
    }

    private static async Task<List<ChannelBinding>> LoadBindingsAsync(SqliteConnection connection, ConversationId conversationId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT binding_id, channel_type, channel_address, thread_id, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at
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
                ThreadId = reader.IsDBNull(3) ? null : ThreadId.From(reader.GetString(3)),
                Mode = ParseBindingMode(reader.GetString(4)),
                ThreadingMode = ParseThreadingMode(reader.GetString(5)),
                DisplayPrefix = reader.IsDBNull(6) ? null : reader.GetString(6),
                BoundAt = ParseTimestamp(reader.GetString(7)),
                LastInboundAt = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
                LastOutboundAt = reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9))
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
                INSERT INTO conversations (id, agent_id, title, is_default, status, active_session_id, metadata, created_at, updated_at)
                VALUES ($id, $agentId, $title, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    agent_id = excluded.agent_id,
                    title = excluded.title,
                    is_default = excluded.is_default,
                    status = excluded.status,
                    active_session_id = excluded.active_session_id,
                    metadata = excluded.metadata,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at
                """
            : """
                INSERT INTO conversations (id, agent_id, title, is_default, status, active_session_id, metadata, created_at, updated_at)
                VALUES ($id, $agentId, $title, $isDefault, $status, $activeSessionId, $metadata, $createdAt, $updatedAt)
                """;
        conversationCommand.Parameters.AddWithValue("$id", conversation.ConversationId.Value);
        conversationCommand.Parameters.AddWithValue("$agentId", conversation.AgentId.Value);
        conversationCommand.Parameters.AddWithValue("$title", conversation.Title);
        conversationCommand.Parameters.AddWithValue("$isDefault", conversation.IsDefault ? 1 : 0);
        conversationCommand.Parameters.AddWithValue("$status", conversation.Status.ToString());
        conversationCommand.Parameters.AddWithValue("$activeSessionId", conversation.ActiveSessionId is null ? DBNull.Value : conversation.ActiveSessionId.Value.Value);
        conversationCommand.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(conversation.Metadata, JsonOptions));
        conversationCommand.Parameters.AddWithValue("$createdAt", conversation.CreatedAt.ToString("O"));
        conversationCommand.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAt.ToString("O"));
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
                INSERT INTO conversation_bindings (binding_id, conversation_id, channel_type, channel_address, thread_id, mode, threading_mode, display_prefix, bound_at, last_inbound_at, last_outbound_at)
                VALUES ($bindingId, $conversationId, $channelType, $channelAddress, $threadId, $mode, $threadingMode, $displayPrefix, $boundAt, $lastInboundAt, $lastOutboundAt)
                """;
            bindingCommand.Parameters.AddWithValue("$bindingId", binding.BindingId.Value);
            bindingCommand.Parameters.AddWithValue("$conversationId", conversation.ConversationId.Value);
            bindingCommand.Parameters.AddWithValue("$channelType", binding.ChannelType.Value);
            bindingCommand.Parameters.AddWithValue("$channelAddress", binding.ChannelAddress.Value);
            bindingCommand.Parameters.AddWithValue("$threadId", (object?)binding.ThreadId?.Value ?? DBNull.Value);
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

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static DateTimeOffset ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static ConversationStatus ParseConversationStatus(string? value)
        => Enum.TryParse<ConversationStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ConversationStatus.Active;

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
                ThreadId = binding.ThreadId,
                Mode = binding.Mode,
                ThreadingMode = binding.ThreadingMode,
                DisplayPrefix = binding.DisplayPrefix,
                BoundAt = binding.BoundAt,
                LastInboundAt = binding.LastInboundAt,
                LastOutboundAt = binding.LastOutboundAt
            }).ToList()
        };
}
