using System.IO.Abstractions;

namespace BotNexus.Memory;

/// <summary>
/// The single decision behind <see cref="IMemoryStoreFactory.StoreLocationExists"/>.
/// </summary>
/// <remarks>
/// <para>
/// #2608 established that a sub-agent workspace reaped by the sweeper (#2237) must be skipped
/// before SQLite is reached, because <c>SQLITE_CANTOPEN</c> is permanently unrecoverable and
/// retrying it only burns the <see cref="SqliteRetryHelper"/> budget on an unactionable error.
/// </para>
/// <para>
/// #3542: both factories previously answered <c>true</c> whenever they held a cached store
/// instance, on the reasoning that "an already-created store is live regardless of what the
/// filesystem looks like now". That holds for a long-lived agent whose directory still exists —
/// and in that case the filesystem probe below already answers <c>true</c> — but it is false
/// precisely for the reaped sub-agent this guard exists to catch: its store was created and
/// cached, and only THEN was its directory swept. A cached instance cannot resurrect a deleted
/// directory, so the cache is no longer allowed to override the filesystem. Both factories route
/// through this one method so the two can never diverge.
/// </para>
/// </remarks>
public static class MemoryStoreLocationProbe
{
    /// <summary>
    /// Answers whether the store at <paramref name="dbPath"/> has a location that could be opened.
    /// </summary>
    public static bool Exists(IFileSystem fileSystem, string dbPath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // The store creates its own immediate directory (typically "data") on initialize,
        // so the agent's own directory one level above is the meaningful existence signal:
        // when the sweeper reaps a sub-agent it removes that whole directory (#2608).
        var storeDirectory = fileSystem.Path.GetDirectoryName(dbPath);

        // A store with no probeable directory (":memory:" and friends) has nothing on disk to
        // reap; it is live by construction, cached or not.
        if (string.IsNullOrEmpty(storeDirectory))
            return true;

        if (fileSystem.File.Exists(dbPath))
            return true;

        if (fileSystem.Directory.Exists(storeDirectory))
            return true;

        var agentDirectory = fileSystem.Path.GetDirectoryName(storeDirectory);
        return !string.IsNullOrEmpty(agentDirectory) && fileSystem.Directory.Exists(agentDirectory);
    }
}
