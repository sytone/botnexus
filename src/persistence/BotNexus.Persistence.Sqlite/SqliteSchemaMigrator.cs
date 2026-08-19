using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// The forward-only schema migration runner for BotNexus SQLite stores (#2835).
/// </summary>
/// <remarks>
/// <para><b>Order of the two guards.</b> This runs <i>after</i>
/// <see cref="SqliteStoreIdentityGuard"/> has passed. Migrating a store before knowing it belongs to
/// this world would write schema changes into another world's data - turning a refusable open into an
/// irreversible one.</para>
/// <para><b>Why the whole step is one transaction.</b> Clause 4 of the PBI: a failed migration must
/// leave the store at its original version with no partial schema change. SQLite's DDL is
/// transactional, and <c>PRAGMA user_version</c> writes the database header, which is journaled too -
/// so bracketing the schema change and BOTH version stamps in a single transaction makes
/// "half-migrated" unrepresentable rather than merely unlikely.</para>
/// <para><b>Why an unversioned store adopts rather than replays.</b> Every store shipped before this
/// feature is already at the current shape; replaying migrations from zero against it would re-run
/// steps whose effects are present, and the PBI explicitly excludes backfilling historical
/// migrations. Adoption is also what makes the fresh-store case free.</para>
/// </remarks>
public static class SqliteSchemaMigrator
{
    /// <summary>
    /// Brings the store behind <paramref name="connection"/> to <paramref name="codeVersion"/>,
    /// refusing to open a store that is ahead of the running code.
    /// </summary>
    /// <param name="connection">An OPEN connection to the store.</param>
    /// <param name="codeVersion">The schema version this build of the store understands. Must be positive.</param>
    /// <param name="migrations">
    /// The store's full ordered migration set. Order within the collection is irrelevant - the runner
    /// sorts by <see cref="SqliteSchemaMigration.TargetVersion"/> - but duplicate or unreachable
    /// versions are rejected.
    /// </param>
    /// <exception cref="SqliteSchemaVersionMismatchException">
    /// The store was written by a newer schema than this code understands.
    /// </exception>
    public static void Apply(
        SqliteConnection connection,
        int codeVersion,
        IReadOnlyList<SqliteSchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentOutOfRangeException.ThrowIfLessThan(codeVersion, 1);

        var ordered = Validate(codeVersion, migrations);

        var path = connection.DataSource;

        // In-memory stores have no durable schema to version across process lifetimes and no path to
        // name in a failure message, so there is nothing for a version check to protect. Matches the
        // identity guard's treatment of the same case, and keeps schema-shape assertions elsewhere in
        // the codebase from having to know about a store_meta table they never asked for.
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureMetaTable(connection);
        var storedVersion = ReadStoredVersion(connection);

        if (storedVersion == codeVersion)
        {
            // Equal versions proceed - but the two slots must still agree. A store whose store_meta
            // row was written while its pragma was not (a store versioned by hand, or an older
            // BotNexus that only knew one slot) would otherwise disagree with itself forever.
            if (ReadUserVersion(connection) != codeVersion)
            {
                InTransaction(connection, () => WriteVersion(connection, codeVersion));
            }

            return;
        }

        if (storedVersion > codeVersion)
        {
            throw new SqliteSchemaVersionMismatchException(
                $"SQLite store '{path}' was written by schema version {storedVersion} but this process understands " +
                $"only version {codeVersion}. Refusing to open it: older code reading a newer store silently ignores " +
                "columns it does not know about and writes rows the newer code will later read as incomplete. Run the " +
                "newer build, or restore a backup taken at version " + codeVersion.ToString(CultureInfo.InvariantCulture) + ".",
                storedVersion,
                codeVersion,
                path);
        }

        if (storedVersion == SqliteSchemaVersion.Unversioned)
        {
            // Clauses 5 and 6 share this path and differ only in whether the store already holds
            // data: an empty store is bootstrapped directly, and a pre-existing unversioned store
            // adopts the current version as its baseline. Neither replays migrations.
            InTransaction(connection, () => WriteVersion(connection, codeVersion));
            return;
        }

        InTransaction(connection, () =>
        {
            foreach (var migration in ordered)
            {
                if (migration.TargetVersion <= storedVersion)
                    continue;

                migration.Apply(connection);
            }

            WriteVersion(connection, codeVersion);
        });
    }

    private static List<SqliteSchemaMigration> Validate(
        int codeVersion,
        IReadOnlyList<SqliteSchemaMigration> migrations)
    {
        var seen = new HashSet<int>();
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);

            if (migration.TargetVersion < 1)
            {
                throw new ArgumentException(
                    $"Migration '{migration.Description}' declares target version {migration.TargetVersion}; " +
                    "schema versions start at 1.",
                    nameof(migrations));
            }

            // A migration past the code's own version can never run, so the store could never reach
            // the version it declares. That is a build-time mistake, and silently ignoring it would
            // hide a migration the author believes is shipping.
            if (migration.TargetVersion > codeVersion)
            {
                throw new ArgumentException(
                    $"Migration '{migration.Description}' targets version {migration.TargetVersion}, which is beyond " +
                    $"the declared code version {codeVersion}. Raise the store's schema version constant.",
                    nameof(migrations));
            }

            if (!seen.Add(migration.TargetVersion))
            {
                throw new ArgumentException(
                    $"Two migrations target version {migration.TargetVersion}. The resulting schema would depend on " +
                    "declaration order, which is not a decidable migration history.",
                    nameof(migrations));
            }
        }

        return [.. migrations.OrderBy(m => m.TargetVersion)];
    }

    private static void InTransaction(SqliteConnection connection, Action body)
    {
        using var transaction = connection.BeginTransaction();
        body();
        transaction.Commit();
    }

    private static void EnsureMetaTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE IF NOT EXISTS {SqliteStoreIdentity.TableName} (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private static int ReadStoredVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SqliteSchemaVersion.SchemaVersionKey);

        // store_meta is the authority, not the pragma: it is the value BotNexus writes, and the two
        // are always written together in one transaction, so the pragma can never be ahead of it.
        if (command.ExecuteScalar() is not string raw)
            return SqliteSchemaVersion.Unversioned;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : SqliteSchemaVersion.Unversioned;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void WriteVersion(SqliteConnection connection, int version)
    {
        using var meta = connection.CreateCommand();
        meta.CommandText =
            $"INSERT INTO {SqliteStoreIdentity.TableName} (key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        meta.Parameters.AddWithValue("$key", SqliteSchemaVersion.SchemaVersionKey);
        meta.Parameters.AddWithValue("$value", version.ToString(CultureInfo.InvariantCulture));
        meta.ExecuteNonQuery();

        // PRAGMA user_version takes no parameters - it is a header write, not a statement with a
        // bindable value - so the integer is interpolated. It is an int, never caller text.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
        pragma.ExecuteNonQuery();
    }
}
