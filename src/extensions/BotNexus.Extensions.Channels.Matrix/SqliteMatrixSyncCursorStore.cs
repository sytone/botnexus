using System.Globalization;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// SQLite-backed <see cref="IMatrixSyncCursorStore"/> (#3595). One row per
/// <c>(agent_id, account_name)</c> holding the account's last fully-processed <c>next_batch</c>
/// token.
/// </summary>
/// <remarks>
/// Follows the established BotNexus store shape: a per-operation connection from the shared
/// <see cref="SqliteConnectionFactory"/> (#1541) so the standard busy-timeout policy applies on
/// every open, filesystem-aware journal mode via <see cref="SqliteWalMaintenance"/>, a write lock
/// serialising upserts, and a declared schema version enforced by
/// <see cref="SqliteSchemaMigrator"/> (#2835).
/// </remarks>
public sealed class SqliteMatrixSyncCursorStore : IMatrixSyncCursorStore, IAsyncDisposable
{
    /// <summary>
    /// The schema version this build writes and understands. Bump it in the same commit as a schema
    /// change and add the matching step to <see cref="Migrations"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Ordered forward-only migrations. Empty at version 1: this is the store's baseline rather than
    /// a replay of history that was never recorded.
    /// </summary>
    private static readonly SqliteSchemaMigration[] Migrations = [];

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteWalMaintenance _walMaintenance = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Creates a store persisting to <paramref name="dbPath"/>. The parent directory is created on
    /// first use, so a fresh install needs no provisioning step.
    /// </summary>
    /// <param name="dbPath">Absolute path of the SQLite database file.</param>
    public SqliteMatrixSyncCursorStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string agentId, string accountName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = SqliteConnectionFactory.Create(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT since_token FROM matrix_sync_cursor
            WHERE agent_id = $agent AND account_name = $account;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$account", accountName);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string token && !string.IsNullOrWhiteSpace(token) ? token : null;
    }

    /// <inheritdoc />
    public async Task SetAsync(string agentId, string accountName, string sinceToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sinceToken);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = SqliteConnectionFactory.Create(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO matrix_sync_cursor (agent_id, account_name, since_token, updated_at)
                VALUES ($agent, $account, $token, $now)
                ON CONFLICT(agent_id, account_name) DO UPDATE SET
                    since_token = $token,
                    updated_at = $now;
                """;
            command.Parameters.AddWithValue("$agent", agentId);
            command.Parameters.AddWithValue("$account", accountName);
            command.Parameters.AddWithValue("$token", sinceToken);
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");

            await using var connection = SqliteConnectionFactory.Create(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await _walMaintenance
                .ApplyJournalModeAsync(connection, _dbPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS matrix_sync_cursor (
                    agent_id TEXT NOT NULL,
                    account_name TEXT NOT NULL,
                    since_token TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (agent_id, account_name)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            SqliteSchemaMigrator.Apply(connection, CurrentSchemaVersion, Migrations);

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
