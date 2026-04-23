using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// World-level defaults that are field-merged into each agent's effective config.
/// Exposed in JSON as <c>agents.defaults</c>.
/// </summary>
public sealed class AgentDefaultsConfig
{
    /// <summary>Default tool IDs inherited by agents that do not explicitly set their own toolIds.</summary>
    public List<string>? ToolIds { get; set; }

    /// <summary>Default memory configuration inherited by agents.</summary>
    public MemoryAgentConfig? Memory { get; set; }

    /// <summary>Default heartbeat configuration inherited by agents.</summary>
    public HeartbeatAgentConfig? Heartbeat { get; set; }

    /// <summary>Default file access policy inherited by agents.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }
}
