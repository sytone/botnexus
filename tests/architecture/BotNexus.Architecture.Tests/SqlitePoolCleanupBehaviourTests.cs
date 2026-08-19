using System.Data;
using BotNexus.Testing;
using Microsoft.Data.Sqlite;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Behavioural coverage for <see cref="SqlitePoolCleanup"/>: it must actually release the pooled
/// handle for the database it is given, and must NOT touch any other database's pool (#3392).
/// </summary>
/// <remarks>
/// <para>
/// The source-level fence in <c>SqlitePoolCleanupFenceTests</c> proves nobody calls the banned
/// global API. That is a necessary but insufficient claim: deleting every cleanup call would also
/// satisfy it. These tests supply the other half - that scoped cleanup still HAPPENS - so the fix
/// cannot degenerate into "we removed the teardown". If <c>ClearPoolFor</c> is emptied out, the
/// release test below fails.
/// </para>
/// <para>
/// The observable used is the Windows file lock: a pooled SQLite handle keeps the database file
/// open, so the file cannot be deleted until the pool releases it. That is the exact guarantee test
/// teardown needed from <c>ClearAllPools()</c> in the first place, so it is the right property to
/// assert rather than a proxy for it.
/// </para>
/// </remarks>
public sealed class SqlitePoolCleanupBehaviourTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-pool-cleanup-").FullName;

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering handle on Windows must not fail the test.
        }
    }

    [Fact]
    public void ClearPoolFor_ReleasesThePooledHandle_SoTheFileCanBeDeleted()
    {
        var dbPath = Path.Combine(_directory, "released.db");
        OpenAndReturnToPool(dbPath);

        SqlitePoolCleanup.ClearPoolFor(dbPath);

        // The load-bearing assertion: with the handle released the file delete succeeds. This is
        // what fails if ClearPoolFor stops doing any work, which is what makes the #3392 fix
        // non-vacuous - removing ClearAllPools() alone would leave this red.
        Should.NotThrow(() => File.Delete(dbPath));
        File.Exists(dbPath).ShouldBeFalse();
    }

    [Fact]
    public void ClearPoolFor_LeavesAnotherDatabasesPooledConnection_Usable()
    {
        var victimPath = Path.Combine(_directory, "victim.db");
        var targetPath = Path.Combine(_directory, "target.db");

        // A live, OPEN connection on the victim - exactly the state a sibling test is in when it is
        // mid-query and another test's teardown fires.
        using var victim = new SqliteConnection($"Data Source={victimPath}");
        victim.Open();
        Execute(victim, "CREATE TABLE t (id INTEGER PRIMARY KEY);");

        OpenAndReturnToPool(targetPath);

        SqlitePoolCleanup.ClearPoolFor(targetPath);

        // The regression itself: under ClearAllPools() the victim's native handle is disposed and
        // this query throws ObjectDisposedException: 'SQLitePCL.sqlite3'.
        victim.State.ShouldBe(ConnectionState.Open);
        Should.NotThrow(() => Execute(victim, "INSERT INTO t (id) VALUES (1);"));
        ScalarLong(victim, "SELECT COUNT(*) FROM t;").ShouldBe(1);
    }

    [Fact]
    public void ClearPoolsUnder_ReleasesEveryDatabaseInTheDirectory()
    {
        var first = Path.Combine(_directory, "first.db");
        var second = Path.Combine(_directory, "nested", "second.db");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        OpenAndReturnToPool(first);
        OpenAndReturnToPool(second);

        SqlitePoolCleanup.ClearPoolsUnder(_directory);

        Should.NotThrow(() => File.Delete(first));
        Should.NotThrow(() => File.Delete(second));
    }

    [Fact]
    public void ClearPoolsUnder_OnAMissingDirectory_IsANoOp()
    {
        var missing = Path.Combine(_directory, "never-created");

        // Teardown may run after a test already removed its own directory; that must not throw and
        // mask the real failure the test was reporting.
        Should.NotThrow(() => SqlitePoolCleanup.ClearPoolsUnder(missing));
    }

    [Fact]
    public void ClearPoolForConnectionString_ReleasesTheExactStringItWasGiven()
    {
        var dbPath = Path.Combine(_directory, "exact.db");
        var connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate";
        OpenAndReturnToPool(dbPath, connectionString);

        SqlitePoolCleanup.ClearPoolForConnectionString(connectionString);

        Should.NotThrow(() => File.Delete(dbPath));
    }

    /// <summary>
    /// Opens a pooled connection, creates a table so the file really exists on disk, then closes it
    /// so the underlying handle is RETURNED TO THE POOL rather than destroyed.
    /// </summary>
    /// <remarks>
    /// Closing a <see cref="SqliteConnection"/> does not close the native handle when pooling is on
    /// - that is the whole reason teardown needs an explicit pool clear. Without this step the tests
    /// above would pass even with a do-nothing helper, because there would be no retained handle to
    /// release.
    /// </remarks>
    private static void OpenAndReturnToPool(string dbPath, string? connectionString = null)
    {
        using var connection = new SqliteConnection(connectionString ?? $"Data Source={dbPath}");
        connection.Open();
        Execute(connection, "CREATE TABLE IF NOT EXISTS seed (id INTEGER PRIMARY KEY);");
        connection.Close();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
