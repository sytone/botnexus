using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Tool that lets agents discover other registered agents and their capabilities.
/// </summary>
public sealed class ListAgentsTool(
    IAgentRegistry agentRegistry,
    AgentId callerAgentId) : IAgentTool
{
    public string Name => "list_agents";
    public string Label => "List Agents";

    public Tool Definition => new(
        Name,
        "List registered agents and their capabilities. Use to discover available specialists before calling agent_converse or spawn_subagent.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "filter": {
                  "type": "string",
                  "description": "Optional free-text filter applied to agent ID, display name, or description (case-insensitive)."
                },
                "capability": {
                  "type": "string",
                  "description": "Optional capability or archetype hint (e.g. research, code, planning)."
                }
              },
              "required": []
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filter = ReadString(arguments, "filter");
        var capability = ReadString(arguments, "capability");

        var callerDescriptor = agentRegistry.Get(callerAgentId);
        var subAgentIds = callerDescriptor?.SubAgentIds ?? [];

        var agents = agentRegistry.GetAll()
            .Where(d => MatchesFilter(d, filter))
            .Where(d => MatchesCapability(d, capability))
            .Select(d => new AgentEntry(
                AgentId: d.AgentId.ToString(),
                DisplayName: d.DisplayName,
                Description: d.Description,
                Emoji: d.Emoji,
                Capabilities: ResolveCapabilities(d),
                CanConverse: subAgentIds.Contains(d.AgentId.ToString(), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(e => e.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(agents, JsonOptions);
        return Task.FromResult(new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, json)]));
    }

    private static bool MatchesFilter(AgentDescriptor d, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return d.AgentId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || d.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (d.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesCapability(AgentDescriptor d, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return true;
        var caps = ResolveCapabilities(d);
        return caps.Any(c => c.Contains(capability, StringComparison.OrdinalIgnoreCase))
            || (d.Description?.Contains(capability, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static IReadOnlyList<string> ResolveCapabilities(AgentDescriptor d)
    {
        if (d.Metadata.TryGetValue("capabilities", out var raw) && raw is not null)
        {
            if (raw is JsonElement { ValueKind: JsonValueKind.Array } element)
            {
                return element.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            if (raw is IEnumerable<string> strings)
                return strings.ToList();
            if (raw is string single)
                return [single];
        }
        return [];
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AgentEntry(
        string AgentId,
        string DisplayName,
        string? Description,
        string? Emoji,
        IReadOnlyList<string> Capabilities,
        bool CanConverse);
}
