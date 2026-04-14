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
        TraceStore traceStore,
        bool tracesEnabled)
    {
        app.MapGet("/api/correlate/{id}", async (string id, int? take, CancellationToken cancellationToken) =>
        {
            var normalizedTake = Math.Clamp(take ?? 250, 1, 1_000);
            var logs = new List<LogEntry>();
            var sessions = new List<SessionMessage>();

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

                        sessions.Add(message);
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
