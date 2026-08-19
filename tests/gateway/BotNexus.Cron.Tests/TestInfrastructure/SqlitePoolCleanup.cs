using Microsoft.Data.Sqlite;

namespace BotNexus.Cron.Tests.TestInfrastructure;

/// <summary>
/// Releases the pooled SQLite handles for ONE database file so its directory can be deleted,
/// without disturbing any other test's connections (#3324).
/// </summary>
/// <remarks>
/// <para>
/// Test teardown reaches for <c>SqliteConnection.ClearAllPools()</c> because it reliably frees the
/// file lock. The catch is that it is <b>process-global</b>: this assembly runs with
/// <c>parallelizeTestCollections: true</c>, so a test finishing teardown disposes the pooled native
/// <c>SQLitePCL.sqlite3</c> handles belonging to every other test currently executing a query.
/// </para>
/// <para>
/// The victim then throws <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> from
/// <c>sqlite3_prepare_v2</c> in the middle of unrelated work, so the failure names a test that did
/// nothing wrong and cannot be reproduced by running that test alone - which is exactly the
/// signature reported for <c>CronJobLifecycleTests.OneShotRemoval_DoesNotDependOnThePromptAskingForIt</c>
/// in #3324. Clearing only the pool for the database under test keeps the cleanup guarantee and
/// removes the cross-test blast radius. The same fix was applied to
/// <c>BotNexus.Memory.Tests</c> on 2026-08-06.
/// </para>
/// </remarks>
internal static class SqlitePoolCleanup
{
    /// <summary>
    /// Clears the pooled connections for <paramref name="dbPath"/> only.
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite keys its connection pool on the <b>connection string</b>, not the
    /// resolved file, so a database opened both as <c>Data Source=x</c> (the ad-hoc seeding
    /// connections in these tests) and as <c>Data Source=x;Mode=ReadWriteCreate</c> (what
    /// <c>SqliteCronStore</c> uses) occupies two distinct pools. Both are cleared here; clearing
    /// only one leaves a live handle holding the file lock and the directory delete fails.
    /// </remarks>
    public static void ClearPoolFor(string dbPath)
    {
        foreach (var connectionString in new[] { $"Data Source={dbPath}", $"Data Source={dbPath};Mode=ReadWriteCreate" })
        {
            using var owned = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(owned);
        }
    }
}
