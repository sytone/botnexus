using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>Presence-aware edits. Omission does nothing; null is stored; inherit removes the authored key.
/// Runtime fallback is field-specific; agents.defaults is not merged (#3515).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public abstract class ConfigurationPatch
{
    internal Dictionary<string, object?> Changes { get; } = new(StringComparer.Ordinal);
    /// <summary>Reads a supplied field without conflating omission with an explicit null assignment.</summary>
    protected T? Get<T>(string name) => Changes.TryGetValue(name, out var value) ? (T?)value : default;
}

/// <summary>Explicit, typed editable configuration fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class AgentConfigurationPatch : ConfigurationPatch
{
    /// <summary>Admission state for new work; omission preserves state and removal restores the enabled code default. Null is not a valid state.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Human-readable name; null uses the agent ID at runtime. Omission leaves the authored value unchanged.</summary>
    public string? DisplayName { get => Get<string?>("displayName"); set => Changes["displayName"] = value; }
    /// <summary>Optional description of the agent purpose. Omission leaves the authored value unchanged.</summary>
    public string? Description { get => Get<string?>("description"); set => Changes["description"] = value; }
    /// <summary>Optional emoji displayed alongside the agent name. Omission leaves the authored value unchanged.</summary>
    public string? Emoji { get => Get<string?>("emoji"); set => Changes["emoji"] = value; }
    /// <summary>Provider instance for the selected model; the resulting agent must retain a nonblank provider. Omission leaves the authored value unchanged.</summary>
    public string? Provider { get => Get<string?>("provider"); set => Changes["provider"] = value; }
    /// <summary>Model identifier; the resulting agent must retain a nonblank model. Omission leaves the authored value unchanged.</summary>
    public string? Model { get => Get<string?>("model"); set => Changes["model"] = value; }
    /// <summary>Model allowlist; null or an empty list leaves selection unrestricted within provider policy. Omission leaves the authored value unchanged.</summary>
    public List<string>? AllowedModels { get => Get<List<string>?>("allowedModels"); set => Changes["allowedModels"] = value; }
    /// <summary>Granted tool identifiers; an empty list clears the explicit grants. Omission leaves the authored value unchanged.</summary>
    public List<string>? ToolIds { get => Get<List<string>?>("toolIds"); set => Changes["toolIds"] = value; }
    /// <summary>Positive per-tool timeout in seconds; null uses the runtime code default, not agents.defaults. Omission leaves the authored value unchanged.</summary>
    public int? ToolTimeoutSeconds { get => Get<int?>("toolTimeoutSeconds"); set => Changes["toolTimeoutSeconds"] = value; }
    /// <summary>Agent IDs available for sub-agent calls; an empty list clears explicit grants. Omission leaves the authored value unchanged.</summary>
    public List<string>? SubAgents { get => Get<List<string>?>("subAgents"); set => Changes["subAgents"] = value; }
    /// <summary>Runtime isolation strategy name; null uses in-process. Omission leaves the authored value unchanged.</summary>
    public string? IsolationStrategy { get => Get<string?>("isolationStrategy"); set => Changes["isolationStrategy"] = value; }
    /// <summary>Nonnegative session concurrency limit; zero or null uses the runtime unlimited setting. Omission leaves the authored value unchanged.</summary>
    public int? MaxConcurrentSessions { get => Get<int?>("maxConcurrentSessions"); set => Changes["maxConcurrentSessions"] = value; }
    /// <summary>Reasoning effort validated against the selected model capabilities; null uses the model default. Omission leaves the authored value unchanged.</summary>
    public string? Thinking { get => Get<string?>("thinking"); set => Changes["thinking"] = value; }
    /// <summary>Positive context-window size validated against selected model capabilities; null uses the model default. Omission leaves the authored value unchanged.</summary>
    public int? ContextWindow { get => Get<int?>("contextWindow"); set => Changes["contextWindow"] = value; }
    /// <summary>Partial memory settings; null stores a null block and uses runtime fallback. Omission leaves the authored value unchanged.</summary>
    public MemoryPatch? Memory { get => Get<MemoryPatch?>("memory"); set => Changes["memory"] = value; }
    /// <summary>Partial session lifecycle settings; null stores a null block and uses runtime fallback. Omission leaves the authored value unchanged.</summary>
    public SoulPatch? Soul { get => Get<SoulPatch?>("soul"); set => Changes["soul"] = value; }
    /// <summary>Partial heartbeat settings; null stores a null block and uses runtime fallback. Omission leaves the authored value unchanged.</summary>
    public HeartbeatPatch? Heartbeat { get => Get<HeartbeatPatch?>("heartbeat"); set => Changes["heartbeat"] = value; }
    /// <summary>Partial agent file-access policy; a null block uses the whole gateway policy at runtime. Omission leaves the authored value unchanged.</summary>
    public FileAccessPatch? FileAccess { get => Get<FileAccessPatch?>("fileAccess"); set => Changes["fileAccess"] = value; }
    /// <summary>Partial session-tool access settings; null uses own-agent runtime access. Omission leaves the authored value unchanged.</summary>
    public AccessPatch? SessionAccess { get => Get<AccessPatch?>("sessionAccess"); set => Changes["sessionAccess"] = value; }
    /// <summary>Partial conversation-tool access settings; null uses session access at runtime. Omission leaves the authored value unchanged.</summary>
    public AccessPatch? ConversationAccess { get => Get<AccessPatch?>("conversationAccess"); set => Changes["conversationAccess"] = value; }
    /// <summary>Partial approval and availability rules; omitted sibling rules are preserved. Omission leaves the authored value unchanged.</summary>
    public ToolPolicyPatch? ToolPolicy { get => Get<ToolPolicyPatch?>("toolPolicy"); set => Changes["toolPolicy"] = value; }
    /// <summary>Typed dot paths whose authored keys are removed at any supported nesting depth. Despite the wire name, removal does not merge agents.defaults (#3515); runtime fallback is field-specific. Paths cannot overlap edits. Omission leaves the authored value unchanged.</summary>
    public List<string>? Inherit { get; set; }
}

