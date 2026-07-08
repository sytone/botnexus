namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Abstraction over "is this database path on a network-mounted filesystem" so the
/// journal-mode decision in <see cref="SqliteWalMaintenance"/> is deterministically
/// unit-testable. SQLite's WAL mode requires shared-memory (mmap) support that is
/// unreliable or unavailable on NFS/SMB/UNC mounts, so a store on such a path must
/// fall back to rollback journaling (<c>journal_mode=DELETE</c>).
/// </summary>
public interface INetworkPathDetector
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> resolves to a network mount
    /// (Windows UNC or a mapped network drive, or a POSIX NFS/SMB/CIFS mount). A best-effort
    /// heuristic: implementations should prefer a false negative (treat as local) over
    /// throwing, because a wrong "local" answer merely keeps WAL - the effective-mode
    /// verification in <see cref="SqliteWalMaintenance"/> is the ultimate safety net.
    /// </summary>
    bool IsNetworkPath(string path);
}
