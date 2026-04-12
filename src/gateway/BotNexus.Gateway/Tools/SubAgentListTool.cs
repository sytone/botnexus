using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentListTool(
    ISubAgentManager subAgentManager,
    SessionId sessionId) : IAgentTool
{
    public string Name => "list_subagents";
    public string Label => "List Sub-Agents";

    public Tool Definition => new(
        Name,
        "List active and completed sub-agents for the current session.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {}
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var subAgents = await subAgentManager.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Serialize(new { SubAgents = subAgents }, JsonOptions);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
