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

    [Fact]
    public async Task ApplyJournalMode_LocalPath_AppliesDefaultJournalSizeLimitOf64MiB()
    {
        var dbPath = Path.Combine(_dir, "jsl-default.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.JournalSizeLimitBytes.ShouldBe(SqliteWalMaintenance.DefaultJournalSizeLimitBytes);
        SqliteWalMaintenance.DefaultJournalSizeLimitBytes.ShouldBe(64L * 1024 * 1024);

        // Read the pragma back so this asserts SQLite actually took the setting, not just that
        // the helper echoed its own argument into the result.
        (await QueryJournalSizeLimitAsync(connection))
            .ShouldBe(SqliteWalMaintenance.DefaultJournalSizeLimitBytes);
    }

    [Fact]
    public async Task ApplyJournalMode_LocalPath_AppliesConfiguredJournalSizeLimit()
    {
        var dbPath = Path.Combine(_dir, "jsl-configured.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        const long configured = 8L * 1024 * 1024;
        var result = await helper.ApplyJournalModeAsync(
            connection, dbPath, journalSizeLimitBytes: configured);

        result.JournalSizeLimitBytes.ShouldBe(configured);
        (await QueryJournalSizeLimitAsync(connection)).ShouldBe(configured);
    }

    [Fact]
    public async Task ApplyJournalMode_NegativeOneLimit_DisablesTheBoundAndIsSurfaced()
    {
        var dbPath = Path.Combine(_dir, "jsl-unlimited.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(
            connection, dbPath, journalSizeLimitBytes: SqliteWalMaintenance.UnlimitedJournalSizeLimit);

        result.JournalSizeLimitBytes.ShouldBe(SqliteWalMaintenance.UnlimitedJournalSizeLimit);
        (await QueryJournalSizeLimitAsync(connection))
            .ShouldBe(SqliteWalMaintenance.UnlimitedJournalSizeLimit);
    }

    [Fact]
    public async Task ApplyJournalMode_NetworkPath_DoesNotApplyJournalSizeLimit()
    {
        var dbPath = Path.Combine(_dir, "jsl-network.db");
        var helper = CreateHelper(isNetwork: true);
        await using var connection = await OpenAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.JournalSizeLimitBytes.ShouldBeNull();
    }

    [Fact]
    public async Task ApplyJournalMode_InvalidNegativeJournalSizeLimit_Throws()
    {
        var dbPath = Path.Combine(_dir, "jsl-invalid.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            helper.ApplyJournalModeAsync(connection, dbPath, journalSizeLimitBytes: -2));
    }

    [Fact]
    public async Task ApplyJournalMode_BoundedLimit_ShrinksWalBackToTheBoundAfterCheckpoint()
    {
        // End-to-end proof of the bug in #2370: with a bounded journal_size_limit, a WAL that
        // grew past the bound is truncated back to it when a checkpoint resets the log, instead
        // of staying parked at its high-water mark for the life of the process.
        var dbPath = Path.Combine(_dir, "jsl-shrink.db");
        var helper = CreateHelper(isNetwork: false);
        await using var connection = await OpenAsync(dbPath);

        const long limit = 32 * 1024;
        await helper.ApplyJournalModeAsync(
            connection,
            dbPath,
            walAutocheckpoint: 0, // disable autocheckpoint so the WAL is free to balloon first
            journalSizeLimitBytes: limit);

        await GrowWalAsync(connection);
        var walPath = dbPath + "-wal";
        new FileInfo(walPath).Length.ShouldBeGreaterThan(limit);

        await SqliteWalMaintenance.CheckpointAsync(connection, SqliteCheckpointMode.Passive);
        // journal_size_limit is enforced when the WAL resets on the next commit after a full
        // checkpoint, so drive one more small write.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO big (v) VALUES ('tail');";
            await cmd.ExecuteNonQueryAsync();
        }

        new FileInfo(walPath).Refresh();
        new FileInfo(walPath).Length.ShouldBeLessThanOrEqualTo(limit);
    }

    private static async Task<long> QueryJournalSizeLimitAsync(SqliteConnection connection)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA journal_size_limit;";
        return Convert.ToInt64(await query.ExecuteScalarAsync());
    }

    private static async Task GrowWalAsync(SqliteConnection connection)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS big (id INTEGER PRIMARY KEY, v TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        var payload = new string('x', 4096);
        for (var i = 0; i < 200; i++)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO big (v) VALUES ($v);";
            cmd.Parameters.AddWithValue("$v", payload);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
