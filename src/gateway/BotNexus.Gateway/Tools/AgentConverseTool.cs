using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class AgentConverseTool(
    IAgentExchangeService conversationService,
    ISessionStore sessionStore,
    AgentId initiatorAgentId,
    SessionId sessionId,
    AgentExchangeOptions? exchangeOptions = null) : IAgentTool
{
    private const int DefaultTimeoutSeconds = 600;
    private const int MaxTimeoutSeconds = 1800;

    private readonly AgentExchangeOptions _exchangeOptions = exchangeOptions ?? new AgentExchangeOptions();

    public string Name => "agent_converse";
    public string Label => "Agent Converse";

    /// <summary>
    /// Reserves enough executor time for substantive peer work while individual calls may request a shorter bounded budget.
    /// </summary>
    public TimeSpan? DefaultTimeout => TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public Tool Definition => new(
        Name,
        "Start a conversation with another registered agent. Not every agent is reachable: converse is governed by policy. Call list_agents first and only target an agent whose 'canConverse' is true -- targeting an agent with canConverse=false is a deterministic policy denial that wastes the turn and will never succeed on retry.",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "agentId": { "type": "string", "description": "The target agent's ID. Must be an agent whose 'canConverse' is true in list_agents output; otherwise the call is denied by policy." },
                "message": { "type": "string", "description": "Opening message to send." },
                "objective": { "type": "string", "description": "What you want to achieve." },
                "timeoutSeconds": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": {{MaxTimeoutSeconds}},
                  "default": {{DefaultTimeoutSeconds}},
                  "description": "Wall-clock budget in seconds for this exchange (default 10 minutes, maximum 30 minutes). The 30-minute hard maximum prevents abandoned peer exchanges from consuming executor capacity indefinitely."
                },
                "maxTurns": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": {{_exchangeOptions.EffectiveMaxTurnsCeiling}},
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

        var prepared = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);
        var timeoutSeconds = ReadTimeoutSeconds(arguments);
        prepared["timeoutSeconds"] = timeoutSeconds;
        // ToolExecutor recognises `timeout` as seconds. Keeping the public schema name explicit avoids
        // colliding with tools whose timeout unit is implicit while still enforcing this call budget.
        prepared["timeout"] = timeoutSeconds;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
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

        var timeoutSeconds = ReadTimeoutSeconds(arguments);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var result = await conversationService.ConverseAsync(
            new AgentExchangeRequest
            {
                InitiatorId = initiatorAgentId,
                TargetId = AgentId.From(targetAgentId),
                Message = message,
                Objective = ReadString(arguments, "objective"),
                MaxTurns = Math.Clamp(ReadInt(arguments, "maxTurns", 1), 1, _exchangeOptions.EffectiveMaxTurnsCeiling),
                CallChain = await ResolveCallChainAsync(timeoutCts.Token).ConfigureAwait(false)
            },
            timeoutCts.Token).ConfigureAwait(false);

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

    private static int ReadTimeoutSeconds(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("timeoutSeconds", out var rawTimeout) || rawTimeout is null)
            return DefaultTimeoutSeconds;

        var parsed = rawTimeout switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var value) => value,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var value) => value,
            int value => value,
            string text when int.TryParse(text, out var value) => value,
            _ => throw new ArgumentException("timeoutSeconds must be an integer.", nameof(arguments))
        };

        if (parsed < 1)
            throw new ArgumentOutOfRangeException(nameof(arguments), "timeoutSeconds must be at least 1 second.");

        return Math.Min(parsed, MaxTimeoutSeconds);
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
