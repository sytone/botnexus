using Microsoft.Data.Sqlite;

namespace BotNexus.Testing;

/// <summary>
/// Releases the pooled SQLite handles for ONE database file so its directory can be deleted,
/// without disturbing any other test's connections (#3324, #3392).
/// </summary>
/// <remarks>
/// <para>
/// Test teardown reaches for <c>SqliteConnection.ClearAllPools()</c> because it reliably frees the
/// file lock. The catch is that it is <b>process-global</b>: test collections run in parallel by
/// default across this repo, so a test finishing teardown disposes the pooled native
/// <c>SQLitePCL.sqlite3</c> handles belonging to every other test currently executing a query.
/// </para>
/// <para>
/// The victim then throws <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> from
/// <c>sqlite3_prepare_v2</c> in the middle of unrelated work, so the failure names a test that did
/// nothing wrong and cannot be reproduced by running that test alone. That is the signature reported
/// for <c>CronJobLifecycleTests.OneShotRemoval_DoesNotDependOnThePromptAskingForIt</c> in #3324 and
/// again for <c>SqliteMemoryStoreVectorScanTruncationTests</c> in #3392 - the latter reddening the
/// CI of a pull request that touched no memory code at all. Clearing only the pools for the database
/// under test keeps the cleanup guarantee and removes the cross-test blast radius.
/// </para>
/// <para>
/// This type has exactly one definition. Projects that need it link this file in via
/// <c>&lt;Compile Include="..\..\BotNexus.Testing\SqlitePoolCleanup.cs" /&gt;</c> rather than keeping
/// a per-project copy, so the reasoning above cannot drift between projects.
/// </para>
/// </remarks>
internal static class SqlitePoolCleanup
{
    /// <summary>
    /// Clears the pooled connections for <paramref name="dbPath"/> only.
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite keys its pool groups on the <b>verbatim connection string</b>, not on
    /// the resolved file (see <c>SqliteConnectionFactory.GetPoolGroup</c>, which uses the string as
    /// the dictionary key). A database opened both as <c>Data Source=x</c> - the shape ad-hoc test
    /// seeding connections use - and as <c>Data Source=x;Mode=ReadWriteCreate</c> - the shape the
    /// production stores use - therefore occupies two distinct pools. Every shape observed in this
    /// repository is cleared here; clearing only one leaves a live handle holding the file lock and
    /// the subsequent directory delete fails.
    /// </remarks>
    public static void ClearPoolFor(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        foreach (var connectionString in ConnectionStringVariants(dbPath))
        {
            ClearPoolForConnectionString(connectionString);
        }
    }

    /// <summary>
    /// Clears the pooled connections for one exact connection string.
    /// </summary>
    /// <remarks>
    /// Use this when the test already holds the very string it opened connections with; it is exact
    /// rather than heuristic, so it is preferred over <see cref="ClearPoolFor(string)"/> where
    /// available. A connection string with <c>Pooling=False</c> never created a pool group in the
    /// first place, so clearing it is a harmless no-op rather than a special case to guard.
    /// </remarks>
    public static void ClearPoolForConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        using var owned = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(owned);
    }

    /// <summary>
    /// Clears the pooled connections for every SQLite database file beneath <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// For test classes whose teardown deletes a whole temp directory rather than one known file:
    /// the set of databases created is decided by the individual test methods, so the directory is
    /// the only thing teardown can name. This is still scoped - it touches only pools for files this
    /// test's own unique temp directory contains - and so keeps the property that no sibling test's
    /// live handle is disposed. A directory that no longer exists is a no-op, because teardown may
    /// run after a test already cleaned up.
    /// </remarks>
    public static void ClearPoolsUnder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            // -wal and -shm siblings are cleared via their base database file, not on their own.
            if (file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal))
                continue;

            ClearPoolFor(file);
        }
    }

    /// <summary>
    /// Enumerates the connection-string spellings this repository opens a given database file with.
    /// </summary>
    /// <remarks>
    /// Both the raw path as written by the caller and its fully-qualified form are covered, because
    /// a relative and an absolute spelling of the same file are different dictionary keys and hence
    /// different pool groups.
    /// </remarks>
    private static IEnumerable<string> ConnectionStringVariants(string dbPath)
    {
        var paths = new List<string> { dbPath };
        try
        {
            var full = Path.GetFullPath(dbPath);
            if (!string.Equals(full, dbPath, StringComparison.Ordinal))
                paths.Add(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path we cannot canonicalise is one no store could have opened either; the raw
            // spelling is still cleared below.
        }

        foreach (var path in paths)
        {
            yield return $"Data Source={path}";
            yield return $"Data Source={path};Mode=ReadWriteCreate";
            yield return $"Data Source={path};Mode=ReadOnly";
            yield return new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            yield return new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }
    }
}
