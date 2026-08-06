using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests.TestInfrastructure;

/// <summary>
/// Releases the pooled SQLite handles for ONE database file so its directory can be deleted,
/// without disturbing any other test's connections.
/// </summary>
/// <remarks>
/// <para>
/// Test teardown reaches for <c>SqliteConnection.ClearAllPools()</c> because it reliably frees
/// the file lock. The catch is that it is process-global: with <c>parallelizeTestCollections</c>
/// enabled - which is the default across this repo - a test finishing teardown disposes the
/// pooled native handles belonging to every other test currently executing a query.
/// </para>
/// <para>
/// The victim then throws <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> from the middle of
/// unrelated work, so the failure names a test that did nothing wrong and cannot be reproduced
/// by running that test alone. It was measured on 2026-08-06 as a 40% failure rate across five
/// identical parallel container runs. Clearing only the pool for the connection string under
/// test keeps the cleanup guarantee and removes the cross-test blast radius.
/// </para>
/// </remarks>
internal static class SqlitePoolCleanup
{
    /// <summary>Clears the pooled connections for <paramref name="dbPath"/> only.</summary>
    public static void ClearPoolFor(string dbPath)
    {
        using var owned = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        SqliteConnection.ClearPool(owned);
    }
}
