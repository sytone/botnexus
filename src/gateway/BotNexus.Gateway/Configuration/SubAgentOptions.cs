namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configures background sub-agent spawning limits and defaults.
/// </summary>
public sealed class SubAgentOptions
{
    /// <summary>
    /// Gets or sets the maximum number of concurrent sub-agents allowed per parent session.
    /// </summary>
    public int MaxConcurrentPerSession { get; set; } = 5;

    /// <summary>
    /// Gets or sets the default maximum turn budget applied to sub-agent runs.
    /// </summary>
    public int DefaultMaxTurns { get; set; } = 30;

    /// <summary>
    /// Gets or sets the default timeout, in seconds, applied to sub-agent runs.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the maximum allowed nested sub-agent depth.
    /// </summary>
    public int MaxDepth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the default model for sub-agent runs.
    /// Empty uses the parent model.
    /// </summary>
    public string DefaultModel { get; set; } = "";
}
