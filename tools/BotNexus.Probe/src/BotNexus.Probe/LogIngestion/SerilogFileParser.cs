using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Probe.LogIngestion;

public sealed partial class SerilogFileParser
{
    private static readonly Regex HeaderRegex = HeaderLine();

    public async IAsyncEnumerable<LogEntry> ParseDirectoryAsync(
        string directoryPath,
        LogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            yield break;
        }

        var files = Directory.EnumerateFiles(directoryPath, "*.log*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName);

        foreach (var file in files)
        {
            await foreach (var entry in ParseFileAsync(file, query, cancellationToken))
            {
                yield return entry;
            }
        }
    }

    public async IAsyncEnumerable<LogEntry> ParseFileAsync(
        string filePath,
        LogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? line;
        long lineNumber = 0;
        PendingEntry? pending = null;
        var fileDate = File.GetLastWriteTime(filePath).Date;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            var headerMatch = HeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                if (pending is not null)
                {
                    var built = pending.Build(filePath);
                    if (Matches(built, query))
                    {
                        yield return built;
                    }
                }

                pending = PendingEntry.FromHeader(headerMatch, fileDate, lineNumber);
                continue;
            }

            pending?.AppendDetail(line);
        }

        if (pending is not null)
        {
            var built = pending.Build(filePath);
            if (Matches(built, query))
            {
                yield return built;
            }
        }
    }

    private static bool Matches(LogEntry entry, LogQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Level) && !entry.Level.Equals(query.Level, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.From is { } from && entry.Timestamp < from)
        {
            return false;
        }

        if (query.To is { } to && entry.Timestamp > to)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId) && !Contains(entry.CorrelationId, query.CorrelationId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SessionId) && !Contains(entry.SessionId, query.SessionId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.AgentId) && !Contains(entry.AgentId, query.AgentId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var haystack = $"{entry.Message}\n{entry.Exception}";
            if (!Contains(haystack, query.SearchText))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string? source, string value)
        => source?.Contains(value, StringComparison.OrdinalIgnoreCase) is true;

    private static DateTimeOffset ParseTimestamp(DateTime fileDate, string value)
    {
        if (!TimeSpan.TryParse(value, out var time))
        {
            return new DateTimeOffset(fileDate, TimeZoneInfo.Local.GetUtcOffset(fileDate));
        }

        var local = fileDate.Add(time);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static Dictionary<string, string> ParseProperties(string? rawProperties)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawProperties))
        {
            return output;
        }

        foreach (var segment in rawProperties.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= segment.Length - 1)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim().Trim('"');
            output[key] = value;
        }

        return output;
    }

    private static string? ResolveProperty(IReadOnlyDictionary<string, string> properties, params string[] candidates)
    {
        foreach (var key in candidates)
        {
            if (properties.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed class PendingEntry(
        DateTimeOffset timestamp,
        string level,
        string message,
        Dictionary<string, string> properties,
        long lineNumber)
    {
        private readonly StringBuilder _details = new();

        public static PendingEntry FromHeader(Match headerMatch, DateTime fileDate, long lineNumber)
        {
            var timestamp = ParseTimestamp(fileDate, headerMatch.Groups["time"].Value);
            var properties = ParseProperties(headerMatch.Groups["props"].Success ? headerMatch.Groups["props"].Value : null);
            var message = headerMatch.Groups["message"].Value.Trim();
            var level = headerMatch.Groups["level"].Value.Trim();
            return new PendingEntry(timestamp, level, message, properties, lineNumber);
        }

        public void AppendDetail(string line)
        {
            if (_details.Length > 0)
            {
                _details.AppendLine();
            }

            _details.Append(line);
        }

        public LogEntry Build(string sourceFile)
        {
            var exception = _details.Length > 0 ? _details.ToString() : null;
            var readOnlyProps = properties.AsReadOnly();

            return new LogEntry(
                timestamp,
                level,
                message,
                exception,
                ResolveProperty(properties, "CorrelationId", "correlationId", "correlation_id"),
                ResolveProperty(properties, "SessionId", "sessionId", "session_id"),
                ResolveProperty(properties, "AgentId", "agentId", "agent_id"),
                ResolveProperty(properties, "Channel", "ChannelType", "channel"),
                Path.GetFileName(sourceFile),
                lineNumber,
                readOnlyProps);
        }
    }

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2})\s+(?<level>[A-Z]+)\]\s(?<message>.*?)(?:\s\{(?<props>.*)\})?$", RegexOptions.Compiled)]
    private static partial Regex HeaderLine();
}
