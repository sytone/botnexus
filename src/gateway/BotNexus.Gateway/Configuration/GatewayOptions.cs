using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>Cross-agent orchestration and built-in tool options bound from <c>gateway</c>.</summary>
public sealed class GatewayOptions
{
    /// <summary>
    /// Optional default agent used when no explicit target or session-bound agent is available.
    /// </summary>
    [Display(
        Name = "Default agent",
        Description = "Optional default agent used when no explicit target or session-bound agent is available.",
        GroupName = "Gateway",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 0)]
    public string? DefaultAgentId { get; set; }

    /// <summary>
    /// Maximum allowed depth for cross-agent/sub-agent call chains.
    /// </summary>
    [Display(
        Name = "Max call chain depth",
        Description = "Maximum allowed depth for cross-agent/sub-agent call chains.",
        GroupName = "Gateway",
        Order = 1)]
    [DefaultValue(10)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 1)]
    public int MaxCallChainDepth { get; set; } = 10;

    /// <summary>
    /// Maximum duration for cross-agent prompt calls before timing out.
    /// </summary>
    [Display(
        Name = "Cross-agent timeout (seconds)",
        Description = "Maximum duration, in seconds, for cross-agent prompt calls before timing out.",
        GroupName = "Gateway",
        Order = 2)]
    [DefaultValue(120)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 2)]
    public int CrossAgentTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum depth for <c>agent_converse</c> call chains.
    /// </summary>
    [Display(
        Name = "Agent conversation max depth",
        Description = "Maximum depth for agent_converse call chains.",
        GroupName = "Gateway",
        Order = 3)]
    [DefaultValue(3)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 3)]
    public int AgentConversationMaxDepth { get; set; } = 3;

    /// <summary>
    /// Options controlling background sub-agent spawning behavior.
    /// </summary>
    public SubAgentOptions SubAgents { get; set; } = new();

    /// <summary>
    /// Options controlling session pre-warming and multi-session subscription behavior.
    /// </summary>
    public SessionWarmupOptions SessionWarmup { get; set; } = new();

    /// <summary>
    /// Options controlling the built-in delay/wait tool.
    /// </summary>
    public DelayToolOptions DelayTool { get; set; } = new();

    /// <summary>
    /// Options controlling the built-in file watcher tool.
    /// </summary>
    public FileWatcherToolOptions FileWatcherTool { get; set; } = new();

    /// <summary>
    /// When <see langword="true"/>, the gateway automatically re-dispatches the last user
    /// message from sessions interrupted by an unclean restart. Defaults to
    /// <see langword="false"/> until the replay path is confirmed stable.
    /// </summary>
    [Display(
        Name = "Auto-replay interrupted turns",
        Description = "When on, the gateway automatically re-dispatches the last user message from sessions interrupted by an unclean restart.",
        GroupName = "Gateway",
        Order = 4)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "gateway", Order = 4)]
    public bool AutoReplayInterruptedTurns { get; set; } = false;

    /// <summary>
    /// Maximum number of automatic replay attempts for a single interrupted session before
    /// falling back to the notification-only path. Prevents infinite replay loops caused by
    /// messages that always crash the agent.
    /// </summary>
    [Display(
        Name = "Max auto-replay attempts",
        Description = "Maximum number of automatic replay attempts for a single interrupted session before falling back to notification-only.",
        GroupName = "Gateway",
        Order = 5)]
    [DefaultValue(2)]
    [Range(0, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 5)]
    public int MaxAutoReplayAttempts { get; set; } = 2;
}
