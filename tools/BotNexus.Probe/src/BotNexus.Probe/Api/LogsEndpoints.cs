using BotNexus.Probe.LogIngestion;

namespace BotNexus.Probe.Api;

public static class LogsEndpoints
{
    public static IEndpointRouteBuilder MapLogsEndpoints(this IEndpointRouteBuilder app, ProbeOptions options, SerilogFileParser parser)
    {
        app.MapGet("/api/logs", async (
            string? level,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? sessionId,
            string? correlationId,
            string? agentId,
            string? search,
            int? skip,
            int? take,
            CancellationToken cancellationToken) =>
        {
            var query = new LogQuery(level, from, to, correlationId, sessionId, agentId, search);
            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);
            var results = new List<LogEntry>(normalizedTake);
            var seen = 0;

            await foreach (var entry in parser.ParseDirectoryAsync(options.LogsPath, query, cancellationToken))
            {
                if (seen++ < normalizedSkip)
                {
                    continue;
                }

                results.Add(entry);
                if (results.Count >= normalizedTake)
                {
                    break;
                }
            }

            return Results.Ok(new
            {
                skip = normalizedSkip,
                take = normalizedTake,
                count = results.Count,
                items = results
            });
        });

        app.MapGet("/api/logs/files", () =>
        {
            if (!Directory.Exists(options.LogsPath))
            {
                return Results.Ok(Array.Empty<object>());
            }

            var files = Directory.EnumerateFiles(options.LogsPath, "*.log*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new
                {
                    name = file.Name,
                    fullPath = file.FullName,
                    size = file.Length,
                    modified = new DateTimeOffset(file.LastWriteTimeUtc),
                    from = new DateTimeOffset(file.CreationTimeUtc),
                    to = new DateTimeOffset(file.LastWriteTimeUtc)
                });

            return Results.Ok(files);
        });

        return app;
    }
}
