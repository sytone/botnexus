using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// Response for the effective agent config endpoint. Contains the resolved
/// configuration with provenance metadata per field.
/// </summary>
public sealed class EffectiveAgentConfigResponse
{
    /// <summary>The agent identifier.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Whether world-level defaults were applied to produce this config.</summary>
    public bool DefaultsApplied { get; set; }

    /// <summary>The resolved (effective) agent configuration.</summary>
    public EffectiveAgentConfigDto Config { get; set; } = new();

    /// <summary>
    /// Provenance per field. Keys are field paths (e.g. <c>"toolIds"</c>, <c>"memory.enabled"</c>).
    /// Values are one of: <c>"agent"</c>, <c>"inherited"</c>, <c>"world-default"</c>, <c>"implicit-default"</c>.
    /// </summary>
    public Dictionary<string, string> Sources { get; set; } = [];
}

/// <summary>
/// The effective resolved fields from an agent's merged configuration.
/// </summary>
public sealed class EffectiveAgentConfigDto
{
    /// <summary>Resolved tool IDs available to the agent.</summary>
    public List<string>? ToolIds { get; set; }

    /// <summary>Resolved memory configuration.</summary>
    public MemoryAgentConfig? Memory { get; set; }

    /// <summary>Resolved heartbeat configuration.</summary>
    public HeartbeatAgentConfig? Heartbeat { get; set; }

    /// <summary>Resolved file-access policy.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }
}
