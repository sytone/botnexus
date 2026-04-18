using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentManageTool(
    ISubAgentManager subAgentManager,
    SessionId sessionId) : IAgentTool
{
    public string Name => "manage_subagent";
    public string Label => "Manage Sub-Agent";

    public Tool Definition => new(
        Name,
        "Get status or kill a sub-agent for this session.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "subAgentId": {
                  "type": "string",
                  "description": "Sub-agent identifier."
                },
                "action": {
                  "type": "string",
                  "enum": ["status", "kill"],
                  "description": "Management action."
                }
              },
              "required": ["subAgentId", "action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var subAgentId = ReadString(arguments, "subAgentId");
        if (string.IsNullOrWhiteSpace(subAgentId))
            throw new ArgumentException("Missing required argument: subAgentId.");

        var action = ReadString(arguments, "action");
        if (!string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Argument 'action' must be 'status' or 'kill'.");

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var subAgentId = ReadString(arguments, "subAgentId")
            ?? throw new ArgumentException("Missing required argument: subAgentId.");
        var action = ReadString(arguments, "action")
            ?? throw new ArgumentException("Missing required argument: action.");

        if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
        {
            var killed = await subAgentManager.KillAsync(subAgentId, sessionId, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Serialize(new { SubAgentId = subAgentId, Killed = killed }, JsonOptions);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, response)]);
        }

        var info = await subAgentManager.GetAsync(subAgentId, cancellationToken).ConfigureAwait(false);
        if (info is null)
            throw new KeyNotFoundException($"Sub-agent '{subAgentId}' was not found.");
        if (!string.Equals(info.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sub-agent does not belong to the current session.");

        var result = JsonSerializer.Serialize(new
        {
            info.SubAgentId,
            info.Status,
            info.ResultSummary,
            info.StartedAt,
            info.CompletedAt
        }, JsonOptions);

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
