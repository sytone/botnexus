using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Covers the world-identity stamp and verification (#2833). Each test configures and then resets
/// the process-wide guard, because the guard is deliberately process-scoped: identity is a property
/// of the running world, not of a call site.
/// </summary>
[Collection("store-identity")]
public sealed class SqliteStoreIdentityGuardTests : IDisposable
{
    private const string WorldA = "11111111-1111-1111-1111-111111111111";
    private const string WorldB = "22222222-2222-2222-2222-222222222222";

    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-store-identity-").FullName;

    public void Dispose()
    {
        SqliteStoreIdentityGuard.Reset();
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

    private static string ConnectionString(string path) => $"Data Source={path}";

    /// <summary>Clause 1: an empty store is stamped with world_id and store_kind on first open.</summary>
    [Fact]
    public void Empty_store_is_stamped_with_world_id_and_store_kind()
    {
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));
        var path = PathFor("cron.sqlite");

        using (var connection = SqliteConnectionFactory.Create(ConnectionString(path)))
        {
            connection.Open();
        }

        Assert.Equal(WorldA, ReadMetaFromDisk(path, SqliteStoreIdentity.WorldIdKey));
        Assert.Equal("cron", ReadMetaFromDisk(path, SqliteStoreIdentity.StoreKindKey));
        Assert.False(string.IsNullOrWhiteSpace(ReadMetaFromDisk(path, SqliteStoreIdentity.CreatedAtKey)));
        Assert.False(
            string.IsNullOrWhiteSpace(ReadMetaFromDisk(path, SqliteStoreIdentity.CreatedByVersionKey)));
    }

    /// <summary>Happy path: a store stamped with the running world opens without complaint.</summary>
    [Fact]
    public void Matching_world_id_opens_successfully()
    {
        var path = PathFor("cron.sqlite");
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using (var first = SqliteConnectionFactory.Create(ConnectionString(path)))
        {
            first.Open();
        }

        // A fresh guard state proves the second open is decided by what is ON DISK, not by the
        // in-process memo from the first open.
        SqliteStoreIdentityGuard.Reset();
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using var second = SqliteConnectionFactory.Create(ConnectionString(path));
        second.Open();

        Assert.Equal(System.Data.ConnectionState.Open, second.State);
        Assert.Equal(WorldA, ReadMetaFromDisk(path, SqliteStoreIdentity.WorldIdKey));
    }

    /// <summary>
    /// Clause 2: a store stamped for another world is refused, and the message names BOTH world IDs
    /// and the store path. An operator reading only one ID cannot tell which side is wrong.
    /// </summary>
    [Fact]
    public void Mismatched_world_id_throws_naming_both_worlds_and_path()
    {
        var path = PathFor("cron.sqlite");
        StampStore(path, WorldB, "cron");

        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using var connection = SqliteConnectionFactory.Create(ConnectionString(path));
        var ex = Assert.Throws<SqliteStoreIdentityMismatchException>(() => connection.Open());

        Assert.Contains(WorldA, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WorldB, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_directory, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorldA, ex.ExpectedWorldId);
        Assert.Equal(WorldB, ex.ActualWorldId);
        Assert.Equal(path, ex.StorePath);
        Assert.Equal(_directory, ex.HomePath);
    }

    /// <summary>Clause 3: opening sessions.db as the cron store is refused.</summary>
    [Fact]
    public void Mismatched_store_kind_throws()
    {
        var path = PathFor("sessions.db");
        StampStore(path, WorldA, "sessions");

        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using var connection = SqliteConnectionFactory.CreateForStoreKind(ConnectionString(path), storeKind: "cron");
        var ex = Assert.Throws<SqliteStoreIdentityMismatchException>(() => connection.Open());

        Assert.Contains("sessions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cron", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clause 4: an existing unstamped store with real tables in it is adopted, stamped, and warned
    /// about EXACTLY once, with the store path in the warning.
    /// </summary>
    [Fact]
    public void Unstamped_store_with_tables_is_adopted_and_warned_exactly_once()
    {
        var path = PathFor("cron.sqlite");
        CreateLegacyStore(path);

        var logger = new CapturingLogger();
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory), logger);

        for (var i = 0; i < 3; i++)
        {
            SqliteStoreIdentityGuard.Reset();
            SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory), logger);
            using var connection = SqliteConnectionFactory.Create(ConnectionString(path));
            connection.Open();
        }

        Assert.Equal(WorldA, ReadMetaFromDisk(path, SqliteStoreIdentity.WorldIdKey));

        var warnings = logger.Warnings.Where(w => w.Contains(path, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(warnings);
    }

    /// <summary>
    /// Clause 4 boundary: an EMPTY store must be stamped silently. Warning on every fresh store
    /// would make the adoption warning meaningless noise, and there is nothing to adopt.
    /// </summary>
    [Fact]
    public void Empty_store_is_stamped_without_an_adoption_warning()
    {
        var logger = new CapturingLogger();
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory), logger);
        var path = PathFor("cron.sqlite");

        using (var connection = SqliteConnectionFactory.Create(ConnectionString(path)))
        {
            connection.Open();
        }

        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// Clause 5: verification lives at the connection seam. A store type that has no identity code
    /// of its own - modelled here by an arbitrary new store file nobody has written a check for -
    /// is still stamped and still refused when it belongs to another world.
    /// </summary>
    [Fact]
    public void Store_type_with_no_identity_code_is_still_verified_at_the_seam()
    {
        var path = PathFor("some-store-invented-tomorrow.db");
        StampStore(path, WorldB, "some-store-invented-tomorrow");

        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using var connection = SqliteConnectionFactory.Create(ConnectionString(path));
        Assert.Throws<SqliteStoreIdentityMismatchException>(() => connection.Open());
    }

    /// <summary>
    /// Clause 6: the #2819 shape. A process configured for world A resolves a path that belongs to
    /// world B; the open is refused AND - the part that actually matters - no row reaches world B's
    /// store. Asserted against rows present on disk, read back through a raw connection that
    /// bypasses the factory, not against what the resolver returned.
    /// </summary>
    [Fact]
    public void World_a_process_cannot_write_into_a_world_b_store()
    {
        var worldBStore = PathFor("cron.sqlite");
        StampStore(worldBStore, WorldB, "cron");
        CreateJobsTable(worldBStore);

        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        var threw = false;
        try
        {
            using var connection = SqliteConnectionFactory.Create(ConnectionString(worldBStore));
            connection.Open();

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO jobs (id) VALUES ('phantom-job');";
            insert.ExecuteNonQuery();
        }
        catch (SqliteStoreIdentityMismatchException)
        {
            threw = true;
        }

        Assert.True(threw, "World A must be refused the world B store.");

        // The load-bearing assertion: zero phantom rows on disk, in the shape #2819 produced.
        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection(ConnectionString(worldBStore));
        raw.Open();
        using var count = raw.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM jobs;";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));

        // And world B's own identity is untouched - a refused open must not rewrite the stamp.
        using var stamp = raw.CreateCommand();
        stamp.CommandText = "SELECT value FROM store_meta WHERE key = 'world_id';";
        Assert.Equal(WorldB, stamp.ExecuteScalar() as string);
    }

    /// <summary>
    /// With no identity configured the guard is inert: no stamp, no throw. Hosts and tools that have
    /// not opted in keep their existing behaviour exactly.
    /// </summary>
    [Fact]
    public void Guard_is_inert_when_no_identity_is_configured()
    {
        SqliteStoreIdentityGuard.Reset();
        var path = PathFor("cron.sqlite");

        using (var connection = SqliteConnectionFactory.Create(ConnectionString(path)))
        {
            connection.Open();
        }

        Assert.Null(ReadMetaFromDisk(path, SqliteStoreIdentity.WorldIdKey));
    }

    /// <summary>An in-memory store has no mis-resolvable path, so it is neither stamped nor refused.</summary>
    [Fact]
    public void In_memory_store_is_not_stamped()
    {
        SqliteStoreIdentityGuard.Configure(new SqliteStoreIdentity(WorldA, _directory));

        using var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'store_meta';";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Theory]
    [InlineData("cron.sqlite", "cron")]
    [InlineData("sessions.db", "sessions")]
    [InlineData("CONFIG.DB", "config")]
    [InlineData("", "unknown")]
    public void DeriveStoreKind_uses_the_file_name(string fileName, string expected)
        => Assert.Equal(expected, SqliteStoreIdentityGuard.DeriveStoreKind(fileName));

    private static void StampStore(string path, string worldId, string kind)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        create.ExecuteNonQuery();
        Insert(connection, "world_id", worldId);
        Insert(connection, "store_kind", kind);

        static void Insert(SqliteConnection connection, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO store_meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            command.ExecuteNonQuery();
        }
    }

    private static void CreateJobsTable(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY);";
        command.ExecuteNonQuery();
    }

    private static void CreateLegacyStore(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_rows (id TEXT PRIMARY KEY);";
        command.ExecuteNonQuery();
    }

    private static string? ReadMetaFromDisk(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='store_meta';";
        if (exists.ExecuteScalar() is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM store_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
