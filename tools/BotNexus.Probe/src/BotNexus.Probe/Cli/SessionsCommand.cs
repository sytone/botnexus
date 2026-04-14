using BotNexus.Probe.LogIngestion;
using System.Text;
using System.Text.Json;

namespace BotNexus.Probe.Cli;

public static class SessionsCommand
{
    public static async Task<int> ListAsync(
        CliOptions options,
        string[] args,
        JsonlSessionReader sessionReader,
        SessionDbReader? sessionDbReader,
        CancellationToken cancellationToken)
    {
        _ = sessionReader;
        var commandOptions = ParseList(args);

        if (sessionDbReader is not null)
        {
            try
            {
                var sessions = await sessionDbReader.ListSessionsAsync(
                    commandOptions.AgentId,
                    commandOptions.ChannelType,
                    commandOptions.SessionType,
                    commandOptions.Status,
                    commandOptions.Skip,
                    commandOptions.Take,
                    cancellationToken);

                var payload = new
                {
                    status = sessions.Count > 0 ? "ok" : "empty",
                    source = "sqlite",
                    count = sessions.Count,
                    items = sessions
                };

                CliOutput.Write(options, payload, () => FormatSessionsText(sessions, "sqlite"));
                return sessions.Count > 0 ? 0 : 2;
            }
            catch
            {
                // Fall through to JSONL reader.
            }
        }

        if (!Directory.Exists(options.SessionsPath))
        {
            var emptyPayload = new { status = "empty", source = "jsonl", count = 0, items = Array.Empty<object>() };
            CliOutput.Write(options, emptyPayload, () => "No sessions directory found.");
            return 2;
        }

        var files = Directory.EnumerateFiles(options.SessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var output = new List<SessionSummary>(files.Count);
        foreach (var file in files)
        {
            output.Add(new SessionSummary(
                Path.GetFileNameWithoutExtension(file.Name),
                null,
                null,
                null,
                null,
                null,
                new DateTimeOffset(file.LastWriteTimeUtc),
                new DateTimeOffset(file.LastWriteTimeUtc),
                await CountLinesAsync(file.FullName, cancellationToken)));
        }

        var paged = output.Skip(commandOptions.Skip).Take(commandOptions.Take).ToList();
        var fallbackPayload = new
        {
            status = paged.Count > 0 ? "ok" : "empty",
            source = "jsonl",
            count = paged.Count,
            items = paged
        };

        CliOutput.Write(options, fallbackPayload, () => FormatSessionsText(paged, "jsonl"));
        return paged.Count > 0 ? 0 : 2;
    }

    public static async Task<int> GetAsync(
        CliOptions options,
        string[] args,
        JsonlSessionReader sessionReader,
        SessionDbReader? sessionDbReader,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            CliOutput.WriteError("session command requires an id. Usage: probe session <id> [--take N] [--skip N] [--search text]");
            return 1;
        }

        var sessionId = args[0];
        var commandOptions = ParseDetail(args[1..]);

        if (sessionDbReader is not null)
        {
            try
            {
                var detail = await GetDetailFromSqliteAsync(sessionDbReader, sessionId, commandOptions, cancellationToken);
                if (detail is not null)
                {
                    CliOutput.Write(options, detail.Payload, () => FormatDetailText(detail.SessionId, detail.Items, detail.Source, detail.Detail));
                    return detail.Items.Count > 0 ? 0 : 2;
                }
            }
            catch
            {
                // Fall through to JSONL reader.
            }
        }

        var sessionFile = FindSessionFile(options.SessionsPath, sessionId);
        if (sessionFile is null)
        {
            var emptyPayload = new { status = "empty", source = "jsonl", sessionId, count = 0, items = Array.Empty<object>() };
            CliOutput.Write(options, emptyPayload, () => $"Session '{sessionId}' not found.");
            return 2;
        }

        var messages = new List<SessionMessage>(commandOptions.Take);
        if (string.IsNullOrWhiteSpace(commandOptions.Search))
        {
            await foreach (var message in sessionReader.ReadMessagesAsync(sessionFile, commandOptions.Skip, commandOptions.Take, cancellationToken))
            {
                messages.Add(message);
            }
        }
        else
        {
            var seen = 0;
            await foreach (var document in sessionReader.ReadAsync(sessionFile, 0, int.MaxValue, null, cancellationToken))
            {
                using (document)
                {
                    var message = JsonlSessionReader.ToSessionMessage(document.RootElement, sessionId);
                    if (!Contains(message.Content, commandOptions.Search) &&
                        !Contains(message.Role, commandOptions.Search) &&
                        !Contains(message.AgentId, commandOptions.Search))
                    {
                        continue;
                    }

                    if (seen++ < commandOptions.Skip)
                    {
                        continue;
                    }

                    messages.Add(message);
                    if (messages.Count >= commandOptions.Take)
                    {
                        break;
                    }
                }
            }
        }

        var meta = await sessionReader.ReadMetaAsync(sessionFile, cancellationToken);
        var payload = new
        {
            status = messages.Count > 0 ? "ok" : "empty",
            source = "jsonl",
            sessionId,
            skip = commandOptions.Skip,
            take = commandOptions.Take,
            query = commandOptions.Search,
            count = messages.Count,
            metadata = meta?.RootElement.Clone(),
            items = messages
        };

        CliOutput.Write(options, payload, () => CliOutput.FormatSessionMessages(sessionId, messages));
        return messages.Count > 0 ? 0 : 2;
    }

