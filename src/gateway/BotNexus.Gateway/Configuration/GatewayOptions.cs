namespace BotNexus.Gateway.Configuration;

public sealed class GatewayOptions
{
    /// <summary>
    /// Optional default agent used when no explicit target or session-bound agent is available.
    /// </summary>
    public string? DefaultAgentId { get; set; }

    /// <summary>
    /// Maximum allowed depth for cross-agent/sub-agent call chains.
    /// </summary>
    public int MaxCallChainDepth { get; set; } = 10;

    /// <summary>
    /// Maximum duration for cross-agent prompt calls before timing out.
    /// </summary>
    public int CrossAgentTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Options controlling background sub-agent spawning behavior.
    /// </summary>
    public SubAgentOptions SubAgents { get; set; } = new();

    /// <summary>
    /// Options controlling session pre-warming and multi-session subscription behavior.
    /// </summary>
    public SessionWarmupOptions SessionWarmup { get; set; } = new();
}
