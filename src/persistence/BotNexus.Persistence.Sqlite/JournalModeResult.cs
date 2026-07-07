namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Outcome of applying journal-mode maintenance to a SQLite connection. Returned so callers
/// that do not inject an <c>ILogger</c> can still react to a mode mismatch programmatically.
/// </summary>
/// <param name="RequestedMode">The journal mode that was requested (e.g. <c>wal</c> or <c>delete</c>).</param>
/// <param name="EffectiveMode">The journal mode SQLite reported after the PRAGMA, lower-cased
/// (result of <c>PRAGMA journal_mode;</c>).</param>
/// <param name="IsNetworkPath"><c>true</c> when the database path was detected as a network mount,
/// which is why rollback journaling was requested instead of WAL.</param>
/// <param name="WalAutocheckpoint">The <c>wal_autocheckpoint</c> page threshold applied when WAL was
/// requested; <c>null</c> when WAL was not requested (network path).</param>
public readonly record struct JournalModeResult(
    string RequestedMode,
    string EffectiveMode,
    bool IsNetworkPath,
    int? WalAutocheckpoint)
{
    /// <summary>
    /// <c>true</c> when the effective journal mode matches what was requested. When <c>false</c>
    /// the PRAGMA did not take (common on unexpected network filesystems where WAL silently
    /// falls back), and callers should expect degraded concurrency behaviour.
    /// </summary>
    public bool Applied => string.Equals(RequestedMode, EffectiveMode, StringComparison.OrdinalIgnoreCase);
}
