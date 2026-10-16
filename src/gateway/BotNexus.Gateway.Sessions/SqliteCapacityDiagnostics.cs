using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

/// <summary>Payload-free observations, not a causal attribution of SQLITE_FULL.</summary>
public sealed record SqliteCapacityObservation(
    long? PageCount, long? MaxPageCount, long? PageSize, long? FreeListCount,
    long? TempStore, long? MainVolumeAvailableBytes, long? TempVolumeAvailableBytes)
{
    /// <summary>Returns observations captured before the failing connection was disposed.</summary>
    public static SqliteCapacityObservation? FromFailure(SqliteException exception) => SqliteCapacityDiagnostics.Get(exception);

    /// <summary>Independent observed constraints; none proves the failed operation's cause.</summary>
    public bool PageLimitObserved => PageCount.HasValue && MaxPageCount > 0 && PageCount >= MaxPageCount;
    /// <summary>Whether the main volume snapshot reported no available bytes.</summary>
    public bool MainVolumeFullObserved => MainVolumeAvailableBytes == 0;
    /// <summary>Whether the explicitly configured temp volume reported no available bytes.</summary>
    public bool TempVolumeFullObserved => TempVolumeAvailableBytes == 0;
}

internal static class SqliteCapacityDiagnostics
{
    private const string ObservationKey = "BotNexus.SqliteCapacityObservation";

    public static SqliteCapacityObservation? Get(SqliteException exception) =>
        exception.Data[ObservationKey] as SqliteCapacityObservation;

    // Called by exception filters before the failing connection/transaction is disposed.
    // Always returns false: the original exception and stack propagate unchanged.
    public static bool ObserveFailure(SqliteConnection connection, SqliteException exception)
    {
        if (exception.SqliteErrorCode != 13) return false;
        try { exception.Data[ObservationKey] = Capture(connection); }
        catch (Exception) { /* Best effort must never replace the original failure. */ }
        return false;
    }

    internal static SqliteCapacityObservation Capture(SqliteConnection connection,
        Func<string, long?>? availableBytes = null)
    {
        availableBytes ??= AvailableBytes;
        var pageCount = ReadPragma(connection, "PRAGMA main.page_count");
        var maxPageCount = ReadPragma(connection, "PRAGMA main.max_page_count");
        var pageSize = ReadPragma(connection, "PRAGMA main.page_size");
        var freeListCount = ReadPragma(connection, "PRAGMA main.freelist_count");
        var tempStore = ReadPragma(connection, "PRAGMA temp_store");
        long? mainBytes = null;
        long? tempBytes = null;
        try
        {
            // DataSource on the already-open connection reflects the actual main database.
            var source = connection.DataSource;
            if (!string.IsNullOrWhiteSpace(source) && source != ":memory:")
                mainBytes = availableBytes(Path.GetFullPath(source));
        }
        catch (Exception) { }
        try
        {
            // SQLite can choose among platform-specific temp directories. Only an explicit
            // SQLite temp directory establishes which volume is in use. Never guess from TMP.
            if (tempStore != 2)
            {
                using var command = connection.CreateCommand();
                command.CommandTimeout = 1;
                command.CommandText = "PRAGMA temp_store_directory";
                if (command.ExecuteScalar() is string directory && !string.IsNullOrWhiteSpace(directory))
                    tempBytes = availableBytes(Path.GetFullPath(directory));
            }
        }
        catch (Exception) { }
        return new(pageCount, maxPageCount, pageSize, freeListCount, tempStore, mainBytes, tempBytes);
    }

    private static long? ReadPragma(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is null ? null : Convert.ToInt64(value);
        }
        catch (Exception) { return null; }
    }

    private static long? AvailableBytes(string path)
    {
        // Resolve the containing mount, not simply '/' (Linux may mount the DB separately).
        // No directory enumeration or file reads; drive metadata only, no paths escape.
        var drive = DriveInfo.GetDrives()
            .Where(d => (path.Equals(d.Name.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                path.StartsWith(Path.EndsInDirectorySeparator(d.Name) ? d.Name : d.Name + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return drive?.AvailableFreeSpace;
    }
}
