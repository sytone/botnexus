using Microsoft.Data.Sqlite;
using Moq;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Disposal-race coverage for <see cref="SqliteWalMaintenance"/> (#3124). A managed
/// <see cref="SqliteConnection"/> can report <c>Open</c> while its native <c>SQLitePCL.sqlite3</c>
/// handle has already been released, and every pragma this helper issues used to prepare a
/// statement against that dead handle and throw <see cref="ObjectDisposedException"/> out of store
/// initialisation. These tests drive the released-handle path <b>deterministically</b> - by
/// disposing the connection - rather than hoping to win a timing race under parallel load.
/// </summary>
public sealed class SqliteWalMaintenanceDisposalTests : IDisposable
{
    private readonly string _dir;

    public SqliteWalMaintenanceDisposalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-wal-disposal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_dir);
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

    private static SqliteWalMaintenance CreateHelper(bool isNetwork = false)
    {
        var detector = new Mock<INetworkPathDetector>();
        detector.Setup(d => d.IsNetworkPath(It.IsAny<string>())).Returns(isNetwork);
        return new SqliteWalMaintenance(detector.Object);
    }

    /// <summary>
    /// Opens a connection and then releases its native handle, leaving the managed object in the
    /// state the #3124 stack observed: a connection whose handle is gone.
    /// </summary>
    private static async Task<SqliteConnection> OpenThenReleaseHandleAsync(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        connection.Handle.ShouldNotBeNull("the handle must be live before it is released, or the test proves nothing");
        await connection.DisposeAsync();
        return connection;
    }

    [Fact]
    public async Task ApplyJournalMode_ReleasedHandle_DoesNotThrowObjectDisposedException()
    {
        var dbPath = Path.Combine(_dir, "released-handle.db");
        var helper = CreateHelper();
        var connection = await OpenThenReleaseHandleAsync(dbPath);

        var applyTask = helper.ApplyJournalModeAsync(connection, dbPath);
        await Should.NotThrowAsync(() => applyTask);
        var result = await applyTask;

        // Nothing was applied, because nothing could be.
        result.WalAutocheckpoint.ShouldBeNull();
        result.JournalSizeLimitBytes.ShouldBeNull();
    }

    [Fact]
    public async Task ApplyJournalMode_ReleasedHandle_DoesNotReportAnUnverifiedModeAsApplied()
    {
        // The effective-mode re-read is the helper's *verification* step. When the handle is gone
        // that verification cannot run, and the caller must not be told the mode took.
        var dbPath = Path.Combine(_dir, "unverified.db");
        var helper = CreateHelper();
        var connection = await OpenThenReleaseHandleAsync(dbPath);

        var result = await helper.ApplyJournalModeAsync(connection, dbPath);

        result.RequestedMode.ShouldBe("wal");
        result.EffectiveMode.ShouldBeEmpty();
        result.Applied.ShouldBeFalse();
    }

    [Fact]
    public async Task Checkpoint_ReleasedHandle_DoesNotThrowObjectDisposedException()
    {
        var dbPath = Path.Combine(_dir, "checkpoint-released.db");
        var connection = await OpenThenReleaseHandleAsync(dbPath);

        await Should.NotThrowAsync(() => SqliteWalMaintenance.CheckpointAsync(connection));
    }

    [Fact]
    public async Task ApplyJournalMode_NonDisposalPragmaFailure_StillPropagates()
    {
        // AC3: only ObjectDisposedException is swallowed. SQLite refuses to switch into WAL from
        // inside an open transaction, which is a genuine failure travelling through the very same
        // shared pragma seam - a blanket catch would silently eat it.
        var dbPath = Path.Combine(_dir, "propagates.db");
        var helper = CreateHelper();
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t(x);";
            await create.ExecuteNonQueryAsync();
        }

        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN;";
            await begin.ExecuteNonQueryAsync();
        }

        await Should.ThrowAsync<SqliteException>(() => helper.ApplyJournalModeAsync(connection, dbPath));
    }
}
