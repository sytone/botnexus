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
/// Two tables:
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
            ORDER BY section_order ASC, created_at ASC
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

    private static async Task<ConversationSection?> GetSectionCoreAsync(SqliteConnection connection, SectionId sectionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
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

            await using var nextOrderCommand = connection.CreateCommand();
            nextOrderCommand.CommandText = "SELECT COALESCE(MAX(section_order), -1) + 1 FROM conversation_sections WHERE agent_id = $agentId";
            nextOrderCommand.Parameters.AddWithValue("$agentId", section.AgentId.Value);
            section.Order = Convert.ToInt32(await nextOrderCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
            section.CreatedAt = DateTimeOffset.UtcNow;
            section.UpdatedAt = section.CreatedAt;

            await using var command = connection.CreateCommand();
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
    public async Task ReorderSectionsAsync(AgentId agentId, IReadOnlyList<SectionId> orderedSectionIds, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Load owned sections in current order so omitted ids keep their relative order after the
            // supplied ones.
            var owned = new List<string>();
            await using (var listCommand = connection.CreateCommand())
            {
                listCommand.CommandText = "SELECT id FROM conversation_sections WHERE agent_id = $agentId ORDER BY section_order ASC, created_at ASC";
                listCommand.Parameters.AddWithValue("$agentId", agentId.Value);
                await using var reader = await listCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    owned.Add(reader.GetString(0));
            }
            var ownedSet = owned.ToHashSet(StringComparer.Ordinal);

            var finalOrder = new List<string>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in orderedSectionIds)
            {
                if (ownedSet.Contains(id.Value) && placed.Add(id.Value))
                    finalOrder.Add(id.Value);
            }
            foreach (var id in owned.Where(id => !placed.Contains(id)))
                finalOrder.Add(id);

            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < finalOrder.Count; i++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE conversation_sections SET section_order = $order, updated_at = $updatedAt WHERE id = $id";
                command.Parameters.AddWithValue("$order", i);
                command.Parameters.AddWithValue("$updatedAt", now);
                command.Parameters.AddWithValue("$id", finalOrder[i]);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
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

            // Delete assignments first (defensive: cascade is enabled, but be explicit so the
            // conversations return to their system section even if PRAGMA foreign_keys is off).
            await using (var deleteAssignments = connection.CreateCommand())
            {
                deleteAssignments.CommandText = "DELETE FROM conversation_section_assignments WHERE section_id = $id";
                deleteAssignments.Parameters.AddWithValue("$id", sectionId.Value);
                await deleteAssignments.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversation_sections WHERE id = $id";
            command.Parameters.AddWithValue("$id", sectionId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
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
