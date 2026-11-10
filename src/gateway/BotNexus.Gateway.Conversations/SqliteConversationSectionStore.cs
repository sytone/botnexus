using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// SQLite-backed <see cref="IConversationSectionStore"/> for durable, server-side user-defined
/// conversation sections and their conversation assignments (issue #2124). Uses WAL journal mode and
/// a one-time schema initialisation lock, mirroring <see cref="SqliteConversationStore"/>.
/// </summary>
/// <remarks>
/// Custom sections, assignments and additive unified group-order metadata:
/// <list type="bullet">
///   <item><c>conversation_sections</c> - one row per user-defined section (id, agent, world, name,
///     order, collapsed, timestamps).</item>
///   <item><c>conversation_section_assignments</c> - <c>conversation_id -&gt; section_id</c> with the
///     conversation id as the primary key, structurally enforcing the at-most-one-section invariant.
///     <c>ON DELETE CASCADE</c> on the section foreign key means deleting a section drops its
///     assignments (returning those conversations to their system section) without touching the
///     conversations themselves.</item>
/// </list>
/// </remarks>
public sealed class SqliteConversationSectionStore : IConversationSectionStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConversationSectionStore> _logger;
    private readonly IWorldContext? _worldContext;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <summary>Initialises a new store without world stamping (tests and bare wire-ups).</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="logger">Logger instance.</param>
    public SqliteConversationSectionStore(string connectionString, ILogger<SqliteConversationSectionStore> logger)
        : this(connectionString, logger, worldContext: null) { }

    /// <summary>Initialises a new store that stamps the current world id on persisted sections.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="worldContext">Resolves the gateway's current world identity; <c>null</c> disables stamping.</param>
    public SqliteConversationSectionStore(string connectionString, ILogger<SqliteConversationSectionStore> logger, IWorldContext? worldContext)
    {
        _connectionString = connectionString;
        _logger = logger;
        _worldContext = worldContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSection>> ListSectionsAsync(AgentId agentId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, agent_id, world_id, name, section_order, is_collapsed, created_at, updated_at
            FROM conversation_sections
            WHERE agent_id = $agentId
            ORDER BY section_order ASC, created_at ASC, id ASC
            """;
        command.Parameters.AddWithValue("$agentId", agentId.Value);

        var results = new List<ConversationSection>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    /// <inheritdoc />
    public async Task<ConversationSection?> GetSectionAsync(SectionId sectionId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await GetSectionCoreAsync(connection, sectionId, ct).ConfigureAwait(false);
    }

    private static async Task<ConversationSection?> GetSectionCoreAsync(SqliteConnection connection, SectionId sectionId, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, agent_id, world_id, name, section_order, is_collapsed, created_at, updated_at
            FROM conversation_sections
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", sectionId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ConversationSection> CreateSectionAsync(ConversationSection section, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        StampWorldId(section);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var order = await ReadOrderCoreAsync(connection, transaction, section.AgentId, ct).ConfigureAwait(false);
            section.Order = order.Count(e => e.SectionId.HasValue);
            // CreatedAt is write-once (#2316): rebuild the record rather than mutating it.
            section = section with { CreatedAt = DateTimeOffset.UtcNow };
            section.UpdatedAt = section.CreatedAt;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_sections (id, agent_id, world_id, name, section_order, is_collapsed, created_at, updated_at)
                VALUES ($id, $agentId, $worldId, $name, $order, $collapsed, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$id", section.SectionId.Value);
            command.Parameters.AddWithValue("$agentId", section.AgentId.Value);
            command.Parameters.AddWithValue("$worldId", section.WorldId);
            command.Parameters.AddWithValue("$name", section.Name);
            command.Parameters.AddWithValue("$order", section.Order);
            command.Parameters.AddWithValue("$collapsed", section.IsCollapsed ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", section.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", section.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await WriteOrderCoreAsync(connection, transaction, section.AgentId,
                order.Append(ConversationSectionOrderEntry.ForSection(section.SectionId)).ToArray(), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return section with { };
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<ConversationSection?> UpdateSectionAsync(SectionId sectionId, string? name, bool? isCollapsed, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var existing = await GetSectionCoreAsync(connection, sectionId, ct).ConfigureAwait(false);
            if (existing is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversation_sections
                SET name = $name, is_collapsed = $collapsed, updated_at = $updatedAt
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$name", name ?? existing.Name);
            command.Parameters.AddWithValue("$collapsed", (isCollapsed ?? existing.IsCollapsed) ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$id", sectionId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            existing.Name = name ?? existing.Name;
            existing.IsCollapsed = isCollapsed ?? existing.IsCollapsed;
            existing.UpdatedAt = now;
            return existing;
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSectionOrderEntry>> GetGroupOrderAsync(AgentId agentId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            // One database snapshot for section membership and persisted order, including other
            // store instances. Read normalization never materializes metadata as a side effect.
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var order = await ReadOrderCoreAsync(connection, transaction, agentId, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return order;
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSectionOrderEntry>> ReorderGroupsAsync(AgentId agentId,
        IReadOnlyList<ConversationSectionOrderEntry> orderedGroups, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var current = await ReadOrderCoreAsync(connection, transaction, agentId, ct).ConfigureAwait(false);
            var order = ConversationSectionOrderEntry.Reorder(current, orderedGroups);
            await WriteOrderCoreAsync(connection, transaction, agentId, order, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return order;
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task ReorderSectionsAsync(AgentId agentId, IReadOnlyList<SectionId> orderedSectionIds, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var current = await ReadOrderCoreAsync(connection, transaction, agentId, ct).ConfigureAwait(false);
            var order = ConversationSectionOrderEntry.ReorderCustomSlots(current, orderedSectionIds);
            await WriteOrderCoreAsync(connection, transaction, agentId, order, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteSectionAsync(SectionId sectionId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var section = await GetSectionCoreAsync(connection, sectionId, ct, transaction).ConfigureAwait(false);
            if (section is null)
                return;
            var current = await ReadOrderCoreAsync(connection, transaction, section.AgentId, ct).ConfigureAwait(false);
            // Explicit cleanup and order compaction commit with the deletion. Conversations are
            // never touched, and a failure rolls assignments and all order metadata back together.
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM conversation_section_assignments WHERE section_id = $id;
                DELETE FROM conversation_section_group_order WHERE section_id = $id;
                DELETE FROM conversation_sections WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", sectionId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await WriteOrderCoreAsync(connection, transaction, section.AgentId,
                current.Where(e => e.SectionId != sectionId).ToArray(), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task<IReadOnlyList<ConversationSectionOrderEntry>> ReadOrderCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, AgentId agentId, CancellationToken ct)
    {
        var owned = new List<SectionId>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM conversation_sections WHERE agent_id = $agentId ORDER BY section_order, created_at, id";
            command.Parameters.AddWithValue("$agentId", agentId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                owned.Add(SectionId.From(reader.GetString(0)));
        }
        var persisted = new List<ConversationSectionOrderEntry>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT system_group, section_id FROM conversation_section_group_order WHERE agent_id = $agentId ORDER BY group_order";
            command.Parameters.AddWithValue("$agentId", agentId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0) && reader.IsDBNull(1) &&
                    Enum.TryParse<BuiltInConversationSection>(reader.GetString(0), out var system) &&
                    Enum.IsDefined(system) && system.ToString() == reader.GetString(0))
                    persisted.Add(ConversationSectionOrderEntry.ForSystem(system));
                else if (reader.IsDBNull(0) && !reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1)))
                    persisted.Add(ConversationSectionOrderEntry.ForSection(SectionId.From(reader.GetString(1))));
            }
        }
        return ConversationSectionOrderEntry.Normalize(persisted, owned);
    }

    private static async Task WriteOrderCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        AgentId agentId, IReadOnlyList<ConversationSectionOrderEntry> order, CancellationToken ct)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM conversation_section_group_order WHERE agent_id = $agentId";
            clear.Parameters.AddWithValue("$agentId", agentId.Value);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var customIndex = 0;
        var now = DateTimeOffset.UtcNow.ToString("O");
        for (var i = 0; i < order.Count; i++)
        {
            var entry = order[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_section_group_order (agent_id, group_order, system_group, section_id)
                VALUES ($agentId, $order, $system, $sectionId);
                """;
            command.Parameters.AddWithValue("$agentId", agentId.Value);
            command.Parameters.AddWithValue("$order", i);
            command.Parameters.AddWithValue("$system", (object?)entry.System?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$sectionId", (object?)entry.SectionId?.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (entry.SectionId is { } id)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE conversation_sections SET section_order = $order, updated_at = $now WHERE id = $id AND agent_id = $agentId";
                update.Parameters.AddWithValue("$order", customIndex++);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", id.Value);
                update.Parameters.AddWithValue("$agentId", agentId.Value);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task AssignConversationAsync(SectionId sectionId, ConversationId conversationId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (await GetSectionCoreAsync(connection, sectionId, ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"Section '{sectionId}' does not exist.");

            // conversation_id is the primary key, so the upsert replaces any prior assignment -
            // structurally enforcing at-most-one-section per conversation.
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversation_section_assignments (conversation_id, section_id, assigned_at)
                VALUES ($conversationId, $sectionId, $assignedAt)
                ON CONFLICT(conversation_id) DO UPDATE SET section_id = excluded.section_id, assigned_at = excluded.assigned_at
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId.Value);
            command.Parameters.AddWithValue("$sectionId", sectionId.Value);
            command.Parameters.AddWithValue("$assignedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task RemoveConversationAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversation_section_assignments WHERE conversation_id = $conversationId";
            command.Parameters.AddWithValue("$conversationId", conversationId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetAssignmentsAsync(AgentId agentId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.conversation_id, a.section_id
            FROM conversation_section_assignments a
            INNER JOIN conversation_sections s ON s.id = a.section_id
            WHERE s.agent_id = $agentId
            """;
        command.Parameters.AddWithValue("$agentId", agentId.Value);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
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

            await using (var walCommand = connection.CreateCommand())
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS conversation_sections (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    world_id TEXT NOT NULL DEFAULT '',
                    name TEXT NOT NULL,
                    section_order INTEGER NOT NULL DEFAULT 0,
                    is_collapsed INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversation_section_assignments (
                    conversation_id TEXT PRIMARY KEY,
                    section_id TEXT NOT NULL,
                    assigned_at TEXT NOT NULL,
                    FOREIGN KEY (section_id) REFERENCES conversation_sections(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS conversation_section_group_order (
                    agent_id TEXT NOT NULL,
                    group_order INTEGER NOT NULL,
                    system_group TEXT NULL,
                    section_id TEXT NULL,
                    PRIMARY KEY (agent_id, group_order),
                    CHECK ((system_group IS NOT NULL AND section_id IS NULL)
                        OR (system_group IS NULL AND section_id IS NOT NULL)),
                    UNIQUE (agent_id, system_group),
                    UNIQUE (agent_id, section_id),
                    FOREIGN KEY (section_id) REFERENCES conversation_sections(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_conversation_sections_agent ON conversation_sections(agent_id);
                CREATE INDEX IF NOT EXISTS idx_section_assignments_section ON conversation_section_assignments(section_id);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private SqliteConnection CreateConnection() => SqliteConnectionFactory.Create(_connectionString);

    private void StampWorldId(ConversationSection section)
    {
        if (string.IsNullOrEmpty(section.WorldId) && _worldContext is not null)
            section.WorldId = _worldContext.CurrentWorldId;
    }

    private static ConversationSection Map(SqliteDataReader reader) => new()
    {
        SectionId = SectionId.From(reader.GetString(0)),
        AgentId = AgentId.From(reader.GetString(1)),
        WorldId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Name = reader.GetString(3),
        Order = checked((int)reader.GetInt64(4)),
        IsCollapsed = reader.GetInt64(5) != 0,
        CreatedAt = DateTimeOffset.TryParse(reader.GetString(6), out var c) ? c : DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.TryParse(reader.GetString(7), out var u) ? u : DateTimeOffset.UtcNow
    };
}
