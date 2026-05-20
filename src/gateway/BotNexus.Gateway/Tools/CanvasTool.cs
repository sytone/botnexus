using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
namespace BotNexus.Gateway.Tools;
public sealed class CanvasTool(
    AgentId agentId,
    ConversationId? conversationId,
    IReadOnlyList<IAgentCanvasNotifier>? canvasNotifiers = null) : IAgentTool
{
    private readonly AgentId _agentId = agentId;
    private readonly string _conversationId = conversationId?.Value ?? string.Empty;
    private readonly IReadOnlyList<IAgentCanvasNotifier> _canvasNotifiers = canvasNotifiers ?? [];
    public string Name => "canvas";
    public string Label => "Canvas";
    public Tool Definition => new(
        Name,
        "Publish Canvas tab HTML for the current agent scope. Use action='render' with html content to replace output, or action='clear' to clear output.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["render", "clear"],
                  "description": "Canvas action to perform."
                },
                "html": {
                  "type": "string",
                  "description": "HTML payload to render when action is 'render'."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadRequiredString(arguments, "action").Trim().ToLowerInvariant();
        if (action is not ("render" or "clear"))
            throw new ArgumentException("Argument 'action' must be either 'render' or 'clear'.");
        if (action == "render" && string.IsNullOrWhiteSpace(ReadString(arguments, "html")))
            throw new ArgumentException("Argument 'html' is required when action is 'render'.");
        return Task.FromResult(arguments);
    }
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = ReadRequiredString(arguments, "action").Trim().ToLowerInvariant();
        var html = action == "clear" ? string.Empty : ReadString(arguments, "html") ?? string.Empty;
        foreach (var notifier in _canvasNotifiers)
            await notifier.NotifyCanvasUpdatedAsync(_agentId.Value, _conversationId, html, cancellationToken).ConfigureAwait(false);
        var message = action == "clear"
            ? "Canvas cleared for current agent."
            : "Canvas rendered for current agent.";
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
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