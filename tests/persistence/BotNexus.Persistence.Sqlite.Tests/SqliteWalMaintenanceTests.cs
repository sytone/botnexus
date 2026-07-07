using Microsoft.Data.Sqlite;
using Moq;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteWalMaintenanceTests : IDisposable
{
    private readonly string _dir;

    public SqliteWalMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-wal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering -wal handle on Windows must not fail the test.
        }
    }

    private static SqliteWalMaintenance CreateHelper(bool isNetwork)
    {
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(It.IsAny<string>())).Returns(isNetwork);
        return new SqliteWalMaintenance(detector.Object);
    }

    private async Task<SqliteConnection> OpenAsync(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task ApplyJournalMode_LocalPath_EngagesWalAsEffectiveMode()
    {
        var dbPath = Path.Combine(_dir, "local.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.IsNetworkPath.ShouldBeFalse();
        result.RequestedMode.ShouldBe("wal");
        result.EffectiveMode.ShouldBe("wal");
        result.Applied.ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyJournalMode_NetworkPath_FallsBackToDeleteJournaling()
    {
        var dbPath = Path.Combine(_dir, "network.db");
        var helper = CreateHelper(isNetwork: true);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.IsNetworkPath.ShouldBeTrue();
        result.RequestedMode.ShouldBe("delete");
        result.EffectiveMode.ShouldBe("delete");
        result.Applied.ShouldBeTrue();
        result.WalAutocheckpoint.ShouldBeNull();
    }

    [Fact]
    public async Task ApplyJournalMode_LocalPath_SetsConfiguredWalAutocheckpoint()
    {
        var dbPath = Path.Combine(_dir, "autocp.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        const int configured = 250;
        var result = await helper.ApplyJournalModeAsync(connection, dbPath, walAutocheckpoint: configured);

        result.WalAutocheckpoint.ShouldBe(configured);

        await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA wal_autocheckpoint;";
        var effective = Convert.ToInt32(await query.ExecuteScalarAsync());
        effective.ShouldBe(configured);
    }

    [Fact]
    public async Task ApplyJournalMode_DefaultAutocheckpoint_Is1000()
    {
        var dbPath = Path.Combine(_dir, "defaultcp.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.WalAutocheckpoint.ShouldBe(SqliteWalMaintenance.DefaultWalAutocheckpoint);
        SqliteWalMaintenance.DefaultWalAutocheckpoint.ShouldBe(1000);
    }

    [Fact]
    public void JournalModeResult_MismatchedEffectiveMode_SurfacesNotApplied()
    {
        // In-memory databases cannot enter WAL - SQLite reports "memory". Verify the result's
        // Applied flag correctly reports the mismatch that drives the warning log path.
        var result = new JournalModeResult(
            RequestedMode: "wal",
            EffectiveMode: "memory",
            IsNetworkPath: false,
            WalAutocheckpoint: 1000);

        result.Applied.ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyJournalMode_InMemoryConnection_ReportsMismatchWarningViaResult()
    {
        // A shared in-memory database ignores journal_mode=wal (stays "memory"), exercising the
        // real effective-mode verification path end-to-end and proving Applied == false.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var helper = CreateHelper(isNetwork: false);

        var result = await helper.ApplyJournalModeAsync(connection, "/tmp/in-memory-placeholder.db");

        result.RequestedMode.ShouldBe("wal");
        result.EffectiveMode.ShouldBe("memory");
        result.Applied.ShouldBeFalse();
    }

    [Fact]
    public async Task Checkpoint_Passive_ExecutesWithoutError()
    {
        var dbPath = Path.Combine(_dir, "cp-passive.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);
        await helper.ApplyJournalModeAsync(connection, dbPath);
        await WriteSomeDataAsync(connection);

        await Should.NotThrowAsync(() =>
            SqliteWalMaintenance.CheckpointAsync(connection, SqliteCheckpointMode.Passive));
    }

    [Fact]
    public async Task Checkpoint_Truncate_ExecutesWithoutError()
    {
        var dbPath = Path.Combine(_dir, "cp-truncate.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);
        await helper.ApplyJournalModeAsync(connection, dbPath);
        await WriteSomeDataAsync(connection);

        await Should.NotThrowAsync(() =>
            SqliteWalMaintenance.CheckpointAsync(connection, SqliteCheckpointMode.Truncate));
    }

    private static async Task WriteSomeDataAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS t (id INTEGER PRIMARY KEY, v TEXT);
            INSERT INTO t (v) VALUES ('a'), ('b'), ('c');
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
