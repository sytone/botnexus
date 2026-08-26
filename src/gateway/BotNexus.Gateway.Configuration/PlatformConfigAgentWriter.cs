using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
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
    private readonly ILocationResolver? _locationResolver;

    public PlatformConfigAgentWriter(
        PlatformConfigWriter configWriter,
        BotNexusHome botNexusHome,
        ILocationResolver? locationResolver = null)
    {
        ArgumentNullException.ThrowIfNull(configWriter);
        ArgumentNullException.ThrowIfNull(botNexusHome);

        _configWriter = configWriter;
        _botNexusHome = botNexusHome;
        _locationResolver = locationResolver;
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
            SetOptionalCount(entry, "maxConcurrentSessions", descriptor.MaxConcurrentSessions);

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
            SetFileAccess(entry, descriptor.FileAccess, _locationResolver);
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

    // #3547: a count of 0 is a LEGAL STORED VALUE ("unlimited"), not an absent one, and the
    // descriptor carries no unset/zero distinction to tell them apart. Treating the sentinel as
    // "remove" therefore deleted `maxConcurrentSessions: 0` whenever an unrelated field was edited.
    // A non-positive value now leaves an existing key exactly as stored and only declines to create
    // one that was never there, so the sentinel can no longer destroy a deliberate setting.
    private static void SetOptionalCount(JsonObject target, string propertyName, int value)
    {
        if (value <= 0)
            return;

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
    //
    // #3547: PlatformConfigAgentSource RESOLVES '@location' references to absolute paths on read,
    // so a descriptor that merely round-tripped carries resolved paths and writing them back
    // silently replaced portable aliases with machine-specific absolute paths. PreservePathAliases
    // restores the stored spelling for any entry whose resolved form is unchanged, so a genuine
    // edit still writes through while an untouched list keeps its aliases.
    private static void SetFileAccess(JsonObject target, FileAccessPolicy? policy, ILocationResolver? locationResolver)
    {
        if (policy is null
            || (policy.AllowedReadPaths.Count == 0
                && policy.AllowedWritePaths.Count == 0
                && policy.DeniedPaths.Count == 0))
        {
            // #3547: an absent policy on the descriptor is not an instruction to delete a stored
            // one. Only remove when there is nothing stored to preserve.
            return;
        }

        var stored = target["fileAccess"] as JsonObject;
        var fileAccess = new JsonObject();
        AddPathList(fileAccess, "allowedReadPaths", policy.AllowedReadPaths, stored, locationResolver);
        AddPathList(fileAccess, "allowedWritePaths", policy.AllowedWritePaths, stored, locationResolver);
        AddPathList(fileAccess, "deniedPaths", policy.DeniedPaths, stored, locationResolver);
        target["fileAccess"] = fileAccess;

        static void AddPathList(
            JsonObject parent,
            string name,
            IReadOnlyList<string> values,
            JsonObject? stored,
            ILocationResolver? locationResolver)
        {
            if (values.Count == 0)
                return;
            var storedList = stored?[name] as JsonArray;
            parent[name] = JsonSerializer.SerializeToNode(
                PreservePathAliases(values, storedList, locationResolver),
                JsonOptions);
        }
    }

    /// <summary>
    /// Restores the stored '@location' spelling of file-access paths that the config source
    /// resolved to absolute paths on read (#3547).
    /// </summary>
    /// <remarks>
    /// An alias is restored only when it still RESOLVES to the incoming absolute path, which is
    /// precisely the "read and written back unchanged" case. A caller that genuinely changed the
    /// path writes a value the alias no longer resolves to, so the edit is honoured rather than
    /// silently re-aliased. Matching is by resolved value rather than by position, so adds,
    /// removals and reorders are all handled without a length precondition. With no resolver
    /// available the comparison cannot be made, so the caller's values are written through
    /// unchanged - failing toward the caller's intent rather than toward a guessed alias.
    /// </remarks>
    private static List<string> PreservePathAliases(
        IReadOnlyList<string> values,
        JsonArray? stored,
        ILocationResolver? locationResolver)
    {
        var result = new List<string>(values.Count);
        if (stored is null || locationResolver is null)
        {
            result.AddRange(values);
            return result;
        }

        // Map each stored alias to the absolute path it resolves to, so an incoming resolved path
        // can be traced back to the alias that produced it.
        Dictionary<string, string> aliasByResolved = new(StringComparer.OrdinalIgnoreCase);
        foreach (var node in stored)
        {
            var storedValue = node?.GetValue<string>();
            if (storedValue is null || !storedValue.StartsWith('@'))
                continue;

            var resolved = LocationReferenceResolver.Resolve(storedValue, locationResolver);
            if (resolved is not null)
                aliasByResolved.TryAdd(resolved, storedValue);
        }

        foreach (var value in values)
        {
            if (!value.StartsWith('@') && aliasByResolved.TryGetValue(value, out var alias))
            {
                result.Add(alias);
                continue;
            }

            result.Add(value);
        }

        return result;
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

    // #3547: the extensions bag is an OPEN MAP the descriptor only partially models - the config
    // source strips null-valued entries on the way in, and a caller that never read an extension
    // cannot echo it back. Replacing the whole object therefore deleted every unmodelled extension
    // (10 keys lost from a live agent on a single `thinking` edit). Merge per key instead: the
    // descriptor updates the extensions it carries and is silent about the rest, so an entry it
    // never mentioned survives. Clearing one extension is an explicit config-path operation, not a
    // side effect of omitting it here.
    private static void SetExtensions(JsonObject target, IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (extensions.Count == 0)
            return;

        if (target["extensions"] is not JsonObject extensionsObject)
        {
            extensionsObject = new JsonObject();
            target["extensions"] = extensionsObject;
        }

        foreach (var (key, value) in extensions)
            extensionsObject[key] = JsonNode.Parse(value.GetRawText());
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
