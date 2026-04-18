using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Memory.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Memory.Tools;

public sealed class MemoryGetTool : IAgentTool
{
    private readonly IMemoryStore _memoryStore;

    public MemoryGetTool(IMemoryStore memoryStore)
    {
        _memoryStore = memoryStore;
    }

    public string Name => "memory_get";

    public string Label => "Memory Get";

    public Tool Definition => new(
        Name,
        "Retrieve a specific memory entry by ID, or list recent memories from a session.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "Memory entry ID to retrieve"
                },
                "sessionId": {
                  "type": "string",
                  "description": "List memories from a specific session"
                },
                "limit": {
                  "type": "integer",
                  "description": "Max entries when listing by session (default: 20)"
                }
              }
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = arguments.TryGetValue("id", out var idValue) ? ToStringValue(idValue) : null;
        var sessionId = arguments.TryGetValue("sessionId", out var sessionValue) ? ToStringValue(sessionValue) : null;

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Provide either 'id' or 'sessionId'.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(id))
            prepared["id"] = id;
        if (!string.IsNullOrWhiteSpace(sessionId))
            prepared["sessionId"] = sessionId;

        if (arguments.TryGetValue("limit", out var limit) && limit is not null)
            prepared["limit"] = ToIntValue(limit, "limit");

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

        if (arguments.TryGetValue("id", out var idValue) && !string.IsNullOrWhiteSpace(ToStringValue(idValue)))
        {
            var memory = await _memoryStore.GetByIdAsync(ToStringValue(idValue)!, cancellationToken).ConfigureAwait(false);
            return memory is null
                ? new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Memory entry not found.")])
                : new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, FormatMemory(memory))]);
        }

        var sessionId = ToStringValue(arguments["sessionId"])!;
        var limit = arguments.TryGetValue("limit", out var limitValue) && limitValue is not null
            ? Math.Max(1, ToIntValue(limitValue, "limit"))
            : 20;

        var entries = await _memoryStore.GetBySessionAsync(sessionId, limit, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"No memory entries found for session '{sessionId}'.")]);

        List<string> lines =
        [
            $"Session '{sessionId}' memories ({entries.Count}):",
            string.Empty
        ];

        foreach (var entry in entries)
        {
            lines.Add(FormatMemory(entry));
            lines.Add(string.Empty);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, string.Join(Environment.NewLine, lines))]);
    }

    private static string FormatMemory(MemoryEntry entry)
    {
        var preview = entry.Content.Length > 400
            ? $"{entry.Content[..400]}..."
            : entry.Content;

        return string.Join(Environment.NewLine,
            $"ID: {entry.Id}",
            $"Timestamp: {entry.CreatedAt:O}",
            $"Source: {entry.SourceType}",
            $"Session: {entry.SessionId ?? "(none)"}",
            $"Content: {preview}");
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
