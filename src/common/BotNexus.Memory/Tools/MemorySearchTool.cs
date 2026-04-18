using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Memory.Tools;

public sealed class MemorySearchTool : IAgentTool
{
    private readonly IMemoryStore _memoryStore;
    private readonly int _defaultTopK;

    public MemorySearchTool(IMemoryStore memoryStore, MemoryAgentConfig? config = null)
    {
        _memoryStore = memoryStore;
        _defaultTopK = Math.Max(1, config?.Search?.DefaultTopK ?? 10);
    }

    public string Name => "memory_search";

    public string Label => "Memory Search";

    public Tool Definition => new(
        Name,
        "Search the agent's persistent memory across all past sessions. Use natural language queries to find relevant past conversations, decisions, facts, and stored knowledge. Results are ranked by relevance with optional temporal decay (recent memories score higher).",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "Natural language search query"
                },
                "topK": {
                  "type": "integer",
                  "description": "Maximum number of results to return (default: 10)"
                },
                "filter": {
                  "type": "object",
                  "description": "Optional filters: { sourceType, sessionId, afterDate, beforeDate, tags }",
                  "properties": {
                    "sourceType": { "type": "string" },
                    "sessionId": { "type": "string" },
                    "afterDate": { "type": "string" },
                    "beforeDate": { "type": "string" },
                    "tags": { "type": "array", "items": { "type": "string" } }
                  }
                }
              },
              "required": ["query"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("query", out var queryValue) || string.IsNullOrWhiteSpace(ToStringValue(queryValue)))
            throw new ArgumentException("Missing required argument: query.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = ToStringValue(queryValue)!
        };

        if (arguments.TryGetValue("topK", out var topK) && topK is not null)
            prepared["topK"] = ToIntValue(topK, "topK");

        if (arguments.TryGetValue("filter", out var filter) && filter is not null)
            prepared["filter"] = filter;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        return ExecuteCoreAsync(arguments, cancellationToken);
    }

    private async Task<AgentToolResult> ExecuteCoreAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var query = ToStringValue(arguments["query"]) ?? string.Empty;
        var topK = arguments.TryGetValue("topK", out var topKValue) && topKValue is not null
            ? Math.Max(1, ToIntValue(topKValue, "topK"))
            : _defaultTopK;
        var filter = ParseFilter(arguments.TryGetValue("filter", out var filterValue) ? filterValue : null);

        var entries = await _memoryStore.SearchAsync(query, topK, filter, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "No matching memories found.")]);

        var lines = new List<string>(entries.Count * 6)
        {
            $"Found {entries.Count} memory entr{(entries.Count == 1 ? "y" : "ies")}:",
            string.Empty
        };

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var preview = entry.Content.Length > 240
                ? $"{entry.Content[..240]}..."
                : entry.Content;
            preview = preview.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');

            lines.Add($"[{i + 1}] ID: {entry.Id}");
            lines.Add($"Score: #{i + 1} (ranked)");
            lines.Add($"Timestamp: {entry.CreatedAt:O}");
            if (!string.IsNullOrWhiteSpace(entry.SessionId))
                lines.Add($"Session: {entry.SessionId}");
            lines.Add($"Source: {entry.SourceType}");
            lines.Add($"Preview: {preview}");
            lines.Add(string.Empty);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, string.Join(Environment.NewLine, lines))]);
    }

    private static MemorySearchFilter? ParseFilter(object? value)
    {
        if (value is null)
            return null;

        JsonElement element = value switch
        {
            JsonElement jsonElement => jsonElement,
            string text when !string.IsNullOrWhiteSpace(text) => JsonDocument.Parse(text).RootElement.Clone(),
            _ => JsonSerializer.SerializeToElement(value)
        };

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return new MemorySearchFilter
        {
            SourceType = ReadOptionalString(element, "sourceType"),
            SessionId = ReadOptionalString(element, "sessionId"),
            AfterDate = ReadOptionalDate(element, "afterDate"),
            BeforeDate = ReadOptionalDate(element, "beforeDate"),
            Tags = ReadOptionalStringArray(element, "tags")
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return null;

        List<string> tags = [];
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                tags.Add(item.GetString()!);
        }

        return tags.Count == 0 ? null : tags;
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };

    private static int ToIntValue(object value, string argumentName)
        => value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var d) => (int)d,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };
}
