namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// WAL checkpoint strategies exposed by <see cref="SqliteWalMaintenance.CheckpointAsync"/>.
/// Maps to SQLite's <c>PRAGMA wal_checkpoint(&lt;mode&gt;)</c> arguments.
/// </summary>
public enum SqliteCheckpointMode
{
    /// <summary>
    /// <c>PASSIVE</c> - checkpoint as many frames as possible without waiting for readers or
    /// writers. Never blocks; the <c>-wal</c> file is not truncated. Safe to call frequently
    /// (this is what the Step 3 periodic service will use for routine maintenance).
    /// </summary>
    Passive,

    /// <summary>
    /// <c>TRUNCATE</c> - like <c>RESTART</c> but also truncates the <c>-wal</c> file to zero
    /// bytes on success, reclaiming disk. May block briefly. Used for aggressive reclamation
    /// (e.g. on graceful shutdown or an explicit compaction request).
    /// </summary>
    Truncate,
}
