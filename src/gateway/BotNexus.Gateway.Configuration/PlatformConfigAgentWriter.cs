using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Configuration;

public sealed class PlatformConfigAgentWriter : IAgentConfigurationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
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

            // Required identity + routing surface.
            entry["provider"] = descriptor.ApiProvider;
            entry["model"] = descriptor.ModelId;
            entry["displayName"] = descriptor.DisplayName;
            entry["enabled"] = true;

            // Simple scalar surface.
            SetOptionalString(entry, "emoji", descriptor.Emoji);
            SetOptionalString(entry, "description", descriptor.Description);
            SetOptionalString(entry, "systemPromptFile", descriptor.SystemPromptFile);
            SetOptionalString(entry, "isolationStrategy", descriptor.IsolationStrategy);
            SetOptionalString(entry, "cacheRetention", descriptor.CacheRetentionMode);
            SetOptionalString(entry, "thinking", descriptor.Thinking);
            SetOptionalContextWindow(entry, "contextWindow", descriptor.ContextWindow);
            SetOptionalInt(entry, "maxConcurrentSessions", descriptor.MaxConcurrentSessions);

            // List surface.
            SetOptionalList(entry, "systemPromptFiles", descriptor.SystemPromptFiles);
            SetOptionalList(entry, "allowedModels", descriptor.AllowedModelIds);
            SetOptionalList(entry, "subAgents", descriptor.SubAgentIds);
            SetOptionalList(entry, "subAgentRoles", descriptor.SubAgentRoles);
            SetOptionalList(entry, "toolIds", descriptor.ToolIds);
            SetOptionalStringArray(entry, "shellCommand", descriptor.ShellCommand);

            // Structured object surface.
            SetOptionalObject(entry, "metadata", descriptor.Metadata);
            SetOptionalObject(entry, "isolationOptions", descriptor.IsolationOptions);
            SetOptionalNode(entry, "memory", descriptor.Memory);
            SetOptionalNode(entry, "soul", descriptor.Soul);
            SetOptionalNode(entry, "heartbeat", descriptor.Heartbeat);
            SetOptionalNode(entry, "dateTimeInjection", descriptor.DateTimeInjection);
            SetFileAccess(entry, descriptor.FileAccess);
            SetSessionAccess(entry, descriptor.SessionAccessLevel, descriptor.SessionAllowedAgents);
            SetConversationAccess(entry, descriptor.ConversationAccessLevel, descriptor.ConversationAllowedAgents);
            SetExtensions(entry, descriptor.ExtensionConfig);
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

    private static void SetOptionalStringArray(JsonObject target, string propertyName, string[]? values)
    {
        if (values is not { Length: > 0 })
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

    // Nullable int variant: absent (null) removes the key; any set value (including a large
    // context window) is written verbatim. Distinct from SetOptionalInt, whose <=0 sentinel
    // does not apply to a selectable context-window size.
    private static void SetOptionalContextWindow(JsonObject target, string propertyName, int? value)
    {
        if (value is null)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = value.Value;
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

    // File access is persisted only when at least one path list is non-empty so an agent with no
    // policy leaves the section absent (workspace-only), matching how PlatformConfigAgentSource
    // treats a null FileAccess policy.
    private static void SetFileAccess(JsonObject target, FileAccessPolicy? policy)
    {
        if (policy is null
            || (policy.AllowedReadPaths.Count == 0
                && policy.AllowedWritePaths.Count == 0
                && policy.DeniedPaths.Count == 0))
        {
            target.Remove("fileAccess");
            return;
        }

        var fileAccess = new JsonObject();
        AddPathList(fileAccess, "allowedReadPaths", policy.AllowedReadPaths);
        AddPathList(fileAccess, "allowedWritePaths", policy.AllowedWritePaths);
        AddPathList(fileAccess, "deniedPaths", policy.DeniedPaths);
        target["fileAccess"] = fileAccess;

        static void AddPathList(JsonObject parent, string name, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return;
            parent[name] = JsonSerializer.SerializeToNode(values, JsonOptions);
        }
    }

    // Session/conversation access default to "own" with no allowlist; only persist when the
    // effective policy diverges from that default so an unedited agent leaves the section absent
    // and unrelated fields are untouched.
    private static void SetSessionAccess(JsonObject target, string level, IReadOnlyList<string> allowedAgents)
        => SetAccess(target, "sessionAccess", level, allowedAgents);

    private static void SetConversationAccess(JsonObject target, string level, IReadOnlyList<string> allowedAgents)
        => SetAccess(target, "conversationAccess", level, allowedAgents);

    private static void SetAccess(JsonObject target, string propertyName, string level, IReadOnlyList<string> allowedAgents)
    {
        var isDefault = (string.IsNullOrWhiteSpace(level) || string.Equals(level, "own", StringComparison.OrdinalIgnoreCase))
            && allowedAgents.Count == 0;
        if (isDefault)
        {
            target.Remove(propertyName);
            return;
        }

        var access = new JsonObject
        {
            ["level"] = string.IsNullOrWhiteSpace(level) ? "own" : level
        };
        if (allowedAgents.Count > 0)
            access["allowedAgents"] = JsonSerializer.SerializeToNode(allowedAgents, JsonOptions);
        target[propertyName] = access;
    }

    private static void SetExtensions(JsonObject target, IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (extensions.Count == 0)
        {
            target.Remove("extensions");
            return;
        }

        var extensionsObject = new JsonObject();
        foreach (var (key, value) in extensions)
            extensionsObject[key] = JsonNode.Parse(value.GetRawText());
        target["extensions"] = extensionsObject;
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
