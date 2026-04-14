using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BotNexus.Probe.LogIngestion;

public sealed class JsonlSessionReader
{
    public async IAsyncEnumerable<JsonDocument> ReadAsync(
        string filePath,
        int skip = 0,
        int take = int.MaxValue,
        Action<string>? warning = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath) || take <= 0)
        {
            yield break;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? line;
        var lineNumber = 0;
        var yielded = 0;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            if (lineNumber <= skip || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, out var document))
            {
                warning?.Invoke($"Skipping malformed JSONL line {lineNumber} in {Path.GetFileName(filePath)}.");
                continue;
            }

            yield return document;
            yielded++;

            if (yielded >= take)
            {
                yield break;
            }
        }
    }

    public async Task<JsonDocument?> ReadMetaAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var metaFilePath = filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? filePath.Replace(".jsonl", ".meta.json", StringComparison.OrdinalIgnoreCase)
            : $"{filePath}.meta.json";

        if (!File.Exists(metaFilePath))
        {
            return null;
        }

        await using var stream = new FileStream(metaFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<SessionMessage> ReadMessagesAsync(
        string filePath,
        int skip = 0,
        int take = int.MaxValue,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var document in ReadAsync(filePath, skip, take, null, cancellationToken))
        {
            using (document)
            {
                yield return ToSessionMessage(document.RootElement, Path.GetFileNameWithoutExtension(filePath));
            }
        }
    }

    public static SessionMessage ToSessionMessage(JsonElement element, string fallbackSessionId)
    {
        var sessionId = GetString(element, "sessionId")
            ?? GetString(element, "session_id")
            ?? fallbackSessionId;

        var timestamp = GetDateTimeOffset(element, "timestamp")
            ?? GetDateTimeOffset(element, "createdAt")
            ?? GetDateTimeOffset(element, "time");

        var role = GetString(element, "role");
        var content = GetString(element, "content")
            ?? GetString(element, "message")
            ?? GetString(element, "text");
        var agentId = GetString(element, "agentId") ?? GetString(element, "agent_id");

        var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("timestamp") || property.NameEquals("createdAt") || property.NameEquals("time") ||
                property.NameEquals("role") || property.NameEquals("content") || property.NameEquals("message") ||
                property.NameEquals("text") || property.NameEquals("agentId") || property.NameEquals("agent_id") ||
                property.NameEquals("sessionId") || property.NameEquals("session_id"))
            {
                continue;
            }

            metadata[property.Name] = property.Value.Clone();
        }

        return new SessionMessage(timestamp, role, content, agentId, sessionId, metadata.Count == 0 ? null : metadata);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        var raw = GetString(element, propertyName);
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static bool TryParseLine(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
