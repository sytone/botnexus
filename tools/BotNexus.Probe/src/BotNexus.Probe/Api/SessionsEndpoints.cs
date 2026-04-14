using BotNexus.Probe.LogIngestion;
using System.Text.Json;

namespace BotNexus.Probe.Api;

public static class SessionsEndpoints
{
    public static IEndpointRouteBuilder MapSessionsEndpoints(
        this IEndpointRouteBuilder app,
        ProbeOptions options,
        JsonlSessionReader sessionReader,
        SessionDbReader? sessionDbReader)
    {
        app.MapGet("/api/sessions", async (string? agent, string? channel, string? type, string? status, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);

            if (sessionDbReader is not null)
            {
                try
                {
                    var sessions = await sessionDbReader.ListSessionsAsync(agent, channel, type, status, normalizedSkip, normalizedTake, cancellationToken);
                    return Results.Ok(new
                    {
                        source = "sqlite",
                        count = sessions.Count,
                        sessions = sessions,
                        items = sessions
                    });
                }
                catch
                {
                    // Fall back to JSONL.
                }
            }

            if (!Directory.Exists(options.SessionsPath))
            {
                return Results.Ok(new
                {
                    source = "jsonl",
                    count = 0,
                    sessions = Array.Empty<object>(),
                    items = Array.Empty<object>()
                });
            }

            var files = Directory.EnumerateFiles(options.SessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(normalizedSkip)
                .Take(normalizedTake)
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

            return Results.Ok(new
            {
                source = "jsonl",
                count = output.Count,
                sessions = output,
                items = output
            });
        });

        app.MapGet("/api/sessions/counts", async (CancellationToken cancellationToken) =>
        {
            if (sessionDbReader is not null)
            {
                try
                {
                    var counts = await sessionDbReader.GetCountsAsync(cancellationToken);
                    return Results.Ok(new { source = "sqlite", counts });
                }
                catch
                {
                    // Fall back to JSONL.
                }
            }

            var total = Directory.Exists(options.SessionsPath)
                ? Directory.EnumerateFiles(options.SessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly).Count()
                : 0;
            return Results.Ok(new
            {
                source = "jsonl",
                counts = new SessionCounts(total, 0, 0, 0, 0)
            });
        });

        app.MapGet("/api/sessions/{sessionId}", async (string sessionId, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);

            if (sessionDbReader is not null)
            {
                try
                {
                    var detail = await sessionDbReader.GetSessionAsync(sessionId, cancellationToken);
                    if (detail is not null)
                    {
                        var history = await sessionDbReader.GetHistoryAsync(detail.Id, normalizedSkip, normalizedTake, cancellationToken);
                        return Results.Ok(new
                        {
                            sessionId = detail.Id,
                            source = "sqlite",
                            agentId = detail.AgentId,
                            channelType = detail.ChannelType,
                            sessionType = detail.SessionType,
                            status = detail.Status,
                            callerId = detail.CallerId,
                            participants = ParseJsonElementOrNull(detail.ParticipantsJson),
                            metadata = ParseJsonElementOrNull(detail.Metadata),
                            createdAt = detail.CreatedAt,
                            updatedAt = detail.UpdatedAt,
                            skip = normalizedSkip,
                            take = normalizedTake,
                            count = history.Count,
                            items = history,
                            messages = new
                            {
                                skip = normalizedSkip,
                                take = normalizedTake,
                                count = history.Count,
                                items = history
                            }
                        });
                    }
                }
                catch
                {
                    // Fall back to JSONL.
                }
            }

            var sessionFile = FindSessionFile(options.SessionsPath, sessionId);
            if (sessionFile is null)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }

            var messages = new List<SessionMessage>(normalizedTake);
            await foreach (var message in sessionReader.ReadMessagesAsync(sessionFile, normalizedSkip, normalizedTake, cancellationToken))
            {
                messages.Add(message);
            }

            var meta = await sessionReader.ReadMetaAsync(sessionFile, cancellationToken);
            return Results.Ok(new
            {
                sessionId,
                source = "jsonl",
                skip = normalizedSkip,
                take = normalizedTake,
                count = messages.Count,
                metadata = meta?.RootElement.Clone(),
                items = messages,
                messages = new
                {
                    skip = normalizedSkip,
                    take = normalizedTake,
                    count = messages.Count,
                    items = messages
                }
            });
        });

        app.MapGet("/api/sessions/{sessionId}/search", async (string sessionId, string q, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);

            if (sessionDbReader is not null)
            {
                try
                {
                    var detail = await sessionDbReader.GetSessionAsync(sessionId, cancellationToken);
                    if (detail is not null)
                    {
                        var matches = (await sessionDbReader.SearchHistoryAsync(q, detail.Id, normalizedSkip + normalizedTake, cancellationToken))
                            .Skip(normalizedSkip)
                            .Take(normalizedTake)
                            .ToList();
                        return Results.Ok(new
                        {
                            sessionId = detail.Id,
                            source = "sqlite",
                            query = q,
                            skip = normalizedSkip,
                            take = normalizedTake,
                            count = matches.Count,
                            items = matches,
                            messages = new
                            {
                                skip = normalizedSkip,
                                take = normalizedTake,
                                count = matches.Count,
                                items = matches
                            }
                        });
                    }
                }
                catch
                {
                    // Fall back to JSONL.
                }
            }

            var sessionFile = FindSessionFile(options.SessionsPath, sessionId);
            if (sessionFile is null)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }

            var results = new List<SessionMessage>(normalizedTake);
            var seen = 0;

            await foreach (var document in sessionReader.ReadAsync(sessionFile, 0, int.MaxValue, null, cancellationToken))
            {
                using (document)
                {
                    var message = JsonlSessionReader.ToSessionMessage(document.RootElement, sessionId);
                    if (!Contains(message.Content, q) &&
                        !Contains(message.Role, q) &&
                        !Contains(message.AgentId, q))
                    {
                        continue;
                    }

                    if (seen++ < normalizedSkip)
                    {
                        continue;
                    }

                    results.Add(message);
                    if (results.Count >= normalizedTake)
                    {
                        break;
                    }
                }
            }

            return Results.Ok(new
            {
                sessionId,
                source = "jsonl",
                query = q,
                skip = normalizedSkip,
                take = normalizedTake,
                count = results.Count,
                items = results,
                messages = new
                {
                    skip = normalizedSkip,
                    take = normalizedTake,
                    count = results.Count,
                    items = results
                }
            });
        });

        return app;
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

    private static JsonElement? ParseJsonElementOrNull(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

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
}
