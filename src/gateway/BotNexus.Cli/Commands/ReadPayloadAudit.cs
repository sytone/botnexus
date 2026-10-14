using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Measures repeated read response payloads from persisted tool-result rows. The numerator is a
/// successful read result whose complete response body is byte-equivalent to an earlier complete
/// body for the same normalized slice in the same session. The denominator is every successful
/// read result in the half-open audit window. Short unchanged markers are reported separately and
/// never count as repeated payload delivery.
/// </summary>
internal static class ReadPayloadAudit
{
    internal const decimal MaximumRepeatedPayloadsPerThousand = 5m;
    private const string UnchangedMarkerPrefix = "[Unchanged since your earlier read of '";
    private const string TruncationMarker = "\n…[truncated ";

    internal static ReadPayloadAuditReport CreateReport(
        string databasePath,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentOutOfRangeException(nameof(windowEnd), "Audit window end must be later than its start.");

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, content, tool_args, tool_is_error
            FROM session_history
            WHERE role = 'tool'
              AND lower(tool_name) = 'read'
              AND message_kind = 'tool-result'
              AND timestamp >= $windowStart
              AND timestamp < $windowEnd
            ORDER BY session_id, id
            """;
        command.Parameters.AddWithValue("$windowStart", windowStart.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$windowEnd", windowEnd.ToUniversalTime().ToString("O"));

        var report = new ReadPayloadAuditReport(windowStart, windowEnd);
        var slicesBySession = new Dictionary<(string SessionId, string Slice), SliceState>(StringTupleComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(3) && reader.GetInt64(3) != 0)
            {
                report.ExcludedErrorResults++;
                continue;
            }

            report.SuccessfulReadResults++;
            var sessionId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var arguments = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (IsUnchangedMarker(content))
            {
                report.UnchangedMarkers++;
                continue;
            }

            if (!TryNormalizeSlice(arguments, out var slice))
            {
                report.UnclassifiableResults++;
                continue;
            }

            var key = (sessionId, slice);
            if (!slicesBySession.TryGetValue(key, out var state))
            {
                report.FirstSliceResults++;
                slicesBySession.Add(key, new SliceState(content));
                continue;
            }

            if (IsCompletePayload(content) && string.Equals(state.LastCompletePayload, content, StringComparison.Ordinal))
            {
                report.RepeatedFullPayloads++;
                report.RepeatedFullPayloadBytes += Encoding.UTF8.GetByteCount(content);
            }
            else
            {
                report.ChangedSameSliceResults++;
            }

            state.Update(content);
        }

        report.DifferentSliceResults = slicesBySession
            .GroupBy(pair => pair.Key.SessionId, StringComparer.Ordinal)
            .Sum(group => Math.Max(0, group.Count() - 1));
        return report;
    }

    private static bool IsUnchangedMarker(string content)
        => content.StartsWith(UnchangedMarkerPrefix, StringComparison.Ordinal);

    private static bool IsCompletePayload(string content)
        => !content.Contains(TruncationMarker, StringComparison.Ordinal);

    private static bool TryNormalizeSlice(string? serializedArguments, out string slice)
    {
        slice = string.Empty;
        if (string.IsNullOrWhiteSpace(serializedArguments)) return false;

        try
        {
            using var document = JsonDocument.Parse(serializedArguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
                return false;

            var path = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(path)) return false;

            var offset = ReadPositiveInt(document.RootElement, "offset") ?? 1;
            var limit = ReadPositiveInt(document.RootElement, "limit");
            var normalizedPath = path.Trim().Replace('\\', '/').ToUpperInvariant();
            slice = $"{normalizedPath}|{offset}|{limit?.ToString() ?? "*"}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadPositiveInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed < 1) return null;
        return parsed;
    }

    private sealed class SliceState(string firstContent)
    {
        internal string? LastCompletePayload { get; private set; } =
            IsCompletePayload(firstContent) ? firstContent : null;

        internal void Update(string content)
            => LastCompletePayload = IsCompletePayload(content) ? content : null;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string SessionId, string Slice)>
    {
        internal static StringTupleComparer Ordinal { get; } = new();
        public bool Equals((string SessionId, string Slice) x, (string SessionId, string Slice) y) =>
            string.Equals(x.SessionId, y.SessionId, StringComparison.Ordinal) &&
            string.Equals(x.Slice, y.Slice, StringComparison.Ordinal);
        public int GetHashCode((string SessionId, string Slice) obj) => HashCode.Combine(obj.SessionId, obj.Slice);
    }
}

internal sealed class ReadPayloadAuditReport(DateTimeOffset windowStart, DateTimeOffset windowEnd)
{
    public DateTimeOffset WindowStart { get; } = windowStart;
    public DateTimeOffset WindowEnd { get; } = windowEnd;
    public int SuccessfulReadResults { get; internal set; }
    public int RepeatedFullPayloads { get; internal set; }
    public long RepeatedFullPayloadBytes { get; internal set; }
    public int UnchangedMarkers { get; internal set; }
    public int ChangedSameSliceResults { get; internal set; }
    public int DifferentSliceResults { get; internal set; }
    public int FirstSliceResults { get; internal set; }
    public int UnclassifiableResults { get; internal set; }
    public int ExcludedErrorResults { get; internal set; }
    public decimal RepeatedPayloadsPerThousand => SuccessfulReadResults == 0
        ? 0
        : Math.Round(RepeatedFullPayloads * 1000m / SuccessfulReadResults, 2, MidpointRounding.AwayFromZero);
    public decimal MaximumRepeatedPayloadsPerThousand => ReadPayloadAudit.MaximumRepeatedPayloadsPerThousand;
    public bool MeetsThreshold => RepeatedPayloadsPerThousand <= MaximumRepeatedPayloadsPerThousand;
}
