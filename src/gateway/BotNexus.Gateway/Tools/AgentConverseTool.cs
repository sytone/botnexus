using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Conversations;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class AgentConverseTool(
    IAgentConversationService conversationService,
    ISessionStore sessionStore,
    AgentId initiatorAgentId,
    SessionId sessionId) : IAgentTool
{
    public string Name => "agent_converse";
    public string Label => "Agent Converse";

    public Tool Definition => new(
        Name,
        "Start a conversation with another registered agent.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "agentId": { "type": "string", "description": "The target agent's ID." },
                "message": { "type": "string", "description": "Opening message to send." },
                "objective": { "type": "string", "description": "What you want to achieve." },
                "maxTurns": {
                  "type": "integer",
                  "minimum": 1,
                  "default": 1,
                  "description": "Maximum number of turns."
                }
              },
              "required": ["agentId", "message"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "agentId")))
            throw new ArgumentException("Missing required argument: agentId.");
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "message")))
            throw new ArgumentException("Missing required argument: message.");
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var targetAgentId = ReadString(arguments, "agentId")
            ?? throw new ArgumentException("Missing required argument: agentId.");
        var message = ReadString(arguments, "message")
            ?? throw new ArgumentException("Missing required argument: message.");

        var result = await conversationService.ConverseAsync(
            new ConversationRequest
            {
                InitiatorId = initiatorAgentId,
                TargetId = AgentId.From(targetAgentId),
                Message = message,
                Objective = ReadString(arguments, "objective"),
                MaxTurns = Math.Max(1, ReadInt(arguments, "maxTurns", 1)),
                CallChain = await ResolveCallChainAsync(cancellationToken).ConfigureAwait(false)
            },
            cancellationToken).ConfigureAwait(false);

        return new AgentToolResult(
            [
                new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(result, JsonOptions))
            ]);
    }

    private async Task<IReadOnlyList<AgentId>> ResolveCallChainAsync(CancellationToken cancellationToken)
    {
        var currentSession = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (currentSession is null || !currentSession.Metadata.TryGetValue("callChain", out var raw) || raw is null)
            return [initiatorAgentId];

        var parsed = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => AgentId.From(item!))
                    .ToArray(),
            IEnumerable<string> values =>
                values.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(AgentId.From)
                    .ToArray(),
            _ => []
        };

        return parsed.Length == 0 ? [initiatorAgentId] : parsed;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            int parsed => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
