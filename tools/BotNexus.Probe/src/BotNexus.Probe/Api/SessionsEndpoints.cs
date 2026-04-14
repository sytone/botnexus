using BotNexus.Probe.LogIngestion;
using System.Text.Json;

namespace BotNexus.Probe.Api;

public static class SessionsEndpoints
{
    public static IEndpointRouteBuilder MapSessionsEndpoints(this IEndpointRouteBuilder app, ProbeOptions options, JsonlSessionReader sessionReader)
    {
        app.MapGet("/api/sessions", async (CancellationToken cancellationToken) =>
        {
            if (!Directory.Exists(options.SessionsPath))
            {
                return Results.Ok(Array.Empty<object>());
            }

            var files = Directory.EnumerateFiles(options.SessionsPath, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var output = new List<object>(files.Count);
            foreach (var file in files)
            {
                output.Add(new
                {
                    id = Path.GetFileNameWithoutExtension(file.Name),
                    file = file.Name,
                    size = file.Length,
                    modified = new DateTimeOffset(file.LastWriteTimeUtc),
                    messageCount = await CountLinesAsync(file.FullName, cancellationToken)
                });
            }

            return Results.Ok(output);
        });

        app.MapGet("/api/sessions/{sessionId}", async (string sessionId, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var sessionFile = FindSessionFile(options.SessionsPath, sessionId);
            if (sessionFile is null)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }

            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);
            var messages = new List<SessionMessage>(normalizedTake);

            await foreach (var message in sessionReader.ReadMessagesAsync(sessionFile, normalizedSkip, normalizedTake, cancellationToken))
            {
                messages.Add(message);
            }

            var meta = await sessionReader.ReadMetaAsync(sessionFile, cancellationToken);
            return Results.Ok(new
            {
                sessionId,
                skip = normalizedSkip,
                take = normalizedTake,
                count = messages.Count,
                metadata = meta?.RootElement.Clone(),
                items = messages
            });
        });

        app.MapGet("/api/sessions/{sessionId}/search", async (string sessionId, string q, int? skip, int? take, CancellationToken cancellationToken) =>
        {
            var sessionFile = FindSessionFile(options.SessionsPath, sessionId);
            if (sessionFile is null)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }

            var normalizedSkip = Math.Max(0, skip ?? 0);
            var normalizedTake = Math.Clamp(take ?? 100, 1, 1_000);
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
                query = q,
                skip = normalizedSkip,
                take = normalizedTake,
                count = results.Count,
                items = results
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
