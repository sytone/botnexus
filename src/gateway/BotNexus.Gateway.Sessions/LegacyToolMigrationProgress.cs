using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

internal sealed record LegacyToolMigrationProgress(
    int Phase, long HistoryBoundary, long HistoryCursor, long InvocationBoundary,
    long CleanupCursor, long OrphanCursor)
{
    internal static LegacyToolMigrationProgress? Read(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT phase,history_boundary,history_cursor,invocation_boundary,cleanup_cursor,orphan_cursor
            FROM legacy_tool_migration WHERE id=1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5))
            : null;
    }

    internal static LegacyToolMigrationProgress ReadOrCreate(SqliteConnection connection, SqliteTransaction transaction)
    {
        var progress = Read(connection, transaction);
        if (progress is not null) return progress;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO legacy_tool_migration(id,history_boundary)
            VALUES(1,(SELECT COALESCE(MAX(id),0) FROM session_history))
            """;
        command.ExecuteNonQuery();
        return Read(connection, transaction)
            ?? throw new InvalidOperationException("Legacy tool migration checkpoint could not be created.");
    }

    internal void Save(SqliteConnection connection, SqliteTransaction transaction,
        int cleaned = 0, int protectedCount = 0, long clearedBytes = 0, int orphansDeleted = 0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE legacy_tool_migration SET phase=$phase,history_cursor=$historyCursor,
                invocation_boundary=$boundary,cleanup_cursor=$cleanupCursor,orphan_cursor=$orphanCursor,
                cleaned_invocations=cleaned_invocations+$cleaned,
                protected_invocations=protected_invocations+$protected,
                cleared_bytes=cleared_bytes+$bytes,
                orphaned_invocations_deleted=orphaned_invocations_deleted+$orphans
            WHERE id=1
            """;
        command.Parameters.AddWithValue("$phase", Phase);
        command.Parameters.AddWithValue("$historyCursor", HistoryCursor);
        command.Parameters.AddWithValue("$boundary", InvocationBoundary);
        command.Parameters.AddWithValue("$cleanupCursor", CleanupCursor);
        command.Parameters.AddWithValue("$orphanCursor", OrphanCursor);
        command.Parameters.AddWithValue("$cleaned", cleaned);
        command.Parameters.AddWithValue("$protected", protectedCount);
        command.Parameters.AddWithValue("$bytes", clearedBytes);
        command.Parameters.AddWithValue("$orphans", orphansDeleted);
        command.ExecuteNonQuery();
    }

    internal static (long LastId, int Count) ReadInvocationWindow(SqliteConnection connection, SqliteTransaction transaction,
        long afterId, long boundary, int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(id),$after),COUNT(*) FROM (
                SELECT id FROM tool_invocations WHERE id>$after AND id<=$boundary ORDER BY id LIMIT $limit)
            """;
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$boundary", boundary);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Legacy tool migration window could not be read.");
        return (reader.GetInt64(0), reader.GetInt32(1));
    }
}
