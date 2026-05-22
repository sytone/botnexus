using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

public sealed class PlatformConfigAgentWriter : IAgentConfigurationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BotNexusHome _botNexusHome;
    private readonly PlatformConfigWriter _configWriter;

    public PlatformConfigAgentWriter(PlatformConfigWriter configWriter, BotNexusHome botNexusHome)
    {
        ArgumentNullException.ThrowIfNull(configWriter);
        ArgumentNullException.ThrowIfNull(botNexusHome);

        _configWriter = configWriter;
        _botNexusHome = botNexusHome;
    }

    public async Task SaveAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.AgentId.Value);

        _ = _botNexusHome.GetAgentDirectory(descriptor.AgentId.Value);

        await _configWriter.MutateAsync(root =>
        {
            var agents = EnsureAgentsObject(root);
            var entry = GetOrCreateAgentEntry(agents, descriptor.AgentId.Value);

            entry["provider"] = descriptor.ApiProvider;
            entry["model"] = descriptor.ModelId;
            entry["displayName"] = descriptor.DisplayName;
            entry["enabled"] = true;
            SetOptionalString(entry, "description", descriptor.Description);
            SetOptionalString(entry, "systemPromptFile", descriptor.SystemPromptFile);
            SetOptionalList(entry, "allowedModels", descriptor.AllowedModelIds);
            SetOptionalList(entry, "subAgents", descriptor.SubAgentIds);
            SetOptionalList(entry, "toolIds", descriptor.ToolIds);
            SetOptionalString(entry, "isolationStrategy", descriptor.IsolationStrategy);
            SetOptionalInt(entry, "maxConcurrentSessions", descriptor.MaxConcurrentSessions);
            SetOptionalObject(entry, "metadata", descriptor.Metadata);
            SetOptionalObject(entry, "isolationOptions", descriptor.IsolationOptions);
            SetOptionalNode(entry, "soul", descriptor.Soul);
        }, $"before-agent-upsert-{descriptor.AgentId}", cancellationToken);
    }

    public async Task DeleteAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await _configWriter.MutateAsync(root =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetAgentsObject(root, out var agents))
                return;

            if (!agents.Remove(agentId))
                return;
        }, $"before-agent-delete-{agentId}", cancellationToken);
    }

    private static void SetOptionalString(JsonObject target, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = value;
    }

    private static void SetOptionalList(JsonObject target, string propertyName, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(values, JsonOptions);
    }

    private static void SetOptionalInt(JsonObject target, string propertyName, int value)
    {
        if (value <= 0)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = value;
    }

    private static void SetOptionalObject(JsonObject target, string propertyName, IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(values, JsonOptions);
    }

    private static void SetOptionalNode<T>(JsonObject target, string propertyName, T? value) where T : class
    {
        if (value is null)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(value, JsonOptions);
    }

    private static JsonObject EnsureAgentsObject(JsonObject root)
    {
        if (root["agents"] is JsonObject agents)
            return agents;

        var created = new JsonObject();
        root["agents"] = created;
        return created;
    }

    private static bool TryGetAgentsObject(JsonObject root, out JsonObject agents)
    {
        if (root["agents"] is JsonObject existing)
        {
            agents = existing;
            return true;
        }

        agents = null!;
        return false;
    }

    private static JsonObject GetOrCreateAgentEntry(JsonObject agents, string agentId)
    {
        if (agents[agentId] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        agents[agentId] = created;
        return created;
    }
}
