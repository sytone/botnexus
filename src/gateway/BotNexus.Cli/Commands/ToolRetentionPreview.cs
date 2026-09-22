using System.Text;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Builds a conservative, read-only retention preview over historical tool rows. The preview does
/// not promise that payloads are safe to delete: mutation, failure, incomplete, recent, and active
/// invocations remain protected until the durable receipt model owns their evidence.
/// </summary>
internal static class ToolRetentionPreview
{
    private static readonly string[] MutationTokens =
    [
        "create", "update", "delete", "remove", "write", "edit", "patch", "send", "comment",
        "reply", "merge", "close", "archive", "kill", "start", "stop", "restart", "set", "add"
    ];

    internal static ToolRetentionReport CreateReport(
        string databasePath,
        int olderThanDays,
        DateTimeOffset? now = null)
    {
        if (olderThanDays < 1)
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), "Retention age must be at least one day.");

        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-olderThanDays);
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.session_id, COALESCE(s.status, 'unknown'), h.message_kind,
                   h.tool_name, h.tool_call_id, h.content, h.tool_args,
                   h.tool_is_error, h.is_history, h.timestamp
            FROM session_history h
            LEFT JOIN sessions s ON s.id = h.session_id
            WHERE h.message_kind IN ('tool-start', 'tool-result')
            ORDER BY h.session_id, h.tool_call_id, h.id
            """;

        var invocations = new Dictionary<(string SessionId, string CallId), Invocation>(StringTupleComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var callId = reader.IsDBNull(4) ? $"missing:{reader.GetValue(9)}:{invocations.Count}" : reader.GetString(4);
            var key = (sessionId, callId);
            if (!invocations.TryGetValue(key, out var invocation))
            {
                invocation = new Invocation(sessionId, reader.IsDBNull(1) ? "unknown" : reader.GetString(1));
                invocations.Add(key, invocation);
            }

            invocation.Add(
                kind: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                toolName: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                content: reader.IsDBNull(5) ? null : reader.GetString(5),
                arguments: reader.IsDBNull(6) ? null : reader.GetString(6),
                isError: !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                isHistory: !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
                timestamp: reader.IsDBNull(9) ? null : reader.GetString(9));
        }

        var report = new ToolRetentionReport(olderThanDays, cutoff);
        foreach (var invocation in invocations.Values)
        {
            var reason = GetProtectionReason(invocation, cutoff);
            if (reason is not null)
            {
                report.AddProtected(reason, invocation.RowCount, invocation.ContentBytes, invocation.StoredArgumentBytes);
                continue;
            }

            report.AddCandidate(invocation);
        }

        return report;
    }

    private static string? GetProtectionReason(Invocation invocation, DateTimeOffset cutoff)
    {
        if (invocation.SessionStatus.Equals("active", StringComparison.OrdinalIgnoreCase)) return "active-session";
        if (invocation.IsError) return "error";
        if (!invocation.HasStart || !invocation.HasResult) return "incomplete";
        if (IsMutation(invocation.ToolName)) return "mutation";
        if (invocation.LatestTimestamp is null) return "unknown-age";
        if (invocation.LatestTimestamp >= cutoff) return "recent";
        if (!invocation.IsHistory &&
            !invocation.SessionStatus.Equals("sealed", StringComparison.OrdinalIgnoreCase) &&
            !invocation.SessionStatus.Equals("expired", StringComparison.OrdinalIgnoreCase)) return "non-historical";
        return null;
    }

    private static bool IsMutation(string toolName)
    {
        var segments = toolName.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment => MutationTokens.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    internal sealed class Invocation(string sessionId, string sessionStatus)
    {
        private string? _arguments;

        internal string SessionId { get; } = sessionId;
        internal string SessionStatus { get; } = sessionStatus;
        internal string ToolName { get; private set; } = string.Empty;
        internal bool HasStart { get; private set; }
        internal bool HasResult { get; private set; }
        internal bool IsError { get; private set; }
        internal bool IsHistory { get; private set; }
        internal int RowCount { get; private set; }
        internal long ContentBytes { get; private set; }
        internal long StoredArgumentBytes { get; private set; }
        internal long ArgumentBytes => Utf8Bytes(_arguments);
        internal DateTimeOffset? LatestTimestamp { get; private set; }

        internal void Add(string kind, string toolName, string? content, string? arguments, bool isError, bool isHistory, string? timestamp)
        {
            RowCount++;
            ToolName = string.IsNullOrWhiteSpace(ToolName) ? toolName : ToolName;
            HasStart |= kind.Equals("tool-start", StringComparison.OrdinalIgnoreCase);
            HasResult |= kind.Equals("tool-result", StringComparison.OrdinalIgnoreCase);
            IsError |= isError;
            IsHistory |= isHistory;
            ContentBytes += Utf8Bytes(content);
            StoredArgumentBytes += Utf8Bytes(arguments);
            if (_arguments is null && arguments is not null) _arguments = arguments;
            if (DateTimeOffset.TryParse(timestamp, out var parsed) && (LatestTimestamp is null || parsed > LatestTimestamp))
                LatestTimestamp = parsed;
        }

        internal string ToolClass => IsMutation(ToolName) ? "mutation" : "read";
        internal string Outcome => IsError ? "error" : HasResult ? "success" : "incomplete";
        internal string AgeBucket(DateTimeOffset cutoff, int olderThanDays)
        {
            var ageDays = olderThanDays + Math.Max(0, (cutoff - LatestTimestamp!.Value).Days);
            return ageDays switch
            {
                <= 60 => "31-60-days",
                <= 90 => "61-90-days",
                _ => "over-90-days"
            };
        }
    }

    private static long Utf8Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private sealed class StringTupleComparer : IEqualityComparer<(string SessionId, string CallId)>
    {
        internal static StringTupleComparer Ordinal { get; } = new();
        public bool Equals((string SessionId, string CallId) x, (string SessionId, string CallId) y) =>
            string.Equals(x.SessionId, y.SessionId, StringComparison.Ordinal) && string.Equals(x.CallId, y.CallId, StringComparison.Ordinal);
        public int GetHashCode((string SessionId, string CallId) obj) => HashCode.Combine(obj.SessionId, obj.CallId);
    }
}

internal sealed class ToolRetentionReport(int olderThanDays, DateTimeOffset cutoff)
{
    public int OlderThanDays { get; } = olderThanDays;
    public DateTimeOffset Cutoff { get; } = cutoff;
    public int CandidateInvocations { get; private set; }
    public int CandidateRows { get; private set; }
    public long CandidateContentBytes { get; private set; }
    public long CandidateArgumentBytes { get; private set; }
    public long DuplicatedCandidateArgumentBytes { get; private set; }
    public long EstimatedReclaimableBytes => CandidateContentBytes + Math.Max(0, DuplicatedCandidateArgumentBytes - CandidateArgumentBytes);
    public Dictionary<string, int> ProtectedRows { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ToolRetentionBreakdown> ByAge { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ToolRetentionBreakdown> BySessionState { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ToolRetentionBreakdown> ByToolClass { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ToolRetentionBreakdown> ByOutcome { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long ProtectedContentBytes { get; private set; }
    public long ProtectedArgumentBytes { get; private set; }

    internal void AddCandidate(ToolRetentionPreview.Invocation invocation)
    {
        CandidateInvocations++;
        CandidateRows += invocation.RowCount;
        CandidateContentBytes += invocation.ContentBytes;
        CandidateArgumentBytes += invocation.ArgumentBytes;
        DuplicatedCandidateArgumentBytes += invocation.StoredArgumentBytes;
        AddBreakdown(ByAge, invocation.AgeBucket(Cutoff, OlderThanDays), invocation);
        AddBreakdown(BySessionState, invocation.SessionStatus, invocation);
        AddBreakdown(ByToolClass, invocation.ToolClass, invocation);
        AddBreakdown(ByOutcome, invocation.Outcome, invocation);
    }

    internal void AddProtected(string reason, int rows, long contentBytes, long argumentBytes)
    {
        ProtectedRows[reason] = ProtectedRows.GetValueOrDefault(reason) + rows;
        ProtectedContentBytes += contentBytes;
        ProtectedArgumentBytes += argumentBytes;
    }

    private static void AddBreakdown(Dictionary<string, ToolRetentionBreakdown> target, string key, ToolRetentionPreview.Invocation invocation)
    {
        if (!target.TryGetValue(key, out var value))
        {
            value = new ToolRetentionBreakdown();
            target[key] = value;
        }
        value.Add(invocation.RowCount, invocation.ContentBytes, invocation.StoredArgumentBytes);
    }
}

internal sealed class ToolRetentionBreakdown
{
    public int CandidateInvocations { get; private set; }
    public int CandidateRows { get; private set; }
    public long ContentBytes { get; private set; }
    public long ArgumentBytes { get; private set; }

    internal void Add(int rows, long contentBytes, long argumentBytes)
    {
        CandidateInvocations++;
        CandidateRows += rows;
        ContentBytes += contentBytes;
        ArgumentBytes += argumentBytes;
    }
}
