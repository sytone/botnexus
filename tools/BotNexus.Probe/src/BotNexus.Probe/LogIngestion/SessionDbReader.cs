using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

namespace BotNexus.Probe.LogIngestion;

public sealed class SessionDbReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private bool _disposed;

    public SessionDbReader(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        };

        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();

        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
    }

    public async Task<List<SessionSummary>> ListSessionsAsync(
        string? agentId = null,
        string? channelType = null,
        string? sessionType = null,
        string? status = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
        => await ExecuteReadAsync(async token =>
        {
            var items = new List<SessionSummary>();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT s.id,
                       s.agent_id,
                       s.channel_type,
                       s.session_type,
                       s.status,
                       s.caller_id,
                       s.created_at,
                       s.updated_at,
                       COALESCE(h.message_count, 0) AS message_count
                FROM sessions s
                LEFT JOIN (
                    SELECT session_id, COUNT(*) AS message_count
                    FROM session_history
                    GROUP BY session_id
                ) h ON h.session_id = s.id
                WHERE (@agentId IS NULL OR s.agent_id = @agentId)
                  AND (@channelType IS NULL OR s.channel_type = @channelType)
                  AND (@sessionType IS NULL OR s.session_type = @sessionType)
                  AND (@status IS NULL OR s.status = @status)
                ORDER BY COALESCE(s.updated_at, s.created_at) DESC, s.id
                LIMIT @take OFFSET @skip;
                """;
            command.Parameters.AddWithValue("@agentId", (object?)agentId ?? DBNull.Value);
            command.Parameters.AddWithValue("@channelType", (object?)channelType ?? DBNull.Value);
            command.Parameters.AddWithValue("@sessionType", (object?)sessionType ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("@skip", Math.Max(0, skip));
            command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 1_000));

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new SessionSummary(
                    reader.GetString(0),
                    GetNullableString(reader, 1),
                    GetNullableString(reader, 2),
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    ParseDateTimeOffset(GetNullableString(reader, 6)),
                    ParseDateTimeOffset(GetNullableString(reader, 7)),
                    reader.GetInt32(8)));
            }

            return items;
        }, ct);

    public async Task<SessionDetail?> GetSessionAsync(string sessionId, CancellationToken ct = default)
        => await ExecuteReadAsync(async token =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id,
                       agent_id,
                       channel_type,
                       session_type,
                       status,
                       caller_id,
                       participants_json,
                       metadata,
                       created_at,
                       updated_at
                FROM sessions
                WHERE id = @sessionId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@sessionId", sessionId);

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            return new SessionDetail(
                reader.GetString(0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                ParseDateTimeOffset(GetNullableString(reader, 8)),
                ParseDateTimeOffset(GetNullableString(reader, 9)));
        }, ct);

    public async Task<List<SessionHistoryEntry>> GetHistoryAsync(
        string sessionId,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
        => await ExecuteReadAsync(async token =>
        {
            var items = new List<SessionHistoryEntry>();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id,
                       session_id,
                       role,
                       content,
                       timestamp,
                       tool_name,
                       tool_call_id,
                       is_compaction_summary
                FROM session_history
                WHERE session_id = @sessionId
                ORDER BY id
                LIMIT @take OFFSET @skip;
                """;
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.Parameters.AddWithValue("@skip", Math.Max(0, skip));
            command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 1_000));

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new SessionHistoryEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    GetNullableString(reader, 3),
                    ParseDateTimeOffset(GetNullableString(reader, 4)),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    !reader.IsDBNull(7) && reader.GetInt32(7) == 1));
            }

            return items;
        }, ct);

    public async Task<SessionCounts> GetCountsAsync(CancellationToken ct = default)
        => await ExecuteReadAsync(async token =>
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) AS total,
                       SUM(CASE WHEN status = 'Active' THEN 1 ELSE 0 END) AS active,
                       SUM(CASE WHEN status = 'Sealed' THEN 1 ELSE 0 END) AS sealed,
                       SUM(CASE WHEN status = 'Expired' THEN 1 ELSE 0 END) AS expired,
                       SUM(CASE WHEN status = 'Suspended' THEN 1 ELSE 0 END) AS suspended
                FROM sessions;
                """;

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return new SessionCounts(0, 0, 0, 0, 0);
            }

            return new SessionCounts(
                GetNullableInt(reader, 0),
                GetNullableInt(reader, 1),
                GetNullableInt(reader, 2),
                GetNullableInt(reader, 3),
                GetNullableInt(reader, 4));
        }, ct);

    public async Task<List<SessionHistoryEntry>> SearchHistoryAsync(
        string query,
        string? sessionId = null,
        int take = 100,
        CancellationToken ct = default)
        => await ExecuteReadAsync(async token =>
        {
            var items = new List<SessionHistoryEntry>();
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id,
                       session_id,
                       role,
                       content,
                       timestamp,
                       tool_name,
                       tool_call_id,
                       is_compaction_summary
                FROM session_history
                WHERE (@sessionId IS NULL OR session_id = @sessionId)
                  AND content LIKE @query ESCAPE '\'
                ORDER BY COALESCE(timestamp, '') DESC, id DESC
                LIMIT @take;
                """;
            command.Parameters.AddWithValue("@sessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@query", $"%{EscapeLike(query)}%");
            command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 1_000));

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new SessionHistoryEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    GetNullableString(reader, 3),
                    ParseDateTimeOffset(GetNullableString(reader, 4)),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    !reader.IsDBNull(7) && reader.GetInt32(7) == 1));
            }

            return items;
        }, ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SqliteConnection.ClearPool(_connection);
        _connection.Dispose();
        _mutex.Dispose();
    }

    private async Task<T> ExecuteReadAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return await ExecuteWithBusyRetryAsync(query, ct);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async Task<T> ExecuteWithBusyRetryAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await query(ct);
            }
            catch (SqliteException exception) when (IsBusyOrLocked(exception) && attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds((attempt + 1) * 100), ct);
            }
        }
    }

    private static bool IsBusyOrLocked(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int GetNullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string EscapeLike(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new StringBuilder(input)
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .ToString();
    }
}
