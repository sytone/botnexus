using Microsoft.Data.Sqlite;

namespace BotNexus.Memory;

/// <summary>
/// Provides simple retry logic for transient SQLite errors (database locked, busy, I/O errors).
/// Used by read operations that do not hold the write lock and can transiently fail under load.
/// </summary>
internal static class SqliteRetryHelper
{
    private const int DefaultMaxAttempts = 3;
    private static readonly int[] BackoffMs = [50, 200];

    /// <summary>
    /// Executes an async operation with retry on transient SQLite failures.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransient(ex) && attempt < maxAttempts - 1)
            {
                await Task.Delay(BackoffMs[Math.Min(attempt, BackoffMs.Length - 1)], ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>
    /// Determines whether a SQLite exception is transient and worth retrying.
    /// </summary>
    internal static bool IsTransient(SqliteException ex)
    {
        // SQLite error codes for transient failures:
        // 5 = SQLITE_BUSY (database is locked by another connection)
        // 6 = SQLITE_LOCKED (table-level lock within same connection — rare for reads)
        // 10 = SQLITE_IOERR (I/O error — may be transient under heavy disk load)
        // 11 = SQLITE_CORRUPT is NOT transient
        return ex.SqliteErrorCode is 5 or 6 or 10;
    }
}