    private static async Task<SqliteSessionDetailResult?> GetDetailFromSqliteAsync(
        SessionDbReader sessionDbReader,
        string requestedSessionId,
        SessionQueryOptions commandOptions,
        CancellationToken cancellationToken)
    {
        var detail = await sessionDbReader.GetSessionAsync(requestedSessionId, cancellationToken);
        if (detail is null)
        {
            var candidates = await sessionDbReader.ListSessionsAsync(take: 500, ct: cancellationToken);
            foreach (var candidate in candidates.Where(session => session.Id.Contains(requestedSessionId, StringComparison.OrdinalIgnoreCase)))
            {
                detail = await sessionDbReader.GetSessionAsync(candidate.Id, cancellationToken);
                if (detail is not null)
                {
                    break;
                }
            }
        }

        if (detail is null)
        {
            return null;
        }

        var history = string.IsNullOrWhiteSpace(commandOptions.Search)
            ? await sessionDbReader.GetHistoryAsync(detail.Id, commandOptions.Skip, commandOptions.Take, cancellationToken)
            : (await sessionDbReader.SearchHistoryAsync(commandOptions.Search, detail.Id, commandOptions.Skip + commandOptions.Take, cancellationToken))
                .Skip(commandOptions.Skip)
                .Take(commandOptions.Take)
                .ToList();

        var payload = new
        {
            status = history.Count > 0 ? "ok" : "empty",
            source = "sqlite",
            sessionId = detail.Id,
            agentId = detail.AgentId,
            channelType = detail.ChannelType,
            sessionType = detail.SessionType,
            statusText = detail.Status,
            callerId = detail.CallerId,
            participants = ParseJsonElementOrNull(detail.ParticipantsJson),
            metadata = ParseJsonElementOrNull(detail.Metadata),
            createdAt = detail.CreatedAt,
            updatedAt = detail.UpdatedAt,
            skip = commandOptions.Skip,
            take = commandOptions.Take,
            query = commandOptions.Search,
            count = history.Count,
            items = history
        };

        return new SqliteSessionDetailResult(detail.Id, detail, history, payload, "sqlite");
    }

    private static string FormatSessionsText(IReadOnlyList<SessionSummary> sessions, string source)
    {
        var lines = new StringBuilder();
        lines.AppendLine($"💬 Sessions ({sessions.Count} results, source: {source})");
        lines.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        foreach (var item in sessions)
        {
            lines.AppendLine(
                $"  {Trim(item.Id, 10),-10} {Trim(item.AgentId, 10),-10} {Trim(item.ChannelType, 9),-9} {Trim(item.SessionType, 11),-11} {Trim(item.Status, 9),-9} {FormatDate(item.CreatedAt),-16} ({item.MessageCount} msgs)");
        }

        return lines.ToString().TrimEnd();
    }

