using BotNexus.Probe.LogIngestion;
using BotNexus.Probe.Otel;

namespace BotNexus.Probe.Api;

public static class CorrelationEndpoints
{
    public static IEndpointRouteBuilder MapCorrelationEndpoints(
        this IEndpointRouteBuilder app,
        ProbeOptions options,
        SerilogFileParser logParser,
        JsonlSessionReader sessionReader,
        SessionDbReader? sessionDbReader,
        TraceStore traceStore,
        bool tracesEnabled)
    {
        app.MapGet("/api/correlate/{id}", async (string id, int? take, CancellationToken cancellationToken) =>
        {
            var normalizedTake = Math.Clamp(take ?? 250, 1, 1_000);
            var logs = new List<LogEntry>();
            var sessions = new List<object>();

            var logQuery = new LogQuery(
                CorrelationId: id,
                SessionId: id,
                AgentId: id,
                SearchText: id);

            await foreach (var logEntry in logParser.ParseDirectoryAsync(options.LogsPath, logQuery, cancellationToken))
            {
                logs.Add(logEntry);
                if (logs.Count >= normalizedTake)
                {
                    break;
                }
            }

            if (Directory.Exists(options.SessionsPath))
            {
                foreach (var sessionFile in Directory.EnumerateFiles(options.SessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    await foreach (var message in sessionReader.ReadMessagesAsync(sessionFile, cancellationToken: cancellationToken))
                    {
                        if (!IsMatch(message, id))
                        {
                            continue;
                        }

                        sessions.Add(new
                        {
                            source = "jsonl",
                            sessionId = message.SessionId,
                            role = message.Role,
                            content = message.Content,
                            timestamp = message.Timestamp,
                            agentId = message.AgentId
                        });
                        if (sessions.Count >= normalizedTake)
                        {
                            break;
                        }
                    }

                    if (sessions.Count >= normalizedTake)
                    {
                        break;
                    }
                }
            }

            if (sessions.Count < normalizedTake && sessionDbReader is not null)
            {
                try
                {
                    var detail = await sessionDbReader.GetSessionAsync(id, cancellationToken);
                    if (detail is not null)
                    {
                        sessions.Add(new
                        {
                            source = "sqlite",
                            match = "sessions.id",
                            sessionId = detail.Id,
                            agentId = detail.AgentId,
                            status = detail.Status,
                            createdAt = detail.CreatedAt
                        });
                    }

                    var bySessionId = await sessionDbReader.GetHistoryAsync(id, take: Math.Min(normalizedTake - sessions.Count, 100), ct: cancellationToken);
                    foreach (var entry in bySessionId)
                    {
                        sessions.Add(new
                        {
                            source = "sqlite",
                            match = "session_history.session_id",
                            sessionId = entry.SessionId,
                            role = entry.Role,
                            content = entry.Content,
                            timestamp = entry.Timestamp,
                            toolName = entry.ToolName
                        });

                        if (sessions.Count >= normalizedTake)
                        {
                            break;
                        }
                    }

                    if (sessions.Count < normalizedTake)
                    {
                        var byContent = await sessionDbReader.SearchHistoryAsync(id, null, Math.Min(normalizedTake - sessions.Count, 100), cancellationToken);
                        foreach (var entry in byContent)
                        {
                            sessions.Add(new
                            {
                                source = "sqlite",
                                match = "session_history.content",
                                sessionId = entry.SessionId,
                                role = entry.Role,
                                content = entry.Content,
                                timestamp = entry.Timestamp,
                                toolName = entry.ToolName
                            });

                            if (sessions.Count >= normalizedTake)
                            {
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Best effort only.
                }
            }

            var traceMatches = tracesEnabled
                ? traceStore.GetTraces(10_000)
                    .Where(span =>
                        span.TraceId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                        span.SpanId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                        span.Attributes.Any(attribute =>
                            attribute.Key.Contains("session", StringComparison.OrdinalIgnoreCase) &&
                            attribute.Value.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                        span.Attributes.Any(attribute => attribute.Value.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    .Take(normalizedTake)
                    .ToList()
                : [];

            return Results.Ok(new
            {
                id,
                logs = new { count = logs.Count, items = logs },
                sessions = new { count = sessions.Count, items = sessions },
                traces = new { enabled = tracesEnabled, count = traceMatches.Count, items = traceMatches }
            });
        });

        return app;
    }

    private static bool IsMatch(SessionMessage message, string id)
    {
        if (message.SessionId.Contains(id, StringComparison.OrdinalIgnoreCase) ||
            Contains(message.AgentId, id) ||
            Contains(message.Content, id))
        {
            return true;
        }

        return message.Metadata?.Any(pair =>
            pair.Key.Contains(id, StringComparison.OrdinalIgnoreCase) ||
            pair.Value.ToString().Contains(id, StringComparison.OrdinalIgnoreCase)) is true;
    }

    private static bool Contains(string? source, string value)
        => source?.Contains(value, StringComparison.OrdinalIgnoreCase) is true;
}
