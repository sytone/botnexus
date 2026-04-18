using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Memory.Tools;

public sealed class MemoryStoreTool : IAgentTool
{
    private readonly IMemoryStore _memoryStore;
    private readonly string _agentId;

    public MemoryStoreTool(IMemoryStore memoryStore, string agentId)
    {
        _memoryStore = memoryStore;
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
    }

    public string Name => "memory_store";

    public string Label => "Memory Store";

    public Tool Definition => new(
        Name,
        "Explicitly store important information in persistent memory. Use for key decisions, facts, preferences, or distilled knowledge the agent wants to recall later.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "content": {
                  "type": "string",
                  "description": "Text content to store"
                },
                "tags": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional tags for categorization"
                },
                "expiresInDays": {
                  "type": "integer",
                  "description": "Optional TTL in days"
                }
              },
              "required": ["content"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("content", out var contentValue) || string.IsNullOrWhiteSpace(ToStringValue(contentValue)))
            throw new ArgumentException("Missing required argument: content.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = ToStringValue(contentValue)!
        };

        if (arguments.TryGetValue("tags", out var tags) && tags is not null)
            prepared["tags"] = tags;

        if (arguments.TryGetValue("expiresInDays", out var expiresInDays) && expiresInDays is not null)
            prepared["expiresInDays"] = ToIntValue(expiresInDays, "expiresInDays");

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

        var content = ToStringValue(arguments["content"])!;
        var expiresInDays = arguments.TryGetValue("expiresInDays", out var expiresValue) && expiresValue is not null
            ? Math.Max(1, ToIntValue(expiresValue, "expiresInDays"))
            : (int?)null;
        var tags = ParseTags(arguments.TryGetValue("tags", out var tagsValue) ? tagsValue : null);

        var metadataJson = tags.Count == 0
            ? null
            : JsonSerializer.Serialize(new { tags });

        var now = DateTimeOffset.UtcNow;
        var entry = new MemoryEntry
        {
            Id = string.Empty,
            AgentId = _agentId,
            SessionId = null,
            TurnIndex = null,
            SourceType = "manual",
            Content = content,
            MetadataJson = metadataJson,
            Embedding = null,
            CreatedAt = now,
            UpdatedAt = null,
            ExpiresAt = expiresInDays is null ? null : now.AddDays(expiresInDays.Value),
            IsArchived = false
        };

        var inserted = await _memoryStore.InsertAsync(entry, cancellationToken).ConfigureAwait(false);
        var response = $"Stored memory entry {inserted.Id} at {inserted.CreatedAt:O}.";
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, response)]);
    }

    private static List<string> ParseTags(object? value)
    {
        if (value is null)
            return [];

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            List<string> tags = [];
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    tags.Add(item.GetString()!);
            }

            return tags;
        }

        if (value is IEnumerable<object?> values)
            return values.Select(ToStringValue).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag!).ToList();

        return [];
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
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var parsedDouble) => (int)parsedDouble,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var parsedDouble) => (int)parsedDouble,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };
}
