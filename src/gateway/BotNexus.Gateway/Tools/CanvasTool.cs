using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Configuration;

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
    IReadOnlyList<IAgentCanvasNotifier>? canvasNotifiers = null,
    CanvasToolOptions? options = null) : IAgentTool
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
    private readonly CanvasToolOptions _options = options ?? new CanvasToolOptions();

    public string Name => "canvas";
    public string Label => "Canvas";

    public Tool Definition => new(
        Name,
        "Publish Canvas tab HTML for the current agent scope. Use action='render' with html content to replace output, or action='clear' to clear output. Use set_state/get_state/clear_state for persistent key-value state. A successful 'render' returns a canvasUrl deep link to the Canvas tab for this conversation: INCLUDE that link in your reply so the user knows where to look, and still carry the substance of your answer in the reply itself rather than deferring entirely to the canvas. When no canvasUrl is returned, say what you rendered without inventing a URL. Rendered HTML has access to a 'window.canvasState' JavaScript API (get/set/delete/getAll/clear) that persists state server-side; the iframe can read and write the same state keys the agent uses via set_state/get_state. The canvasState bridge is injected synchronously before user scripts execute, so it is safe to use immediately without polling or ready-event checks. The bridge also exposes canvasState.submitToAgent({prompt, instructions}), which injects a user message into THIS conversation - and only this conversation - so the user can hand a completed form back to you with one click. USER-INITIATED ONLY: wire it to a button or an explicit user action. Do NOT call it from a timer, an interval, a render path, or automatically on load. The prompt is INSTRUCTION TEXT ONLY and must not carry canvas data - write the data into canvas state and tell the agent which keys to read back via get_state. Supply the prompt text yourself when you render the canvas (e.g. 'The user has completed the review form.') plus optional instructions naming the state keys holding the answers. It is rejected while you are mid-turn.",
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

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, BuildRenderMessage())]);
    }

    /// <summary>
    /// Composes the render result: the existing confirmation plus either the canvas deep link or a
    /// stated reason for its absence (#2975).
    /// </summary>
    /// <remarks>
    /// The confirmation is emitted unchanged and FIRST in every branch. A missing link is a missing
    /// convenience, not a failed render, and a result that led with the failure would read to the
    /// model as though the canvas had not been published.
    /// </remarks>
    private string BuildRenderMessage()
    {
        const string Rendered = "Canvas rendered for current agent.";

        if (_conversationId is null)
            return $"{Rendered} {CanvasDeepLink.NoConversationReason}";

        var baseUrl = CanvasDeepLink.ResolveBaseUrl(_options.PublicBaseUrl, _options.ListenUrl);
        if (!CanvasDeepLink.TryBuild(baseUrl, _agentId.Value, _conversationId.Value.Value, out var link))
            return $"{Rendered} {CanvasDeepLink.UnresolvableBaseUrlReason}";

        return $"{Rendered} canvasUrl: {link} - include this link in your reply so the user can open the canvas.";
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

        // Bound key length and value size at the tool boundary so an agent (or canvas JS writing
        // through the same store path) cannot bloat the conversation store with an oversized value
        // or unbounded distinct keys. Reject without writing, consistent with the other failure paths.
        if (_options.MaxKeyLength > 0 && key.Length > _options.MaxKeyLength)
        {
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                $"Failed to set state: key length {key.Length} exceeds the maximum of {_options.MaxKeyLength} characters.")]);
        }

        if (_options.MaxValueBytes > 0)
        {
            var valueByteCount = Encoding.UTF8.GetByteCount(value.GetRawText());
            if (valueByteCount > _options.MaxValueBytes)
            {
                return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text,
                    $"Failed to set state key '{key}': value size {valueByteCount} bytes exceeds the maximum of {_options.MaxValueBytes} bytes.")]);
            }
        }

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
