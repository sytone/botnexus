namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Registration seam that SQLite stores (or their DI wiring) opt into so the
/// <see cref="SqliteWalCheckpointHostedService"/> can enumerate the live set of database files
/// to checkpoint (#1438). This is preferred over a static config list because the actual
/// on-disk paths are resolved at runtime from <c>BOTNEXUS_DATA_DIR</c> / connection strings,
/// and several stores share a single database file - the registry de-duplicates them so a
/// shared file is only checkpointed once per sweep.
/// </summary>
public interface ISqliteDatabaseRegistry
{
    /// <summary>
    /// Registers a SQLite database file path for periodic checkpointing. Idempotent:
    /// registering the same resolved path more than once (e.g. from multiple stores that
    /// share one file) records it a single time. Null/empty paths are ignored.
    /// </summary>
    void Register(string databasePath);

    /// <summary>
    /// Returns a snapshot of the distinct registered database file paths. Safe to enumerate
    /// concurrently with <see cref="Register"/>.
    /// </summary>
    IReadOnlyCollection<string> GetDatabasePaths();
}
