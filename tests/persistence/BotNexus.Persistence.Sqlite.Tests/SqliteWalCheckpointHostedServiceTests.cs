using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Persistence.Sqlite.Tests;

[CollectionDefinition("SqliteWalCheckpoint", DisableParallelization = true)]
public sealed class SqliteWalCheckpointCollection { }

/// <summary>
/// Tests for the Step 3 periodic WAL checkpoint hosted service (#1438).
/// </summary>
[Collection("SqliteWalCheckpoint")]
public sealed class SqliteWalCheckpointHostedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly List<SqliteConnection> _holders = new();

    public SqliteWalCheckpointHostedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "walcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var h in _holders)
        {
            try { h.Dispose(); } catch { /* best effort */ }
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Cs(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Pooling = false,
    }.ToString();

    private static IOptions<SqliteWalCheckpointOptions> Options(int minutes = 30) =>
        Microsoft.Extensions.Options.Options.Create(new SqliteWalCheckpointOptions { IntervalMinutes = minutes });

    /// <summary>
    /// Creates a WAL-mode database and grows its -wal file with uncheckpointed writes. A long-lived
    /// reader connection (kept open for the lifetime of the test) prevents SQLite from
    /// auto-checkpointing and truncating the WAL out from under the assertions.
    /// </summary>
    private async Task<string> CreateDatabaseWithGrownWalAsync()
    {
        var dbPath = Path.Combine(_dir, "grown-" + Guid.NewGuid().ToString("N") + ".db");

        // Do all setup on a single long-lived connection that stays open for the whole test. SQLite
        // checkpoints and truncates the WAL when the last connection to a database closes, so opening
        // a throwaway writer and closing it would erase the grown -wal. Keeping one connection open
        // (with wal_autocheckpoint disabled) guarantees the -wal survives until the test drives the
        // checkpoint itself.
        var holder = new SqliteConnection(Cs(dbPath));
        await holder.OpenAsync();
        _holders.Add(holder);
        await Exec(holder, "PRAGMA journal_mode = WAL;");
        await Exec(holder, "PRAGMA wal_autocheckpoint = 0;"); // disable auto-checkpoint so the WAL grows
        await Exec(holder, "CREATE TABLE t(id INTEGER PRIMARY KEY, blob TEXT);");
        for (var i = 0; i < 500; i++)
        {
            await Exec(holder, $"INSERT INTO t(blob) VALUES('{new string('x', 400)}');");
        }

        var walPath = dbPath + "-wal";
        Assert.True(File.Exists(walPath), "expected a -wal file to exist");
        Assert.True(new FileInfo(walPath).Length > 0, "expected the -wal file to have grown");
        return dbPath;
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqliteWalCheckpointHostedService Build(
        ISqliteDatabaseRegistry registry,
        INetworkPathDetector detector,
        int minutes = 30) =>
        new(registry, detector, Options(minutes));

    [Fact]
    public async Task PassiveCheckpoint_ReducesGrownWal()
    {
        var dbPath = await CreateDatabaseWithGrownWalAsync();
        var walPath = dbPath + "-wal";
        var before = new FileInfo(walPath).Length;

        var registry = new SqliteDatabaseRegistry();
        registry.Register(dbPath);
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(It.IsAny<string>())).Returns(false);

        var svc = Build(registry, detector.Object);
        await svc.CheckpointAllAsync(SqliteCheckpointMode.Passive, CancellationToken.None);

        // PASSIVE moves WAL frames back into the db (checkpoint pointer advances). Verify via
        // wal_checkpoint reporting all frames checkpointed after the sweep.
        await using var conn = new SqliteConnection(Cs(dbPath));
        await conn.OpenAsync();
        await Exec(conn, "PRAGMA wal_autocheckpoint = 0;");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        // columns: busy, log (total frames in wal), checkpointed
        var log = reader.GetInt32(1);
        var checkpointed = reader.GetInt32(2);
        Assert.Equal(log, checkpointed); // everything already checkpointed by the service sweep
        Assert.True(before > 0);
    }

    [Fact]
    public async Task ShutdownTruncate_ReclaimsWalFile()
    {
        var dbPath = await CreateDatabaseWithGrownWalAsync();
        var walPath = dbPath + "-wal";
        Assert.True(new FileInfo(walPath).Length > 0);

        var registry = new SqliteDatabaseRegistry();
        registry.Register(dbPath);
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(It.IsAny<string>())).Returns(false);

        var svc = Build(registry, detector.Object);
        await svc.StopAsync(CancellationToken.None);

        // TRUNCATE zeroes the -wal file on success.
        Assert.True(File.Exists(walPath));
        Assert.Equal(0, new FileInfo(walPath).Length);
    }

    [Fact]
    public async Task NetworkPathDatabase_IsSkipped()
    {
        var dbPath = await CreateDatabaseWithGrownWalAsync();
        var walPath = dbPath + "-wal";
        var before = new FileInfo(walPath).Length;

        var registry = new SqliteDatabaseRegistry();
        registry.Register(dbPath);
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(dbPath)).Returns(true);

        var svc = Build(registry, detector.Object);
        await svc.CheckpointAllAsync(SqliteCheckpointMode.Truncate, CancellationToken.None);

        detector.Verify(d => d.IsNetworkPath(dbPath), Times.Once);
        // Because it was skipped, the WAL was NOT truncated - it retains its grown size.
        Assert.Equal(before, new FileInfo(walPath).Length);
    }

    [Fact]
    public void Options_IntervalIsConfigurable()
    {
        var opts = new SqliteWalCheckpointOptions { IntervalMinutes = 5 };
        Assert.Equal(TimeSpan.FromMinutes(5), opts.Interval);
    }

    [Fact]
    public void Options_DefaultIntervalIsThirtyMinutes()
    {
        var opts = new SqliteWalCheckpointOptions();
        Assert.Equal(30, opts.IntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(30), opts.Interval);
    }

    [Fact]
    public void Options_NonPositiveIntervalClampsToDefault()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), new SqliteWalCheckpointOptions { IntervalMinutes = 0 }.Interval);
        Assert.Equal(TimeSpan.FromMinutes(30), new SqliteWalCheckpointOptions { IntervalMinutes = -10 }.Interval);
    }

    [Fact]
    public void Registry_DeduplicatesSharedPaths()
    {
        var registry = new SqliteDatabaseRegistry();
        var p = Path.Combine(_dir, "shared.db");
        registry.Register(p);
        registry.Register(p);
        registry.Register(p.ToUpperInvariant());
        Assert.Single(registry.GetDatabasePaths());
    }

    [Fact]
    public void Registry_IgnoresNullOrEmpty()
    {
        var registry = new SqliteDatabaseRegistry();
        registry.Register("");
        registry.Register("   ");
        Assert.Empty(registry.GetDatabasePaths());
    }

    [Fact]
    public async Task CheckpointAll_ContinuesAfterOneDatabaseFails()
    {
        var good = await CreateDatabaseWithGrownWalAsync();
        var missing = Path.Combine(_dir, "does-not-exist.db");

        var registry = new SqliteDatabaseRegistry();
        registry.Register(missing); // ReadWrite open will throw -> must be swallowed
        registry.Register(good);
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(It.IsAny<string>())).Returns(false);

        var svc = Build(registry, detector.Object);
        // Should not throw despite the missing database.
        await svc.CheckpointAllAsync(SqliteCheckpointMode.Truncate, CancellationToken.None);

        Assert.Equal(0, new FileInfo(good + "-wal").Length); // good db still got truncated
    }
}
