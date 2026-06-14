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
    /// Gets or sets the hard upper bound for an agent-supplied <c>maxTurns</c> on a spawn request.
    /// A caller may request fewer turns, but anything above this ceiling is clamped down to it.
    /// This mirrors the runaway-cost guard the <c>agent_converse</c> tool applies via
    /// <c>AgentExchangeOptions.MaxTurnsCeiling</c>: depth and concurrency are already capped, but
    /// without a turn ceiling a single <c>spawn_subagent</c> call could request an unbounded budget.
    /// A value of zero or less disables the ceiling.
    /// </summary>
    public int MaxTurnsCeiling { get; set; } = 30;

    /// <summary>
    /// Gets or sets the default timeout, in seconds, applied to sub-agent runs.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the hard upper bound, in seconds, for an agent-supplied <c>timeoutSeconds</c>
    /// on a spawn request. Today only the timeout is wired to a real cancellation budget, so an
    /// unclamped <c>timeoutSeconds</c> (e.g. hundreds of millions) would let a background sub-agent
    /// run effectively forever. A caller may request a shorter timeout, but anything above this
    /// maximum is clamped down to it. A value of zero or less disables the ceiling.
    /// </summary>
    public int MaxTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Gets or sets the maximum allowed nested sub-agent depth.
    /// </summary>
    public int MaxDepth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the default model for sub-agent runs.
    /// Empty uses the parent model.
    /// </summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>
    /// Resolves the effective turn budget for a spawn request: a non-positive request value falls
    /// back to <see cref="DefaultMaxTurns"/>, and the result is clamped to at most
    /// <see cref="MaxTurnsCeiling"/> (when the ceiling is positive). The floor is always one turn.
    /// </summary>
    /// <param name="requestedMaxTurns">The agent-supplied <c>maxTurns</c> value.</param>
    /// <returns>The clamped turn budget, never less than one.</returns>
    public int ResolveMaxTurns(int requestedMaxTurns)
    {
        var resolved = requestedMaxTurns > 0 ? requestedMaxTurns : DefaultMaxTurns;
        if (resolved < 1)
            resolved = 1;
        if (MaxTurnsCeiling > 0 && resolved > MaxTurnsCeiling)
            resolved = MaxTurnsCeiling;
        return resolved;
    }

    /// <summary>
    /// Resolves the effective timeout, in seconds, for a spawn request: a non-positive request value
    /// falls back to <see cref="DefaultTimeoutSeconds"/>, and the result is clamped to at most
    /// <see cref="MaxTimeoutSeconds"/> (when the maximum is positive). The floor is always one second.
    /// </summary>
    /// <param name="requestedTimeoutSeconds">The agent-supplied <c>timeoutSeconds</c> value.</param>
    /// <returns>The clamped timeout in seconds, never less than one.</returns>
    public int ResolveTimeoutSeconds(int requestedTimeoutSeconds)
    {
        var resolved = requestedTimeoutSeconds > 0 ? requestedTimeoutSeconds : DefaultTimeoutSeconds;
        if (resolved < 1)
            resolved = 1;
        if (MaxTimeoutSeconds > 0 && resolved > MaxTimeoutSeconds)
            resolved = MaxTimeoutSeconds;
        return resolved;
    }
}
