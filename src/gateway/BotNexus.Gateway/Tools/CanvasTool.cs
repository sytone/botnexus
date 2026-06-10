using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Agent tool for canvas HTML rendering and key-value state management.
/// Supports render/clear for HTML output, and set_state/get_state/clear_state for
/// persistent conversation-scoped state accessible to both agent and canvas JS.
/// </summary>
public sealed class CanvasTool(
    AgentId agentId,
    ConversationId? conversationId,
    IConversationStore? conversationStore = null,
    IReadOnlyList<IAgentCanvasNotifier>? canvasNotifiers = null) : IAgentTool
{
    private static readonly JsonElement ToolSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["render", "clear", "set_state", "get_state", "clear_state"],
              "description": "Canvas action to perform."
            },
            "html": {
              "type": "string",
              "description": "HTML payload to render when action is 'render'."
            },
            "key": {
              "type": "string",
              "description": "State key for set_state, get_state (single key), or omit for get_state (all keys)."
            },
            "value": {
              "description": "JSON value to store when action is 'set_state'."
            }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private readonly AgentId _agentId = agentId;
    private readonly ConversationId? _conversationId = conversationId;
    private readonly IConversationStore? _conversationStore = conversationStore;
    private readonly IReadOnlyList<IAgentCanvasNotifier> _canvasNotifiers = canvasNotifiers ?? [];

    public string Name => "canvas";
    public string Label => "Canvas";

    public Tool Definition => new(
        Name,
        "Publish Canvas tab HTML for the current agent scope. Use action='render' with html content to replace output, or action='clear' to clear output. Use set_state/get_state/clear_state for persistent key-value state. Rendered HTML has access to a 'window.canvasState' JavaScript API (get/set/delete/getAll/clear) that persists state server-side; the iframe can read and write the same state keys the agent uses via set_state/get_state.",
        ToolSchema);

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadRequiredString(arguments, "action").Trim().ToLowerInvariant();

        switch (action)
        {
            case "render":
                if (string.IsNullOrWhiteSpace(ReadString(arguments, "html")))
                    throw new ArgumentException("Argument 'html' is required when action is 'render'.");
                break;
            case "clear":
            case "clear_state":
            case "get_state":
                break;
            case "set_state":
                if (string.IsNullOrWhiteSpace(ReadString(arguments, "key")))
                    throw new ArgumentException("Argument 'key' is required when action is 'set_state'.");
                if (!arguments.ContainsKey("value"))
                    throw new ArgumentException("Argument 'value' is required when action is 'set_state'.");
                break;
            default:
                throw new ArgumentException(
                    "Argument 'action' must be one of: render, clear, set_state, get_state, clear_state.");
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

        return action switch
        {
            "render" => await ExecuteRenderAsync(arguments, cancellationToken).ConfigureAwait(false),
            "clear" => await ExecuteClearCanvasAsync(cancellationToken).ConfigureAwait(false),
            "set_state" => await ExecuteSetStateAsync(arguments, cancellationToken).ConfigureAwait(false),
            "get_state" => await ExecuteGetStateAsync(arguments, cancellationToken).ConfigureAwait(false),
            "clear_state" => await ExecuteClearStateAsync(cancellationToken).ConfigureAwait(false),
            _ => new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Unknown action: {action}")])
        };
    }

    private async Task<AgentToolResult> ExecuteRenderAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var html = ReadString(arguments, "html") ?? string.Empty;
        var conversationIdValue = _conversationId?.Value ?? string.Empty;

        foreach (var notifier in _canvasNotifiers)
        {
            await notifier.NotifyCanvasUpdatedAsync(_agentId.Value, conversationIdValue, html, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Canvas rendered for current agent.")]);
    }

    private async Task<AgentToolResult> ExecuteClearCanvasAsync(CancellationToken cancellationToken)
    {
        var conversationIdValue = _conversationId?.Value ?? string.Empty;

        foreach (var notifier in _canvasNotifiers)
        {
            await notifier.NotifyCanvasUpdatedAsync(_agentId.Value, conversationIdValue, string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Canvas cleared for current agent.")]);
    }

    private async Task<AgentToolResult> ExecuteSetStateAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (_conversationId is null || _conversationStore is null)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                "Canvas state is not available: no conversation context or store configured.")]);
        }

        var key = ReadRequiredString(arguments, "key");
        var value = GetJsonValue(arguments, "value");

        var success = await _conversationStore.SetCanvasStateKeyAsync(_conversationId.Value, key, value, cancellationToken)
            .ConfigureAwait(false);

        if (success)
        {
            foreach (var notifier in _canvasNotifiers)
            {
                await notifier.NotifyCanvasStateChangedAsync(_conversationId.Value.Value, key, value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var message = success
            ? $"State key '{key}' set successfully."
            : $"Failed to set state key '{key}': conversation not found.";

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
    }

    private async Task<AgentToolResult> ExecuteGetStateAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (_conversationId is null || _conversationStore is null)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                "Canvas state is not available: no conversation context or store configured.")]);
        }

        var key = ReadString(arguments, "key");

        if (!string.IsNullOrWhiteSpace(key))
        {
            // Single key lookup
            var state = await _conversationStore.GetCanvasStateAsync(_conversationId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (state is null)
            {
                return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                    "Conversation not found.")]);
            }

            if (state.TryGetValue(key, out var value))
            {
                return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, value.ToString())]);
            }

            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                $"Key '{key}' not found in canvas state.")]);
        }

        // All keys
        var allState = await _conversationStore.GetCanvasStateAsync(_conversationId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (allState is null)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Conversation not found.")]);
        }

        if (allState.Count == 0)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Canvas state is empty.")]);
        }

        var json = JsonSerializer.Serialize(allState, new JsonSerializerOptions { WriteIndented = true });
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, json)]);
    }

    private async Task<AgentToolResult> ExecuteClearStateAsync(CancellationToken cancellationToken)
    {
        if (_conversationId is null || _conversationStore is null)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                "Canvas state is not available: no conversation context or store configured.")]);
        }

        await _conversationStore.ClearCanvasStateAsync(_conversationId.Value, cancellationToken).ConfigureAwait(false);

        foreach (var notifier in _canvasNotifiers)
        {
            await notifier.NotifyCanvasStateChangedAsync(_conversationId.Value.Value, "*", null, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
            "All canvas state cleared for this conversation.")]);
    }

    private static JsonElement GetJsonValue(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }

        if (value is JsonElement element)
        {
            return element;
        }

        // Serialize non-JsonElement values to JsonElement
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

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
            _ => value.ToString()
        };
    }
}