/// <summary>Explicit, typed editable configuration fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class AccessPatch : ConfigurationPatch
{
    /// <summary>Access scope: own, allowlist, or all; null uses the runtime access fallback. Omission leaves the authored value unchanged.</summary>
    public string? Level { get => Get<string?>("level"); set => Changes["level"] = value; }
    /// <summary>Agents accessible in allowlist scope; an empty list clears the authored allowlist. Omission leaves the authored value unchanged.</summary>
    public List<string>? AllowedAgents { get => Get<List<string>?>("allowedAgents"); set => Changes["allowedAgents"] = value; }
}

/// <summary>Explicit, typed editable configuration fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class ToolPolicyPatch : ConfigurationPatch
{
    /// <summary>Tools requiring approval regardless of the ordinary tool default. Omission leaves the authored value unchanged.</summary>
    public List<string>? AlwaysApprove { get => Get<List<string>?>("alwaysApprove"); set => Changes["alwaysApprove"] = value; }
    /// <summary>Tools exempted from ordinary approval requirements. Omission leaves the authored value unchanged.</summary>
    public List<string>? NeverApprove { get => Get<List<string>?>("neverApprove"); set => Changes["neverApprove"] = value; }
    /// <summary>Tools unavailable to the agent. Omission leaves the authored value unchanged.</summary>
    public List<string>? Denied { get => Get<List<string>?>("denied"); set => Changes["denied"] = value; }
    /// <summary>Fallback when approval cannot be requested: allow or deny. Omission leaves the authored value unchanged.</summary>
    public string? AskFallback { get => Get<string?>("askFallback"); set => Changes["askFallback"] = value; }
    /// <summary>Tools exempted from the deny fallback when approval cannot be requested. Omission leaves the authored value unchanged.</summary>
    public List<string>? AskFallbackAllow { get => Get<List<string>?>("askFallbackAllow"); set => Changes["askFallbackAllow"] = value; }
}

