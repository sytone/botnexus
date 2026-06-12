namespace BotNexus.Gateway.Agents;

/// <summary>
/// Configuration for agent exchange budget limits and loop detection.
/// Bound from <c>gateway:agentExchange</c> in config.json.
/// </summary>
public sealed class AgentExchangeBudgetOptions
{
    /// <summary>
    /// Maximum total turns per agent pair per calendar day (UTC). Default 200.
    /// </summary>
    public int DailyTurnCap { get; set; } = 200;

    /// <summary>
    /// Window in seconds for loop detection. If the same pair re-engages within
    /// this window, the loop counter increments. Default 60.
    /// </summary>
    public int LoopDetectionWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Number of rapid re-engagements within the detection window before
    /// cooldown is triggered. Default 3.
    /// </summary>
    public int LoopThreshold { get; set; } = 3;

    /// <summary>
    /// Cooldown duration in seconds when a loop is detected. Default 300 (5 minutes).
    /// </summary>
    public int CooldownOnLoopDetectSeconds { get; set; } = 300;
}
