using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Covers the per-store schema version and the forward-only migration runner (#2835).
/// </summary>
/// <remarks>
/// Every assertion here reads the resulting state back <b>from disk on a fresh connection</b> rather
/// than trusting the value the runner returned. The failure this feature exists to prevent is a
/// store whose on-disk shape disagrees with what the running code believes; a test that asserts the
/// runner's own return value would pass in exactly the scenario the feature is meant to catch.
/// </remarks>
public sealed class SqliteSchemaMigratorTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-schema-version-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A held file handle on Windows must not fail an otherwise-passing test.
        }
    }

    private string PathFor(string fileName) => Path.Combine(_directory, fileName);

    private static string ConnectionString(string path) => $"Data Source={path};Mode=ReadWriteCreate";

    private SqliteConnection OpenAt(string path)
    {
        var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        return connection;
    }

    private static SqliteSchemaMigration Creating(int version, string table) =>
        new(version, $"create-{table}", c => Execute(c, $"CREATE TABLE IF NOT EXISTS {table} (id INTEGER PRIMARY KEY);"));

    private static SqliteSchemaMigration Exploding(int version) =>
        new(version, $"explode-{version}", _ => throw new InvalidOperationException($"migration {version} exploded"));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        command.Parameters.AddWithValue("$n", table);
        return command.ExecuteScalar() is not null;
    }

    private int MetaVersionOnDisk(string path)
    {
        using var connection = OpenAt(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $k;";
        command.Parameters.AddWithValue("$k", SqliteSchemaVersion.SchemaVersionKey);
        var raw = command.ExecuteScalar() as string;
        return raw is null ? -1 : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    private int PragmaVersionOnDisk(string path)
    {
        using var connection = OpenAt(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Clause 1: a fresh store records the code's version in BOTH <c>store_meta</c> and
    /// <c>PRAGMA user_version</c>.
    /// </summary>
    [Fact]
    public void Empty_store_records_version_in_store_meta_and_user_version()
    {
        var path = PathFor("fresh.sqlite");

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(connection, codeVersion: 3, migrations: [Creating(2, "a"), Creating(3, "b")]);
        }

        Assert.Equal(3, MetaVersionOnDisk(path));
        Assert.Equal(3, PragmaVersionOnDisk(path));
    }

    /// <summary>
    /// Clause 5: an empty store is bootstrapped DIRECTLY to the current version. Proven by migrations
    /// that throw if they are executed at all - if the runner replayed from zero this test fails.
    /// </summary>
    [Fact]
    public void Empty_store_is_bootstrapped_without_replaying_migrations()
    {
        var path = PathFor("bootstrap.sqlite");

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(connection, codeVersion: 4, migrations: [Exploding(2), Exploding(3), Exploding(4)]);
        }

        Assert.Equal(4, MetaVersionOnDisk(path));
        Assert.Equal(4, PragmaVersionOnDisk(path));
    }

    /// <summary>
    /// Clause 3: a store BEHIND the code runs exactly the intervening migrations, in order, and
    /// finishes at the code's version. Asserted by reading the resulting schema back from disk.
    /// </summary>
    [Fact]
    public void Store_behind_code_runs_intervening_migrations_in_order()
    {
        var path = PathFor("behind.sqlite");
        var executed = new List<int>();

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            SqliteSchemaMigrator.Apply(connection, codeVersion: 1, migrations: []);
        }

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(
                connection,
                codeVersion: 4,
                migrations:
                [
                    // Deliberately out of order in the array: the runner must sort, and must skip
                    // version 1 because the store already holds it.
                    new SqliteSchemaMigration(3, "three", c => { executed.Add(3); Execute(c, "CREATE TABLE t3 (id INTEGER);"); }),
                    new SqliteSchemaMigration(1, "one", _ => executed.Add(1)),
                    new SqliteSchemaMigration(4, "four", c => { executed.Add(4); Execute(c, "CREATE TABLE t4 (id INTEGER);"); }),
                    new SqliteSchemaMigration(2, "two", c => { executed.Add(2); Execute(c, "CREATE TABLE t2 (id INTEGER);"); }),
                ]);
        }

        Assert.Equal([2, 3, 4], executed);
        Assert.Equal(4, MetaVersionOnDisk(path));
        Assert.Equal(4, PragmaVersionOnDisk(path));

        using var verify = OpenAt(path);
        Assert.True(TableExists(verify, "t2"));
        Assert.True(TableExists(verify, "t3"));
        Assert.True(TableExists(verify, "t4"));
    }

    /// <summary>
    /// Clause 2: a store AHEAD of the code is refused, and the message names both versions and the
    /// store path so an operator can act on it without reading source.
    /// </summary>
    [Fact]
    public void Store_ahead_of_code_throws_naming_both_versions_and_path()
    {
        var path = PathFor("ahead.sqlite");

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            SqliteSchemaMigrator.Apply(connection, codeVersion: 9, migrations: []);
        }

        using var opened = OpenAt(path);
        var error = Assert.Throws<SqliteSchemaVersionMismatchException>(
            () => SqliteSchemaMigrator.Apply(opened, codeVersion: 5, migrations: []));

        Assert.Equal(9, error.StoreVersion);
        Assert.Equal(5, error.CodeVersion);
        Assert.Equal(path, error.StorePath);
        Assert.Contains("9", error.Message, StringComparison.Ordinal);
        Assert.Contains("5", error.Message, StringComparison.Ordinal);
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clause 4: a throwing migration rolls the whole step back - the store keeps its ORIGINAL
    /// version in both slots and none of the partial schema change survives.
    /// </summary>
    [Fact]
    public void Failed_migration_leaves_store_at_original_version_with_no_partial_change()
    {
        var path = PathFor("failing.sqlite");

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            SqliteSchemaMigrator.Apply(connection, codeVersion: 1, migrations: []);
        }

        using (var connection = OpenAt(path))
        {
            Assert.Throws<InvalidOperationException>(
                () => SqliteSchemaMigrator.Apply(
                    connection,
                    codeVersion: 3,
                    migrations: [Creating(2, "half_applied"), Exploding(3)]));
        }

        Assert.Equal(1, MetaVersionOnDisk(path));
        Assert.Equal(1, PragmaVersionOnDisk(path));

        using var verify = OpenAt(path);
        Assert.False(TableExists(verify, "half_applied"));
    }

    /// <summary>
    /// Clause 6: an existing store with data but no recorded version adopts the code's version as its
    /// baseline. Migrations that would throw if executed prove it is adopted, not migrated.
    /// </summary>
    [Fact]
    public void Existing_unversioned_store_adopts_current_version_as_baseline()
    {
        var path = PathFor("legacy.sqlite");

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO payload (id) VALUES (1);");
        }

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(connection, codeVersion: 7, migrations: [Exploding(6), Exploding(7)]);
        }

        Assert.Equal(7, MetaVersionOnDisk(path));
        Assert.Equal(7, PragmaVersionOnDisk(path));

        using var verify = OpenAt(path);
        Assert.True(TableExists(verify, "payload"));
    }

    /// <summary>
    /// Equal versions proceed and execute nothing: re-running the runner against an up-to-date store
    /// is a no-op, which is what makes it safe to call on every store initialisation.
    /// </summary>
    [Fact]
    public void Store_at_code_version_runs_no_migrations()
    {
        var path = PathFor("equal.sqlite");
        var executed = 0;

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(connection, codeVersion: 2, migrations: []);
        }

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(
                connection,
                codeVersion: 2,
                migrations: [new SqliteSchemaMigration(2, "two", _ => executed++)]);
        }

        Assert.Equal(0, executed);
        Assert.Equal(2, MetaVersionOnDisk(path));
    }

    /// <summary>
    /// Applying the same migration set repeatedly converges: the second run migrates nothing and the
    /// version is unchanged. Idempotency is a property of the runner, not only of each migration.
    /// </summary>
    [Fact]
    public void Repeated_apply_is_idempotent()
    {
        var path = PathFor("idempotent.sqlite");
        var executed = new List<int>();
        SqliteSchemaMigration[] Migrations() =>
        [
            new(2, "two", c => { executed.Add(2); Execute(c, "CREATE TABLE IF NOT EXISTS t2 (id INTEGER);"); }),
        ];

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            SqliteSchemaMigrator.Apply(connection, codeVersion: 1, migrations: []);
        }

        for (var i = 0; i < 3; i++)
        {
            using var connection = OpenAt(path);
            SqliteSchemaMigrator.Apply(connection, codeVersion: 2, migrations: Migrations());
        }

        Assert.Equal([2], executed);
        Assert.Equal(2, MetaVersionOnDisk(path));
        Assert.Equal(2, PragmaVersionOnDisk(path));
    }

    /// <summary>
    /// The two version slots must agree. A store whose <c>store_meta</c> row was written but whose
    /// <c>user_version</c> pragma was not (an interrupted write, or a store versioned by hand) is
    /// repaired on next open rather than left permanently disagreeing with itself.
    /// </summary>
    [Fact]
    public void Mirrors_store_meta_version_into_user_version_when_pragma_lags()
    {
        var path = PathFor("lagging.sqlite");

        using (var connection = OpenAt(path))
        {
            Execute(connection, "CREATE TABLE payload (id INTEGER PRIMARY KEY);");
            SqliteSchemaMigrator.Apply(connection, codeVersion: 5, migrations: []);
            Execute(connection, "PRAGMA user_version = 0;");
        }

        Assert.Equal(0, PragmaVersionOnDisk(path));

        using (var connection = OpenAt(path))
        {
            SqliteSchemaMigrator.Apply(connection, codeVersion: 5, migrations: []);
        }

        Assert.Equal(5, MetaVersionOnDisk(path));
        Assert.Equal(5, PragmaVersionOnDisk(path));
    }

    /// <summary>
    /// A migration whose target version exceeds the code's version is a programming error - it can
    /// never be reached, so the store could never arrive at the declared version. Fail loudly at the
    /// call site rather than silently ignoring it.
    /// </summary>
    [Fact]
    public void Migration_beyond_code_version_is_rejected()
    {
        var path = PathFor("beyond.sqlite");
        using var connection = OpenAt(path);

        var error = Assert.Throws<ArgumentException>(
            () => SqliteSchemaMigrator.Apply(connection, codeVersion: 2, migrations: [Creating(3, "future")]));

        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two migrations declaring the same target version make the resulting schema depend on array
    /// order, which is not a decidable migration history. Rejected at the call site.
    /// </summary>
    [Fact]
    public void Duplicate_migration_versions_are_rejected()
    {
        var path = PathFor("dupe.sqlite");
        using var connection = OpenAt(path);

        var error = Assert.Throws<ArgumentException>(
            () => SqliteSchemaMigrator.Apply(
                connection, codeVersion: 2, migrations: [Creating(2, "a"), Creating(2, "b")]));

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// In-memory stores have no durable schema to version and no path to name in a failure, so the
    /// runner is a no-op for them - matching the identity guard's treatment of the same case.
    /// </summary>
    [Fact]
    public void In_memory_store_is_not_versioned()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SqliteSchemaMigrator.Apply(connection, codeVersion: 3, migrations: [Exploding(3)]);

        Assert.False(TableExists(connection, SqliteStoreIdentity.TableName));
    }
}