/// <summary>Explicit, typed editable configuration fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class FileAccessPatch : ConfigurationPatch
{
    /// <summary>Paths explicitly permitted for file reads. Omission leaves the authored value unchanged.</summary>
    public List<string>? AllowedReadPaths { get => Get<List<string>?>("allowedReadPaths"); set => Changes["allowedReadPaths"] = value; }
    /// <summary>Paths explicitly permitted for file writes. Omission leaves the authored value unchanged.</summary>
    public List<string>? AllowedWritePaths { get => Get<List<string>?>("allowedWritePaths"); set => Changes["allowedWritePaths"] = value; }
    /// <summary>Paths denied for file access. Omission leaves the authored value unchanged.</summary>
    public List<string>? DeniedPaths { get => Get<List<string>?>("deniedPaths"); set => Changes["deniedPaths"] = value; }
}

/// <summary>Applies one operator batch through the canonical, locked config pipeline.</summary>
public static class AgentConfigurationEditor
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedPaths = BuildPaths();

    private static HashSet<string> BuildPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        AddPaths(typeof(AgentConfigurationPatch), "");
        void AddPaths(Type type, string prefix)
        {
            foreach (var property in type.GetProperties().Where(p => p.Name != nameof(AgentConfigurationPatch.Inherit)))
            {
                var name = prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                paths.Add(name);
                if (typeof(ConfigurationPatch).IsAssignableFrom(property.PropertyType))
                    AddPaths(property.PropertyType, name + ".");
            }
        }
        return paths;
    }

    /// <summary>Validates the resulting runtime candidate under the canonical writer lock before persisting explicit edits.</summary>
    public static Task<IReadOnlyList<string>> ApplyAsync(PlatformConfigWriter writer, AgentId agentId, AgentConfigurationPatch patch, CancellationToken ct, ModelRegistry? modelRegistry = null)
        => writer.MutateValidatedAsync(root =>
        {
            if (root["agents"] is not JsonObject agents) return "Agent not found.";
            var storedKey = agents.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, agentId.Value, StringComparison.OrdinalIgnoreCase));
            if (storedKey is null || agents[storedKey] is not JsonObject agent) return "Agent not found.";
            var error = Validate(patch);
            if (error is not null) return error;
            var writes = Flatten(patch).ToList();
            foreach (var path in patch.Inherit ?? [])
            {
                if (!AllowedPaths.Contains(path)) return $"Unsupported inherit path: {path}.";
                if (writes.Any(w => w == path || w.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(w + ".", StringComparison.Ordinal)))
                    return $"Cannot both edit and inherit {path}.";
            }
            Apply(agent, patch);
            foreach (var path in patch.Inherit ?? [])
            {
                var parts = path.Split('.');
                JsonObject? parent = agent;
                foreach (var part in parts[..^1]) parent = parent?[part] as JsonObject;
                parent?.Remove(parts[^1]);
            }
            try
            {
                var candidate = root.Deserialize<PlatformConfig>(Options) ?? new PlatformConfig();
                var errors = PlatformConfigAgentSource.ValidateCandidate(candidate, AgentId.From(storedKey), modelRegistry);
                if (errors.Count > 0) return string.Join("; ", errors);
                // Validate model edits against the resulting authored candidate while still under
                // the writer lock. The descriptor validator checks capabilities, not selection policy.
                if (writes.Concat(patch.Inherit ?? []).Any(name => name is "model" or "provider" or "allowedModels"))
                {
                    var selected = candidate.Agents?[storedKey];
                    if (selected is null) return "Agent not found.";
                    if (selected.AllowedModels is { Count: > 0 } allowed
                        && !allowed.Contains(selected.Model ?? "", StringComparer.OrdinalIgnoreCase))
                        return $"Model '{selected.Model}' is not allowed for this agent.";
                    if (candidate.Providers?.TryGetValue(selected.Provider ?? "", out var provider) == true)
                    {
                        if (!provider.Enabled)
                            return $"Provider '{selected.Provider}' is disabled.";
                        if (provider.ResolveChatModels() is { } providerAllowed
                            && !providerAllowed.Contains(selected.Model ?? "", StringComparer.OrdinalIgnoreCase))
                            return $"Model '{selected.Model}' is not allowed for provider '{selected.Provider}'.";
                    }
                    if (modelRegistry is null)
                        return "Model registry is unavailable. Model, provider and allowedModels edits cannot be validated; retry when the registry is available.";
                    if (modelRegistry.GetModel(selected.Provider ?? "", selected.Model ?? "") is null)
                        return $"Model '{selected.Model}' is not registered for provider '{selected.Provider}'.";
                }
                return null;
            }
            catch (JsonException)
            {
                return "The resulting agent configuration has invalid field types or nulls.";
            }
        }, "agent-operator-edit", ct, namedSections: ["agents"]);

    /// <summary>
    /// Redacts through ConfigController's whole-document seam, then returns only supported editor
    /// fields, including enabled admission state. Opaque metadata, extensions and future nested keys are
    /// excluded because their secret schemas are not known to this editor.
    /// </summary>
    public static JsonObject ReadEditableConfiguration(this JsonObject root, AgentId agentId)
    {
        root.Redact();
        var source = root["agents"]?[agentId.Value] as JsonObject ?? new JsonObject();
        var result = Project(source, typeof(AgentConfigurationPatch));
        return result;
    }

    private static JsonObject Project(JsonObject source, Type type)
    {
        var result = new JsonObject();
        foreach (var property in type.GetProperties().Where(p => p.Name != nameof(AgentConfigurationPatch.Inherit)))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!source.ContainsKey(name)) continue;
            if (typeof(ConfigurationPatch).IsAssignableFrom(property.PropertyType))
            {
                // A malformed opaque value is not a safe editor read surface either.
                if (source[name] is JsonObject child) result[name] = Project(child, property.PropertyType);
                else if (source[name] is null) result[name] = null;
            }
            else result[name] = source[name]?.DeepClone();
        }
        return result;
    }

    private static IEnumerable<string> Flatten(ConfigurationPatch patch, string prefix = "")
    {
        foreach (var (name, value) in patch.Changes)
            if (value is ConfigurationPatch nested)
                foreach (var path in Flatten(nested, prefix + name + ".")) yield return path;
            else yield return prefix + name;
    }

    private static string? Validate(ConfigurationPatch patch)
    {
        foreach (var (name, value) in patch.Changes)
        {
            if (value is ConfigurationPatch nested && Validate(nested) is { } error) return error;
            if (value is List<string> list && list.Any(string.IsNullOrWhiteSpace)) return $"{name} cannot contain blank entries.";
            if (name is "toolTimeoutSeconds" or "contextWindow" && value is int positive && positive <= 0) return $"{name} must be positive or null.";
            if (value is int number && number < 0) return $"{name} cannot be negative.";
            if (name == "level" && value is not null && value is not ("own" or "allowlist" or "all")) return "Access level must be own, allowlist, or all.";
            if (name == "askFallback" && value is not null && value is not ("allow" or "deny")) return "Ask fallback must be allow or deny.";
        }
        return null;
    }

    private static void Apply(JsonObject target, ConfigurationPatch patch)
    {
        foreach (var (name, value) in patch.Changes)
        {
            if (value is ConfigurationPatch nested)
            {
                if (nested.Changes.Count == 0) continue;
                if (target[name] is not JsonObject child) target[name] = child = new JsonObject();
                Apply(child, nested);
            }
            else target[name] = JsonSerializer.SerializeToNode(value, Options);
        }
    }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class MemoryPatch : ConfigurationPatch
{
    /// <summary>Whether this nested feature is enabled. Omission leaves the authored value unchanged.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Memory storage path. Omission leaves the authored value unchanged.</summary>
    public string? Path { get => Get<string?>("path"); set => Changes["path"] = value; }
    /// <summary>Memory indexing mode. Omission leaves the authored value unchanged.</summary>
    public string? Indexing { get => Get<string?>("indexing"); set => Changes["indexing"] = value; }
    /// <summary>Memory prompt injection mode: full, summary, or none. Omission leaves the authored value unchanged.</summary>
    public string? PromptInjection { get => Get<string?>("promptInjection"); set => Changes["promptInjection"] = value; }
    /// <summary>Partial memory search settings. Omission leaves the authored value unchanged.</summary>
    public MemorySearchPatch? Search { get => Get<MemorySearchPatch?>("search"); set => Changes["search"] = value; }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class MemorySearchPatch : ConfigurationPatch
{
    /// <summary>Default number of memory search results. Omission leaves the authored value unchanged.</summary>
    public int? DefaultTopK { get => Get<int?>("defaultTopK"); set => Changes["defaultTopK"] = value; }
    /// <summary>Partial time-based search decay settings. Omission leaves the authored value unchanged.</summary>
    public TemporalDecayPatch? TemporalDecay { get => Get<TemporalDecayPatch?>("temporalDecay"); set => Changes["temporalDecay"] = value; }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class TemporalDecayPatch : ConfigurationPatch
{
    /// <summary>Whether this nested feature is enabled. Omission leaves the authored value unchanged.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Search result decay half-life in days. Omission leaves the authored value unchanged.</summary>
    public int? HalfLifeDays { get => Get<int?>("halfLifeDays"); set => Changes["halfLifeDays"] = value; }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class SoulPatch : ConfigurationPatch
{
    /// <summary>Whether this nested feature is enabled. Omission leaves the authored value unchanged.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Time zone used by this feature. Omission leaves the authored value unchanged.</summary>
    public string? Timezone { get => Get<string?>("timezone"); set => Changes["timezone"] = value; }
    /// <summary>Local daily session boundary time. Omission leaves the authored value unchanged.</summary>
    public string? DayBoundary { get => Get<string?>("dayBoundary"); set => Changes["dayBoundary"] = value; }
    /// <summary>Whether sealing a session triggers reflection. Omission leaves the authored value unchanged.</summary>
    public bool? ReflectionOnSeal { get => Get<bool?>("reflectionOnSeal"); set => Changes["reflectionOnSeal"] = value; }
    /// <summary>Optional prompt used for session reflection. Omission leaves the authored value unchanged.</summary>
    public string? ReflectionPrompt { get => Get<string?>("reflectionPrompt"); set => Changes["reflectionPrompt"] = value; }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class HeartbeatPatch : ConfigurationPatch
{
    /// <summary>Whether this nested feature is enabled. Omission leaves the authored value unchanged.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Heartbeat scheduling interval in minutes. Omission leaves the authored value unchanged.</summary>
    public int? IntervalMinutes { get => Get<int?>("intervalMinutes"); set => Changes["intervalMinutes"] = value; }
    /// <summary>Optional heartbeat prompt. Omission leaves the authored value unchanged.</summary>
    public string? Prompt { get => Get<string?>("prompt"); set => Changes["prompt"] = value; }
    /// <summary>Partial quiet-hours scheduling settings. Omission leaves the authored value unchanged.</summary>
    public QuietHoursPatch? QuietHours { get => Get<QuietHoursPatch?>("quietHours"); set => Changes["quietHours"] = value; }
}

/// <summary>Presence-aware nested operator settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class QuietHoursPatch : ConfigurationPatch
{
    /// <summary>Whether this nested feature is enabled. Omission leaves the authored value unchanged.</summary>
    public bool? Enabled { get => Get<bool?>("enabled"); set => Changes["enabled"] = value; }
    /// <summary>Quiet-hours start time. Omission leaves the authored value unchanged.</summary>
    public string? Start { get => Get<string?>("start"); set => Changes["start"] = value; }
    /// <summary>Quiet-hours end time. Omission leaves the authored value unchanged.</summary>
    public string? End { get => Get<string?>("end"); set => Changes["end"] = value; }
    /// <summary>Time zone used by this feature. Omission leaves the authored value unchanged.</summary>
    public string? Timezone { get => Get<string?>("timezone"); set => Changes["timezone"] = value; }
}
