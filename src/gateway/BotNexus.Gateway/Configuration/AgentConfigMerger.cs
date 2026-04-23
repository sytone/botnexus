using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Merges <see cref="AgentDefaultsConfig" /> into an <see cref="AgentDefinitionConfig" /> field-by-field.
/// Agent-explicitly-set fields win; absent fields inherit from defaults.
/// Presence is detected via the optional raw JSON element for the agent config.
/// </summary>
public static class AgentConfigMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Produces a new <see cref="AgentDefinitionConfig" /> with defaults merged in.
    /// </summary>
    /// <param name="defaults">World-level agent defaults (may be null).</param>
    /// <param name="agent">Agent-specific config.</param>
    /// <param name="agentRawElement">
    /// Raw JSON element for the agent definition used for presence detection.
    /// When null, any null field on the agent is assumed to mean "inherit from defaults".
    /// </param>
    public static AgentDefinitionConfig Merge(
        AgentDefaultsConfig? defaults,
        AgentDefinitionConfig agent,
        JsonElement? agentRawElement = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (defaults is null)
            return agent;

        // Determine presence of agent-level fields via raw JSON
        var agentObj = agentRawElement is { ValueKind: JsonValueKind.Object } el ? el : (JsonElement?)null;

        return new AgentDefinitionConfig
        {
            // Identity / provider fields — never inherited from defaults
            Provider = agent.Provider,
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            Model = agent.Model,
            AllowedModels = agent.AllowedModels,
            SystemPromptFile = agent.SystemPromptFile,
            SystemPromptFiles = agent.SystemPromptFiles,
            SubAgents = agent.SubAgents,
            IsolationStrategy = agent.IsolationStrategy,
            MaxConcurrentSessions = agent.MaxConcurrentSessions,
            Metadata = agent.Metadata,
            IsolationOptions = agent.IsolationOptions,
            Enabled = agent.Enabled,
            Soul = agent.Soul,
            SessionAccess = agent.SessionAccess,
            ToolPolicy = agent.ToolPolicy,
            Extensions = agent.Extensions,

            // Inherited / merged fields
            ToolIds = MergeToolIds(defaults.ToolIds, agent.ToolIds, agentObj),
            Memory = MergeMemory(defaults.Memory, agent.Memory, agentObj),
            Heartbeat = MergeHeartbeat(defaults.Heartbeat, agent.Heartbeat, agentObj),
            FileAccess = MergeFileAccess(defaults.FileAccess, agent.FileAccess, agentObj),
        };
    }

    // -------------------------------------------------------------------------
    // ToolIds: replacement when agent explicitly sets them
    // -------------------------------------------------------------------------

    private static List<string>? MergeToolIds(
        List<string>? defaults,
        List<string>? agent,
        JsonElement? agentObj)
    {
        // If agent JSON explicitly contained "toolIds", use it (even if empty list)
        if (agentObj is not null && agentObj.Value.TryGetProperty("toolIds", out _))
            return agent;

        // No agent override — inherit defaults
        return agent ?? defaults;
    }

    // -------------------------------------------------------------------------
    // Memory
    // -------------------------------------------------------------------------

    internal static MemoryAgentConfig? MergeMemory(
        MemoryAgentConfig? defaults,
        MemoryAgentConfig? agent,
        JsonElement? agentObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneMemory(agent);
        if (agent is null)
        {
            // Did agent JSON explicitly set "memory" to null? Only if the key existed.
            if (agentObj is not null && agentObj.Value.TryGetProperty("memory", out var memProp) && memProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneMemory(defaults);
        }

        // Both exist — deep merge
        var agentMemObj = agentObj is not null && agentObj.Value.TryGetProperty("memory", out var mProp) && mProp.ValueKind == JsonValueKind.Object
            ? mProp : (JsonElement?)null;

        return new MemoryAgentConfig
        {
            Enabled = PickBool("enabled", defaults.Enabled, agent.Enabled, agentMemObj),
            Indexing = PickString("indexing", defaults.Indexing, agent.Indexing, agentMemObj),
            Search = MergeMemorySearch(defaults.Search, agent.Search, agentMemObj),
        };
    }

    private static MemorySearchAgentConfig? MergeMemorySearch(
        MemorySearchAgentConfig? defaults,
        MemorySearchAgentConfig? agent,
        JsonElement? agentMemObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneMemorySearch(agent);
        if (agent is null)
        {
            if (agentMemObj is not null && agentMemObj.Value.TryGetProperty("search", out var sProp) && sProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneMemorySearch(defaults);
        }

        var agentSearchObj = agentMemObj is not null && agentMemObj.Value.TryGetProperty("search", out var searchProp) && searchProp.ValueKind == JsonValueKind.Object
            ? searchProp : (JsonElement?)null;

        return new MemorySearchAgentConfig
        {
            DefaultTopK = PickInt("defaultTopK", defaults.DefaultTopK, agent.DefaultTopK, agentSearchObj),
            TemporalDecay = MergeTemporalDecay(defaults.TemporalDecay, agent.TemporalDecay, agentSearchObj),
        };
    }

    private static TemporalDecayAgentConfig? MergeTemporalDecay(
        TemporalDecayAgentConfig? defaults,
        TemporalDecayAgentConfig? agent,
        JsonElement? agentSearchObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneTemporalDecay(agent);
        if (agent is null)
        {
            if (agentSearchObj is not null && agentSearchObj.Value.TryGetProperty("temporalDecay", out var tdProp) && tdProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneTemporalDecay(defaults);
        }

        var agentTdObj = agentSearchObj is not null && agentSearchObj.Value.TryGetProperty("temporalDecay", out var tProp) && tProp.ValueKind == JsonValueKind.Object
            ? tProp : (JsonElement?)null;

        return new TemporalDecayAgentConfig
        {
            Enabled = PickBool("enabled", defaults.Enabled, agent.Enabled, agentTdObj),
            HalfLifeDays = PickInt("halfLifeDays", defaults.HalfLifeDays, agent.HalfLifeDays, agentTdObj),
        };
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    internal static HeartbeatAgentConfig? MergeHeartbeat(
        HeartbeatAgentConfig? defaults,
        HeartbeatAgentConfig? agent,
        JsonElement? agentObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneHeartbeat(agent);
        if (agent is null)
        {
            if (agentObj is not null && agentObj.Value.TryGetProperty("heartbeat", out var hProp) && hProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneHeartbeat(defaults);
        }

        var agentHbObj = agentObj is not null && agentObj.Value.TryGetProperty("heartbeat", out var hbProp) && hbProp.ValueKind == JsonValueKind.Object
            ? hbProp : (JsonElement?)null;

        return new HeartbeatAgentConfig
        {
            Enabled = PickBool("enabled", defaults.Enabled, agent.Enabled, agentHbObj),
            IntervalMinutes = PickInt("intervalMinutes", defaults.IntervalMinutes, agent.IntervalMinutes, agentHbObj),
            Prompt = PickNullableString("prompt", defaults.Prompt, agent.Prompt, agentHbObj),
            QuietHours = MergeQuietHours(defaults.QuietHours, agent.QuietHours, agentHbObj),
        };
    }

    private static QuietHoursConfig? MergeQuietHours(
        QuietHoursConfig? defaults,
        QuietHoursConfig? agent,
        JsonElement? agentHbObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneQuietHours(agent);
        if (agent is null)
        {
            if (agentHbObj is not null && agentHbObj.Value.TryGetProperty("quietHours", out var qhProp) && qhProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneQuietHours(defaults);
        }

        var agentQhObj = agentHbObj is not null && agentHbObj.Value.TryGetProperty("quietHours", out var qProp) && qProp.ValueKind == JsonValueKind.Object
            ? qProp : (JsonElement?)null;

        return new QuietHoursConfig
        {
            Enabled = PickBool("enabled", defaults.Enabled, agent.Enabled, agentQhObj),
            Start = PickString("start", defaults.Start, agent.Start, agentQhObj),
            End = PickString("end", defaults.End, agent.End, agentQhObj),
            Timezone = PickNullableString("timezone", defaults.Timezone, agent.Timezone, agentQhObj),
        };
    }

    // -------------------------------------------------------------------------
    // FileAccess
    // -------------------------------------------------------------------------

    internal static FileAccessPolicyConfig? MergeFileAccess(
        FileAccessPolicyConfig? defaults,
        FileAccessPolicyConfig? agent,
        JsonElement? agentObj)
    {
        if (defaults is null)
            return agent is null ? null : CloneFileAccess(agent);
        if (agent is null)
        {
            if (agentObj is not null && agentObj.Value.TryGetProperty("fileAccess", out var faProp) && faProp.ValueKind == JsonValueKind.Null)
                return null;
            return CloneFileAccess(defaults);
        }

        var agentFaObj = agentObj is not null && agentObj.Value.TryGetProperty("fileAccess", out var fProp) && fProp.ValueKind == JsonValueKind.Object
            ? fProp : (JsonElement?)null;

        return new FileAccessPolicyConfig
        {
            AllowedReadPaths = PickList("allowedReadPaths", defaults.AllowedReadPaths, agent.AllowedReadPaths, agentFaObj),
            AllowedWritePaths = PickList("allowedWritePaths", defaults.AllowedWritePaths, agent.AllowedWritePaths, agentFaObj),
            DeniedPaths = PickList("deniedPaths", defaults.DeniedPaths, agent.DeniedPaths, agentFaObj),
        };
    }

    // -------------------------------------------------------------------------
    // Presence-aware scalar pickers
    // -------------------------------------------------------------------------

    private static bool PickBool(string propName, bool defaultVal, bool agentVal, JsonElement? agentObj)
    {
        if (agentObj is null)
            // No raw JSON — if agent value differs from its type default, treat as explicit
            return agentObj is null && agentVal != default ? agentVal : defaultVal;

        return agentObj.Value.TryGetProperty(propName, out _) ? agentVal : defaultVal;
    }

    private static int PickInt(string propName, int defaultVal, int agentVal, JsonElement? agentObj)
    {
        if (agentObj is null)
            return agentVal != default ? agentVal : defaultVal;

        return agentObj.Value.TryGetProperty(propName, out _) ? agentVal : defaultVal;
    }

    private static string PickString(string propName, string defaultVal, string agentVal, JsonElement? agentObj)
    {
        if (agentObj is null)
            return !string.IsNullOrEmpty(agentVal) ? agentVal : defaultVal;

        return agentObj.Value.TryGetProperty(propName, out _) ? agentVal : defaultVal;
    }

    private static string? PickNullableString(string propName, string? defaultVal, string? agentVal, JsonElement? agentObj)
    {
        if (agentObj is null)
            return agentVal ?? defaultVal;

        return agentObj.Value.TryGetProperty(propName, out _) ? agentVal : defaultVal;
    }

    private static List<string>? PickList(string propName, List<string>? defaultVal, List<string>? agentVal, JsonElement? agentObj)
    {
        if (agentObj is null)
            return agentVal ?? defaultVal;

        return agentObj.Value.TryGetProperty(propName, out _) ? agentVal : defaultVal;
    }

    // -------------------------------------------------------------------------
    // Clone helpers
    // -------------------------------------------------------------------------

    private static MemoryAgentConfig CloneMemory(MemoryAgentConfig src) => new()
    {
        Enabled = src.Enabled,
        Indexing = src.Indexing,
        Search = src.Search is null ? null : CloneMemorySearch(src.Search),
    };

    private static MemorySearchAgentConfig CloneMemorySearch(MemorySearchAgentConfig src) => new()
    {
        DefaultTopK = src.DefaultTopK,
        TemporalDecay = src.TemporalDecay is null ? null : CloneTemporalDecay(src.TemporalDecay),
    };

    private static TemporalDecayAgentConfig CloneTemporalDecay(TemporalDecayAgentConfig src) => new()
    {
        Enabled = src.Enabled,
        HalfLifeDays = src.HalfLifeDays,
    };

    private static HeartbeatAgentConfig CloneHeartbeat(HeartbeatAgentConfig src) => new()
    {
        Enabled = src.Enabled,
        IntervalMinutes = src.IntervalMinutes,
        Prompt = src.Prompt,
        QuietHours = src.QuietHours is null ? null : CloneQuietHours(src.QuietHours),
    };

    private static QuietHoursConfig CloneQuietHours(QuietHoursConfig src) => new()
    {
        Enabled = src.Enabled,
        Start = src.Start,
        End = src.End,
        Timezone = src.Timezone,
    };

    private static FileAccessPolicyConfig CloneFileAccess(FileAccessPolicyConfig src) => new()
    {
        AllowedReadPaths = src.AllowedReadPaths is null ? null : new List<string>(src.AllowedReadPaths),
        AllowedWritePaths = src.AllowedWritePaths is null ? null : new List<string>(src.AllowedWritePaths),
        DeniedPaths = src.DeniedPaths is null ? null : new List<string>(src.DeniedPaths),
    };
}
