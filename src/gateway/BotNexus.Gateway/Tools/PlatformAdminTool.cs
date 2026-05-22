using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Platform admin tool that provides safe, validated access to Gateway management operations.
/// Agents use this tool instead of writing directly to config.json or agent files.
/// </summary>
/// <remarks>
/// Operations: get_config, list_agents, get_platform_status, create_agent, update_agent, delete_agent.
/// All mutating operations go through <see cref="IAgentRegistry"/> and
/// <see cref="IAgentConfigurationWriter"/> — they never touch files directly.
/// </remarks>
public sealed class PlatformAdminTool(
    IAgentRegistry agentRegistry,
    IAgentSupervisor agentSupervisor,
    IAgentConfigurationWriter configWriter,
    IOptionsMonitor<PlatformConfig> platformConfig) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string Name => "botnexus_admin";
    public string Label => "BotNexus Platform Admin";

    public Tool Definition => new(
        Name,
        """
        Safe agent-facing API for BotNexus platform management.
        Use this tool instead of writing config.json or agent files directly.
        All mutations are validated and committed through the Gateway's own config layer.

        Actions:
        - get_config: Read current effective platform configuration summary (secrets redacted)
        - list_agents: List all registered agents with metadata and running status
        - get_platform_status: Get gateway agent counts and server time
        - create_agent: Register a new agent (requires agentId, displayName, modelId, apiProvider)
        - update_agent: Update an existing agent's properties (only provided fields are changed)
        - delete_agent: Unregister an agent (does not stop a running instance)
        """,
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["get_config", "list_agents", "get_platform_status", "create_agent", "update_agent", "delete_agent"],
                  "description": "The management operation to perform."
                },
                "agentId": {
                  "type": "string",
                  "description": "Agent ID (required for create_agent, update_agent, delete_agent)."
                },
                "displayName": {
                  "type": "string",
                  "description": "Human-readable agent display name (for create_agent / update_agent)."
                },
                "modelId": {
                  "type": "string",
                  "description": "Model identifier (for create_agent / update_agent)."
                },
                "apiProvider": {
                  "type": "string",
                  "description": "API provider name, e.g. 'github-copilot', 'openai', 'anthropic' (for create_agent / update_agent)."
                },
                "description": {
                  "type": "string",
                  "description": "Agent description (for create_agent / update_agent)."
                },
                "emoji": {
                  "type": "string",
                  "description": "Emoji avatar for the agent (for create_agent / update_agent)."
                },
                "systemPrompt": {
                  "type": "string",
                  "description": "System prompt for the agent (for create_agent / update_agent)."
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
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var action = ReadString(arguments, "action");
        if (string.IsNullOrWhiteSpace(action))
            return Fail("Missing required parameter: action");

        return action.ToLowerInvariant() switch
        {
            "get_config" => GetConfig(),
            "list_agents" => ListAgents(),
            "get_platform_status" => GetPlatformStatus(),
            "create_agent" => await CreateAgentAsync(arguments, cancellationToken),
            "update_agent" => await UpdateAgentAsync(arguments, cancellationToken),
            "delete_agent" => await DeleteAgentAsync(arguments, cancellationToken),
            _ => Fail($"Unknown action '{action}'. Valid actions: get_config, list_agents, get_platform_status, create_agent, update_agent, delete_agent")
        };
    }

    // ─── Read-only operations ────────────────────────────────────────────────

    private AgentToolResult GetConfig()
    {
        var config = platformConfig.CurrentValue;
        var summary = new
        {
            version = config.Version,
            gateway = new
            {
                listenUrl = config.Gateway?.ListenUrl,
                defaultAgentId = config.Gateway?.DefaultAgentId,
                defaultTimezone = config.Gateway?.DefaultTimezone
            },
            agentCount = config.Agents?.Count ?? 0,
            providerCount = config.Providers?.Count ?? 0,
            channelCount = config.Channels?.Count ?? 0
        };
        return Ok(JsonSerializer.Serialize(summary, JsonOptions));
    }

    private AgentToolResult ListAgents()
    {
        var runningInstances = agentSupervisor.GetAllInstances()
            .Select(i => i.AgentId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var agents = agentRegistry.GetAll()
            .Select(d => new AgentSummary(
                AgentId: d.AgentId.ToString(),
                DisplayName: d.DisplayName,
                Description: d.Description,
                Emoji: d.Emoji,
                ModelId: d.ModelId,
                ApiProvider: d.ApiProvider,
                IsRunning: runningInstances.Contains(d.AgentId.ToString())))
            .OrderBy(a => a.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(JsonSerializer.Serialize(agents, JsonOptions));
    }

    private AgentToolResult GetPlatformStatus()
    {
        var allAgents = agentRegistry.GetAll();
        var runningCount = agentSupervisor.GetAllInstances().Count;

        var status = new
        {
            totalAgents = allAgents.Count,
            runningAgents = runningCount,
            idleAgents = allAgents.Count - runningCount,
            serverTime = DateTimeOffset.UtcNow.ToString("O")
        };
        return Ok(JsonSerializer.Serialize(status, JsonOptions));
    }

    // ─── Mutating operations ─────────────────────────────────────────────────

    private async Task<AgentToolResult> CreateAgentAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var agentId = ReadString(arguments, "agentId");
        var displayName = ReadString(arguments, "displayName");
        var modelId = ReadString(arguments, "modelId");
        var apiProvider = ReadString(arguments, "apiProvider");

        if (string.IsNullOrWhiteSpace(agentId))
            return Fail("create_agent requires agentId");
        if (string.IsNullOrWhiteSpace(displayName))
            return Fail("create_agent requires displayName");
        if (string.IsNullOrWhiteSpace(modelId))
            return Fail("create_agent requires modelId");
        if (string.IsNullOrWhiteSpace(apiProvider))
            return Fail("create_agent requires apiProvider");

        var id = AgentId.From(agentId);
        if (agentRegistry.Contains(id))
            return Fail($"Agent '{agentId}' already exists. Use update_agent to modify it.");

        var descriptor = new AgentDescriptor
        {
            AgentId = id,
            DisplayName = displayName,
            ModelId = modelId,
            ApiProvider = apiProvider,
            Description = ReadString(arguments, "description"),
            Emoji = ReadString(arguments, "emoji"),
            SystemPrompt = ReadString(arguments, "systemPrompt")
        };

        try
        {
            agentRegistry.Register(descriptor);
            await configWriter.SaveAsync(descriptor, cancellationToken);
            return Ok($"Agent '{agentId}' created successfully.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to create agent '{agentId}': {ex.Message}");
        }
    }

    private async Task<AgentToolResult> UpdateAgentAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var agentId = ReadString(arguments, "agentId");
        if (string.IsNullOrWhiteSpace(agentId))
            return Fail("update_agent requires agentId");

        var id = AgentId.From(agentId);
        var existing = agentRegistry.Get(id);
        if (existing is null)
            return Fail($"Agent '{agentId}' not found. Use create_agent to register a new agent.");

        var updated = existing with
        {
            DisplayName = ReadString(arguments, "displayName") ?? existing.DisplayName,
            ModelId = ReadString(arguments, "modelId") ?? existing.ModelId,
            ApiProvider = ReadString(arguments, "apiProvider") ?? existing.ApiProvider,
            Description = ReadString(arguments, "description") ?? existing.Description,
            Emoji = ReadString(arguments, "emoji") ?? existing.Emoji,
            SystemPrompt = ReadString(arguments, "systemPrompt") ?? existing.SystemPrompt
        };

        try
        {
            var wasUpdated = agentRegistry.Update(id, updated);
            if (!wasUpdated)
                return Fail($"Failed to update agent '{agentId}': registry update returned false.");
            await configWriter.SaveAsync(updated, cancellationToken);
            return Ok($"Agent '{agentId}' updated successfully.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to update agent '{agentId}': {ex.Message}");
        }
    }

    private async Task<AgentToolResult> DeleteAgentAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var agentId = ReadString(arguments, "agentId");
        if (string.IsNullOrWhiteSpace(agentId))
            return Fail("delete_agent requires agentId");

        var id = AgentId.From(agentId);
        if (!agentRegistry.Contains(id))
            return Fail($"Agent '{agentId}' not found.");

        try
        {
            agentRegistry.Unregister(id);
            await configWriter.DeleteAsync(agentId, cancellationToken);
            return Ok($"Agent '{agentId}' deleted successfully.");
        }
        catch (Exception ex)
        {
            return Fail($"Failed to delete agent '{agentId}': {ex.Message}");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static AgentToolResult Ok(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static AgentToolResult Fail(string message) =>
        new([new AgentToolContent(AgentToolContentType.Text, $"Error: {message}")]);

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value))
            return null;
        if (value is JsonElement elem && elem.ValueKind == JsonValueKind.String)
            return elem.GetString();
        if (value is string s)
            return s;
        return null;
    }

    // ─── Internal DTOs ────────────────────────────────────────────────────────

    private sealed record AgentSummary(
        string AgentId,
        string DisplayName,
        string? Description,
        string? Emoji,
        string ModelId,
        string ApiProvider,
        bool IsRunning);
}
