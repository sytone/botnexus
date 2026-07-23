using BotNexus.Domain.Primitives;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// SQLite-backed <see cref="IToolStore"/>. Persists portal tools to a database file so
/// they survive gateway restarts. Uses a filesystem-aware journal mode and a write-lock
/// semaphore for safe concurrent access, mirroring the cron and webhook stores.
/// </summary>
public sealed class SqliteToolStore(
    string dbPath,
    IFileSystem? fileSystem = null,
    ILogger<SqliteToolStore>? logger = null) : IToolStore
{
    private readonly string _dbPath = dbPath;
    private readonly SqliteWalMaintenance _walMaintenance = new(fileSystem);
    private readonly string _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();
    private readonly ILogger<SqliteToolStore> _logger = logger ?? NullLogger<SqliteToolStore>.Instance;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await _walMaintenance.ApplyJournalModeAsync(connection, _dbPath, cancellationToken: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tools (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    url TEXT NOT NULL,
                    icon TEXT NOT NULL DEFAULT '',
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    sandbox_enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_tools_sort_order
                ON tools(sort_order);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, url, icon, sort_order, sandbox_enabled, created_at
            FROM tools
            ORDER BY sort_order ASC, created_at ASC
            """;

        List<ToolDefinition> tools = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            tools.Add(ReadTool(reader));

        return tools;
    }

    /// <inheritdoc />
    public async Task<ToolDefinition?> GetAsync(ToolId id, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, url, icon, sort_order, sandbox_enabled, created_at
            FROM tools
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadTool(reader)
            : null;
    }

    /// <inheritdoc />
    public async Task<ToolDefinition> CreateAsync(ToolDefinition tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await InitializeAsync(ct).ConfigureAwait(false);

        var created = tool with
        {
            CreatedAt = tool.CreatedAt == default ? DateTimeOffset.UtcNow : tool.CreatedAt
        };

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tools (id, name, url, icon, sort_order, sandbox_enabled, created_at)
                VALUES ($id, $name, $url, $icon, $order, $sandbox, $createdAt)
                """;
            BindTool(command, created);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Created tool '{ToolId}' ({Name}).", created.Id.Value, created.Name);
            return created;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ToolDefinition> UpdateAsync(ToolDefinition tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tools (id, name, url, icon, sort_order, sandbox_enabled, created_at)
                VALUES ($id, $name, $url, $icon, $order, $sandbox, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    url = excluded.url,
                    icon = excluded.icon,
                    sort_order = excluded.sort_order,
                    sandbox_enabled = excluded.sandbox_enabled,
                    created_at = excluded.created_at
                """;
            BindTool(command, tool);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Updated tool '{ToolId}' ({Name}).", tool.Id.Value, tool.Name);
            return tool;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ToolId id, CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tools WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted tool '{ToolId}'.", id.Value);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.Create(_connectionString);

    private static void BindTool(SqliteCommand command, ToolDefinition tool)
    {
        command.Parameters.AddWithValue("$id", tool.Id.Value);
        command.Parameters.AddWithValue("$name", tool.Name);
        command.Parameters.AddWithValue("$url", tool.Url);
        command.Parameters.AddWithValue("$icon", tool.Icon ?? string.Empty);
        command.Parameters.AddWithValue("$order", tool.Order);
        command.Parameters.AddWithValue("$sandbox", tool.SandboxEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", tool.CreatedAt.ToString("O"));
    }

    private static ToolDefinition ReadTool(SqliteDataReader reader)
    {
        return new ToolDefinition
        {
            Id = ToolId.From(reader.GetString(0)),
            Name = reader.GetString(1),
            Url = reader.GetString(2),
            Icon = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Order = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            SandboxEnabled = reader.IsDBNull(5) || reader.GetInt32(5) != 0,
            CreatedAt = ParseDate(reader.GetString(6))
        };
    }

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
