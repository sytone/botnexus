using System.Text.Json.Nodes;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// SQLite-backed configuration store (#2646 PBI 1).
///
/// <para>
/// <b>Why a store at all.</b> Roughly 3,200 of the configuration project's ~11,300 lines exist purely
/// because configuration is a hand-edited JSON document: a cross-process file lock, two concurrency
/// exception types, a backup service, a resilient configuration source, and a writer that
/// read-modify-writes the whole document to change one field. Row granularity plus WAL deletes most of
/// that problem class rather than working around it. The measured win is not fewer lines overall - a
/// schema layer, importer and diff arrive to replace them - it is that the remaining lines are about
/// configuration rather than about files.
/// </para>
///
/// <para>
/// <b>The whole-document write is now the import path only (#3532).</b> <see cref="ApplyChangesAsync"/>
/// applies one statement per changed key, so an edit no longer costs a table rewrite and cannot drop
/// keys the caller did not model.
/// </para>
///
/// <para>
/// <b>Presence is row existence, and that is the whole design.</b> Configuration inheritance is
/// three-valued: absent means inherit, explicit <c>null</c> means suppress, a value means override.
/// A nullable column collapses the first two, so the store records one row per (scope, key) and lets
/// row existence carry what <c>TryGetProperty</c> carries in the document. A row whose
/// <c>value</c> is the JSON text <c>null</c> is an explicit null; no row is inherit. Collapsing those
/// is the single highest-risk failure in this direction, which is why #2766's diff compares state
/// before value.
/// </para>
///
/// <para>
/// <b>Schema is hand-written and explicitly migrated - never reflected.</b> Reflecting table layout
/// from runtime types makes migrations emergent and unreviewable, and the configuration graph contains
/// open-world <see cref="System.Text.Json.JsonElement"/> bags (extension configs) that have no CLR type
/// at schema time. The additive-column mechanism here is deliberately identical to
/// <c>SqliteConversationStore</c>'s: a table-driven <c>(Column, Ddl)</c> array applied with
/// <c>ALTER TABLE ... ADD COLUMN</c>, swallowing SQLite's "duplicate column name" so two gateway
/// instances opening a fresh database concurrently do not race. There must not be a second migration
/// mechanism in this codebase.
/// </para>
/// </summary>
public sealed class SqliteConfigStore(string connectionString) : IConfigStore
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    /// <summary>
    /// Additive column migrations for <c>config_entries</c>, in application order.
    ///
    /// <para>
    /// Empty today - the initial schema is created by <c>CREATE TABLE IF NOT EXISTS</c>. The array
    /// exists from the outset so the first schema change is an entry here rather than an invitation to
    /// invent a second mechanism, which is how migration drift starts.
    /// </para>
    /// </summary>
    private static readonly (string Column, string Ddl)[] EntryColumnMigrations = [];

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);

        await using var connection = SqliteConnectionFactory.Create(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key_path, state, value FROM config_entries;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            var state = (ConfigValueState)reader.GetInt32(1);
            var value = reader.IsDBNull(2) ? null : reader.GetString(2);
            result[path] = new ConfigEntry(path, state, value);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var entries = ConfigDocumentFlattener.Flatten(document);

        await using var connection = SqliteConnectionFactory.Create(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Replace wholesale rather than diff-and-patch. An import is a snapshot of a document, and a
        // partial update would leave rows behind for keys the document no longer has - which is the
        // stale-key failure SonicJS #972 records, where a merge-on-write left removed fields present
        // forever.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM config_entries;";
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (path, entry) in entries)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO config_entries (scope, scope_id, key_path, state, value)
                VALUES ($scope, $scopeId, $keyPath, $state, $value);
                """;
            insert.Parameters.AddWithValue("$scope", (int)ConfigScope.World);
            insert.Parameters.AddWithValue("$scopeId", string.Empty);
            insert.Parameters.AddWithValue("$keyPath", path);
            insert.Parameters.AddWithValue("$state", (int)entry.State);
            insert.Parameters.AddWithValue("$value", (object?)entry.Value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>One statement per changed key, and nothing else is touched.</b> Contrast
    /// <see cref="WriteDocumentAsync"/>, which clears the table first: changing one field there rewrites
    /// every row, so the cost of an edit scales with the size of the configuration rather than the size
    /// of the change, and any key the caller did not model is silently dropped on the way through.
    /// </para>
    /// <para>
    /// <b><c>ON CONFLICT</c> requires the uniqueness constraint to name the same columns as the key.</b>
    /// The upsert targets <c>(scope, scope_id, key_path)</c> because that triple is what identifies a
    /// row; targeting <c>key_path</c> alone would collide across scopes the moment agent-scoped rows
    /// arrive, silently overwriting one scope's value with another's.
    /// </para>
    /// <para>
    /// <b>Removals are applied before upserts</b>, matching the document backend. A key can be both a
    /// removal and the ancestor of an upsert when a leaf becomes a branch (<c>"auth": "none"</c> becoming
    /// <c>"auth": { ... }</c>); deleting first clears the stale leaf row, whereas leaving it would give
    /// the store two rows describing incompatible shapes and the rehydrator rejects that document as
    /// inconsistent.
    /// </para>
    /// <para>
    /// The whole change set runs in one transaction, so a failure part-way cannot leave the store holding
    /// half an edit - which for a change spanning a credential and its enable flag would be worse than
    /// applying neither.
    /// </para>
    /// </remarks>
    public async Task ApplyChangesAsync(ConfigChangeSet changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.IsEmpty)
        {
            // Nothing to do. Opening a transaction to write zero rows would burn a WAL frame and bump
            // the database mtime for a write the caller already knows changes nothing.
            return;
        }

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = SqliteConnectionFactory.Create(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in changes.Removals)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM config_entries
                WHERE scope = $scope AND scope_id = $scopeId AND key_path = $keyPath;
                """;
            delete.Parameters.AddWithValue("$scope", (int)ConfigScope.World);
            delete.Parameters.AddWithValue("$scopeId", string.Empty);
            delete.Parameters.AddWithValue("$keyPath", path);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in changes.Upserts)
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO config_entries (scope, scope_id, key_path, state, value)
                VALUES ($scope, $scopeId, $keyPath, $state, $value)
                ON CONFLICT (scope, scope_id, key_path)
                DO UPDATE SET state = excluded.state, value = excluded.value;
                """;
            upsert.Parameters.AddWithValue("$scope", (int)ConfigScope.World);
            upsert.Parameters.AddWithValue("$scopeId", string.Empty);
            upsert.Parameters.AddWithValue("$keyPath", entry.Path);
            upsert.Parameters.AddWithValue("$state", (int)entry.State);
            upsert.Parameters.AddWithValue("$value", (object?)entry.Value ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitialisedAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialised)
            {
                return;
            }

            await using var connection = SqliteConnectionFactory.Create(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                // WAL is what makes concurrent readers free and removes the reason
                // CrossProcessConfigLock exists.
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS config_entries (
                        scope      INTEGER NOT NULL,
                        scope_id   TEXT    NOT NULL,
                        key_path   TEXT    NOT NULL,
                        state      INTEGER NOT NULL,
                        value      TEXT,
                        PRIMARY KEY (scope, scope_id, key_path)
                    );
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var (column, ddl) in EntryColumnMigrations)
            {
                await EnsureColumnAsync(connection, column, ddl, cancellationToken).ConfigureAwait(false);
            }

            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Applies one additive column migration, tolerating the cross-process race.
    ///
    /// <para>
    /// <c>_initLock</c> only serialises within one process. When two gateway instances open a fresh
    /// database concurrently, the loser of the PRAGMA-then-ALTER race would otherwise throw, so the
    /// "duplicate column name" error is swallowed. Identical in shape and reasoning to
    /// <c>SqliteConversationStore.EnsureColumnAsync</c> (#1885, #1383).
    /// </para>
    /// </summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string column,
        string ddl,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(config_entries);";

        var present = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    present = true;
                    break;
                }
            }
        }

        if (present)
        {
            return;
        }

        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Another process won the race and added the column between the PRAGMA and the ALTER.
            // Deliberately swallowed - the desired end state has been reached either way.
        }
    }
}