    private static string FormatDetailText(string sessionId, IReadOnlyList<SessionHistoryEntry> history, string source, SessionDetail detail)
    {
        var output = new StringBuilder();
        output.AppendLine($"💬 Session {sessionId} (source: {source})");
        output.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        output.AppendLine($"Agent: {detail.AgentId ?? "—"}");
        output.AppendLine($"Channel: {detail.ChannelType ?? "—"}");
        output.AppendLine($"Type: {detail.SessionType ?? "—"}");
        output.AppendLine($"Status: {detail.Status ?? "—"}");
        output.AppendLine($"Created: {FormatDate(detail.CreatedAt)}");
        output.AppendLine($"Updated: {FormatDate(detail.UpdatedAt)}");
        output.AppendLine();

        foreach (var item in history)
        {
            var timestamp = item.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "--:--:--";
            var role = item.Role ?? "unknown";
            var toolName = string.IsNullOrWhiteSpace(item.ToolName) ? string.Empty : $" [{item.ToolName}]";
            var compact = item.IsCompactionSummary ? " [compaction]" : string.Empty;
            output.AppendLine($"[{timestamp}] {role}{toolName}{compact}: {item.Content}");
        }

        return output.ToString().TrimEnd();
    }

    private static JsonElement? ParseJsonElementOrNull(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static string? FindSessionFile(string sessionsPath, string sessionId)
    {
        if (!Directory.Exists(sessionsPath))
        {
            return null;
        }

        var exact = Path.Combine(sessionsPath, $"{sessionId}.jsonl");
        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Contains(sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) is true;

    private static async Task<int> CountLinesAsync(string filePath, CancellationToken cancellationToken)
    {
        var count = 0;
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static SessionQueryOptions ParseDetail(string[] args)
    {
        var skip = 0;
        var take = 100;
        string? search = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            var nextValue = index + 1 < args.Length ? args[index + 1] : null;

            switch (arg)
            {
                case "--skip" when int.TryParse(nextValue, out var parsedSkip):
                    skip = Math.Max(0, parsedSkip);
                    index++;
                    break;
                case "--take" when int.TryParse(nextValue, out var parsedTake):
                    take = Math.Clamp(parsedTake, 1, 1_000);
                    index++;
                    break;
                case "--search":
                    search = nextValue;
                    index++;
                    break;
            }
        }

        return new SessionQueryOptions(skip, take, search);
    }

    private static SessionListQueryOptions ParseList(string[] args)
    {
        string? agent = null;
        string? channel = null;
        string? type = null;
        string? status = null;
        var skip = 0;
        var take = 100;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            var nextValue = index + 1 < args.Length ? args[index + 1] : null;

            switch (arg)
            {
                case "--agent":
                    agent = nextValue;
                    index++;
                    break;
                case "--channel":
                    channel = nextValue;
                    index++;
                    break;
                case "--type":
                    type = nextValue;
                    index++;
                    break;
                case "--status":
                    status = nextValue;
                    index++;
                    break;
                case "--skip" when int.TryParse(nextValue, out var parsedSkip):
                    skip = Math.Max(0, parsedSkip);
                    index++;
                    break;
                case "--take" when int.TryParse(nextValue, out var parsedTake):
                    take = Math.Clamp(parsedTake, 1, 1_000);
                    index++;
                    break;
            }
        }

        return new SessionListQueryOptions(agent, channel, type, status, skip, take);
    }

    private static string Trim(string? value, int max)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "—" : value;
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private static string FormatDate(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    private sealed record SessionQueryOptions(int Skip, int Take, string? Search);
    private sealed record SessionListQueryOptions(string? AgentId, string? ChannelType, string? SessionType, string? Status, int Skip, int Take);
    private sealed record SqliteSessionDetailResult(string SessionId, SessionDetail Detail, List<SessionHistoryEntry> Items, object Payload, string Source);
}
