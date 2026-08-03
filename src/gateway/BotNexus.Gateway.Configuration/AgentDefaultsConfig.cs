using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Configuration;

/// <summary>
/// World-level defaults that are field-merged into each agent's effective config.
/// Exposed in JSON as <c>agents.defaults</c>.
/// </summary>
public sealed class AgentDefaultsConfig
{
    /// <summary>
    /// Five minutes accommodates tools that wait on another agent while still bounding genuinely stuck calls.
    /// </summary>
    public const int DefaultToolTimeoutSeconds = 300;

    /// <summary>Default tool IDs inherited by agents that do not explicitly set their own toolIds.</summary>
    public List<string>? ToolIds { get; set; }

    /// <summary>
    /// Default per-tool timeout (seconds) inherited by agents that do not explicitly set their own timeout.
    /// </summary>
    [Display(
        Name = "Tool timeout (seconds)",
        Description = "Default per-tool timeout, in seconds, inherited by agents that do not set their own. Defaults to 300 seconds.",
        GroupName = "Agent defaults",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "agent-defaults", Order = 0)]
    [DefaultValue(DefaultToolTimeoutSeconds)]
    public int? ToolTimeoutSeconds { get; set; } = DefaultToolTimeoutSeconds;

    /// <summary>Default memory configuration inherited by agents.</summary>
    public MemoryAgentConfig? Memory { get; set; }

    /// <summary>Default heartbeat configuration inherited by agents.</summary>
    public HeartbeatAgentConfig? Heartbeat { get; set; }

    /// <summary>Default file access policy inherited by agents.</summary>
    public FileAccessPolicyConfig? FileAccess { get; set; }
}
