using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Agent tool for managing a per-conversation todo / task list.
/// </summary>
/// <remarks>
/// Step 2/6 of #1464 (per-conversation todo primitive). The plan lives as structured state
/// persisted on the conversation row (<see cref="Conversation.TodoJson"/>, added in step 1),
/// not as free-text prose. This is the structural complement to the <c>&lt;tool_use&gt;</c>
/// anti-narration trip-wire (#1463): narration cannot flip a checkbox; only a real tool result can.
/// The model's job each turn becomes "advance ONE item from <c>[ ]</c> to <c>[x]</c>", and step 4
/// couples a <c>done</c> transition to a same-turn accomplishing tool result so it has teeth.
/// </remarks>
public sealed class TodoTool(
    ConversationId? conversationId,
    IConversationStore? conversationStore = null,
    AgentId? agentId = null,
    IReadOnlyList<IAgentTodoNotifier>? todoNotifiers = null) : IAgentTool
{
    /// <summary>
    /// Serializer options for the persisted <see cref="Conversation.TodoJson"/> payload.
    /// camelCase to match the documented item shape and to render compactly in the prompt section (step 3).
    /// </summary>
    internal static readonly JsonSerializerOptions TodoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonElement ToolSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["write", "update", "list", "clear"],
              "description": "Todo action: 'write' replaces the whole list, 'update' changes one item by id (or appends if new), 'list' returns the current items, 'clear' empties the list."
            },
            "items": {
              "type": "array",
              "description": "For action='write': the full set of items to persist. Each item may set 'text' (required) and optional 'status' and 'id'.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "Optional stable id. Generated if omitted." },
                  "text": { "type": "string", "description": "The task text." },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "done", "cancelled"], "description": "Item status. Defaults to 'pending'." }
                },
                "required": ["text"]
              }
            },
            "id": {
              "type": "string",
              "description": "For action='update': the id of the item to change. If no item has this id, a new item is appended."
            },
            "text": {
              "type": "string",
              "description": "For action='update': new text for the item (optional)."
            },
            "status": {
              "type": "string",
              "enum": ["pending", "in_progress", "done", "cancelled"],
              "description": "For action='update': new status for the item (optional)."
            }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private readonly ConversationId? _conversationId = conversationId;
    private readonly IConversationStore? _conversationStore = conversationStore;
    private readonly AgentId? _agentId = agentId;
    private readonly IReadOnlyList<IAgentTodoNotifier> _todoNotifiers = todoNotifiers ?? [];

    public string Name => "todo";
    public string Label => "Todo";

    public Tool Definition => new(
        Name,
        "Manage a per-conversation execution checklist for the current agent loop. "
        + "Use it to decompose a higher-level TaskNexus task or direct user request into detailed, resumable steps "
        + "for sequencing, checkpoints, retries, validation, deployment, and handoff. "
        + "The checklist survives context compaction, interruption, and session continuation. "
        + "One TaskNexus task may map to many todo items. "
        + "TaskNexus remains the durable system of record for higher-level outcomes, ownership, priority, due dates, provenance, history, and cross-agent reporting. "
        + "Do not use todo instead of TaskNexus for durable, assigned, cross-session, or user-visible work. "
        + "Do not create a TaskNexus task for every implementation step unless that step independently needs long-term ownership or tracking. "
        + "Use action='write' to replace the whole list, 'update' to change one item by id, 'list' to read it, and 'clear' to empty it. "
        + "Status is pending|in_progress|done|cancelled. "
        + "Mark an item done only after the corresponding work is verified by a tool result this turn; narration does not flip a checkbox.",
        ToolSchema);

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadRequiredString(arguments, "action").Trim().ToLowerInvariant();

        switch (action)
        {
            case "list":
            case "clear":
                break;
            case "write":
                if (!arguments.ContainsKey("items"))
                    throw new ArgumentException("Argument 'items' is required when action is 'write'.");
                break;
            case "update":
                if (string.IsNullOrWhiteSpace(ReadString(arguments, "id")))
                    throw new ArgumentException("Argument 'id' is required when action is 'update'.");
                break;
            default:
                throw new ArgumentException("Argument 'action' must be one of: write, update, list, clear.");
        }

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadRequiredString(arguments, "action").Trim().ToLowerInvariant();

        if (_conversationId is null || _conversationStore is null)
        {
            return Text("Todo is not available: no conversation context or store configured.");
        }

        return action switch
        {
            "write" => await ExecuteWriteAsync(arguments, cancellationToken).ConfigureAwait(false),
            "update" => await ExecuteUpdateAsync(arguments, cancellationToken).ConfigureAwait(false),
            "list" => await ExecuteListAsync(cancellationToken).ConfigureAwait(false),
            "clear" => await ExecuteClearAsync(cancellationToken).ConfigureAwait(false),
            _ => Text($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ExecuteWriteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore!.GetAsync(_conversationId!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
            return Text("Conversation not found.");

        var now = DateTimeOffset.UtcNow;
        var incoming = ReadItems(arguments, "items");
        var items = new List<TodoItem>(incoming.Count);
        foreach (var item in incoming)
        {
            var text = item.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue; // skip empty rows rather than persisting blank tasks

            items.Add(new TodoItem
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id!,
                Text = text,
                Status = NormalizeStatus(item.Status),
                CreatedAt = item.CreatedAt ?? now,
                UpdatedAt = now,
            });
        }

        await SaveListAsync(conversation, items, cancellationToken).ConfigureAwait(false);
        return Text($"Todo list set with {items.Count} item(s).\n{Render(items)}");
    }

    private async Task<AgentToolResult> ExecuteUpdateAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore!.GetAsync(_conversationId!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
            return Text("Conversation not found.");

        var id = ReadRequiredString(arguments, "id").Trim();
        var newText = ReadString(arguments, "text")?.Trim();
        var newStatusRaw = ReadString(arguments, "status");
        var now = DateTimeOffset.UtcNow;

        var items = Parse(conversation.TodoJson);
        var existing = items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

        if (existing is null)
        {
            // No item with this id -> append a new one (per spec: "or append if new").
            if (string.IsNullOrEmpty(newText))
                return Text($"Cannot append a new todo item with id '{id}': 'text' is required when the id does not exist.");

            items.Add(new TodoItem
            {
                Id = id,
                Text = newText,
                Status = NormalizeStatus(newStatusRaw),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await SaveListAsync(conversation, items, cancellationToken).ConfigureAwait(false);
            return Text($"Appended new todo item '{id}'.\n{Render(items)}");
        }

        if (!string.IsNullOrEmpty(newText))
            existing.Text = newText;
        if (!string.IsNullOrWhiteSpace(newStatusRaw))
            existing.Status = NormalizeStatus(newStatusRaw);
        existing.UpdatedAt = now;

        await SaveListAsync(conversation, items, cancellationToken).ConfigureAwait(false);
        return Text($"Updated todo item '{id}'.\n{Render(items)}");
    }

    private async Task<AgentToolResult> ExecuteListAsync(CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore!.GetAsync(_conversationId!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
            return Text("Conversation not found.");

        var items = Parse(conversation.TodoJson);
        return items.Count == 0
            ? Text("Todo list is empty.")
            : Text(Render(items));
    }

    private async Task<AgentToolResult> ExecuteClearAsync(CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore!.GetAsync(_conversationId!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
            return Text("Conversation not found.");

        await SaveListAsync(conversation, [], cancellationToken).ConfigureAwait(false);
        return Text("Todo list cleared.");
    }

    private async Task SaveListAsync(Conversation conversation, IReadOnlyList<TodoItem> items, CancellationToken cancellationToken)
    {
        var json = items.Count == 0
            ? null
            : JsonSerializer.Serialize(new TodoDocument { Items = [.. items] }, TodoJsonOptions);

        // Persist via SaveAsync, which holds the store's per-conversation write lock -- the existing
        // write protection. Use a record `with` so the cached/loaded instance is not mutated in place.
        var updated = conversation with { TodoJson = json };
        await _conversationStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        // Fan the change out to live transports (e.g. the portal Todo panel) after the write commits.
        // Best-effort: a broadcast failure must not fail the tool call. Mirrors the canvas notify path.
        if (_todoNotifiers.Count > 0 && _conversationId is not null)
        {
            var agentIdValue = _agentId?.Value ?? string.Empty;
            var conversationIdValue = _conversationId.Value.Value;
            foreach (var notifier in _todoNotifiers)
            {
                try
                {
                    await notifier.NotifyTodoUpdatedAsync(agentIdValue, conversationIdValue, json, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Live-update notification is best-effort; never fail the tool on a broadcast error.
                }
            }
        }
    }

    /// <summary>Parses the persisted todo JSON into a mutable working list. Returns an empty list on null/blank/malformed input.</summary>
    internal static List<TodoItem> Parse(string? todoJson)
    {
        if (string.IsNullOrWhiteSpace(todoJson))
            return [];

        try
        {
            var doc = JsonSerializer.Deserialize<TodoDocument>(todoJson, TodoJsonOptions);
            return doc?.Items is null ? [] : [.. doc.Items];
        }
        catch (JsonException)
        {
            // Tolerate a corrupt/legacy payload rather than throwing on a hot tool path.
            return [];
        }
    }

    private static string NormalizeStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "in_progress" or "done" or "cancelled" or "pending" => normalized,
            _ => "pending",
        };
    }

    private static string Render(IReadOnlyList<TodoItem> items)
    {
        if (items.Count == 0)
            return "(no items)";

        return string.Join('\n', items.Select(i =>
        {
            var box = i.Status switch
            {
                "done" => "[x]",
                "in_progress" => "[~]",
                "cancelled" => "[-]",
                _ => "[ ]",
            };
            return $"{box} {i.Text} (id={i.Id})";
        }));
    }

    private static IReadOnlyList<TodoItemInput> ReadItems(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return [];

        JsonElement element;
        if (value is JsonElement je)
        {
            element = je;
        }
        else
        {
            element = JsonSerializer.SerializeToElement(value);
        }

        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<TodoItemInput>(element.GetArrayLength());
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            result.Add(new TodoItemInput
            {
                Id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null,
                Text = entry.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String ? textEl.GetString() : null,
                Status = entry.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String ? statusEl.GetString() : null,
                CreatedAt = entry.TryGetProperty("createdAt", out var createdEl) && createdEl.TryGetDateTimeOffset(out var created) ? created : null,
            });
        }

        return result;
    }

    private static AgentToolResult Text(string message)
        => new([new AgentToolContent(AgentToolContentType.Text, message)]);

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        var value = ReadString(arguments, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required argument: {key}.");
        return value;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>The persisted document shape stored in <see cref="Conversation.TodoJson"/>.</summary>
    internal sealed class TodoDocument
    {
        [JsonPropertyName("items")]
        public List<TodoItem> Items { get; set; } = [];
    }

    /// <summary>A single persisted todo item.</summary>
    internal sealed class TodoItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending";

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>An incoming item from the tool's <c>write</c> argument (all fields optional except text).</summary>
    private sealed class TodoItemInput
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
    }
}
