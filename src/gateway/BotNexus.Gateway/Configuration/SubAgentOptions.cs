using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configures background sub-agent spawning limits and defaults.
/// </summary>
public sealed class SubAgentOptions
{
    /// <summary>
    /// Gets or sets the maximum number of concurrent sub-agents allowed per parent session.
    /// </summary>
    [Display(
        Name = "Max concurrent per session",
        Description = "Maximum number of concurrent sub-agents allowed per parent session.",
        GroupName = "Sub-agents",
        Order = 0)]
    [DefaultValue(5)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 0)]
    public int MaxConcurrentPerSession { get; set; } = 5;

    /// <summary>
    /// Gets or sets the default maximum turn budget applied to sub-agent runs.
    /// </summary>
    [Display(
        Name = "Default max turns",
        Description = "Default maximum turn budget applied to sub-agent runs when a spawn request does not specify one.",
        GroupName = "Sub-agents",
        Order = 1)]
    [DefaultValue(30)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 1)]
    public int DefaultMaxTurns { get; set; } = 30;

    /// <summary>
    /// Gets or sets the hard upper bound for an agent-supplied <c>maxTurns</c> on a spawn request.
    /// A caller may request fewer turns, but anything above this ceiling is clamped down to it.
    /// This mirrors the runaway-cost guard the <c>agent_converse</c> tool applies via
    /// <c>AgentExchangeOptions.MaxTurnsCeiling</c>: depth and concurrency are already capped, but
    /// without a turn ceiling a single <c>spawn_subagent</c> call could request an unbounded budget.
    /// A value of zero or less disables the ceiling.
    /// </summary>
    [Display(
        Name = "Max turns ceiling",
        Description = "Hard upper bound for an agent-supplied maxTurns on a spawn request. A value of zero or less disables the ceiling.",
        GroupName = "Sub-agents",
        Order = 2)]
    [DefaultValue(30)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 2)]
    public int MaxTurnsCeiling { get; set; } = 30;

    /// <summary>
    /// Gets or sets the default timeout, in seconds, applied to sub-agent runs.
    /// </summary>
    [Display(
        Name = "Default timeout (seconds)",
        Description = "Default timeout, in seconds, applied to sub-agent runs when a spawn request does not specify one.",
        GroupName = "Sub-agents",
        Order = 3)]
    [DefaultValue(600)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 3)]
    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the hard upper bound, in seconds, for an agent-supplied <c>timeoutSeconds</c>
    /// on a spawn request. Today only the timeout is wired to a real cancellation budget, so an
    /// unclamped <c>timeoutSeconds</c> (e.g. hundreds of millions) would let a background sub-agent
    /// run effectively forever. A caller may request a shorter timeout, but anything above this
    /// maximum is clamped down to it. A value of zero or less disables the ceiling.
    /// </summary>
    [Display(
        Name = "Max timeout (seconds)",
        Description = "Hard upper bound, in seconds, for an agent-supplied timeoutSeconds on a spawn request. A value of zero or less disables the ceiling.",
        GroupName = "Sub-agents",
        Order = 4)]
    [DefaultValue(1800)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 4)]
    public int MaxTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Gets or sets the maximum allowed nested sub-agent depth.
    /// </summary>
    [Display(
        Name = "Max depth",
        Description = "Maximum allowed nested sub-agent depth.",
        GroupName = "Sub-agents",
        Order = 5)]
    [DefaultValue(1)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 5)]
    public int MaxDepth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the default model for sub-agent runs.
    /// Empty uses the parent model.
    /// </summary>
    [Display(
        Name = "Default model",
        Description = "Default model for sub-agent runs. Empty uses the parent agent's model.",
        GroupName = "Sub-agents",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "sub-agents", Order = 6, OptionsSource = "models")]
    public string DefaultModel { get; set; } = "";

    /// <summary>
    /// Gets or sets how long, in minutes, a completed/failed/killed/timed-out sub-agent record is
    /// retained in memory after it finishes so that <c>list_subagents</c> and status queries can
    /// still surface a recently-finished sub-agent. After this window the record is swept and its
    /// timeout <see cref="System.Threading.CancellationTokenSource"/> disposed, bounding the manager's
    /// in-memory registry on a long-lived gateway that spawns many sub-agents. A value of zero or
    /// less disables time-based eviction (the count cap below still applies). Running records are
    /// never evicted.
    /// </summary>
    [Display(
        Name = "Completed record retention (minutes)",
        Description = "How long, in minutes, a finished sub-agent record is retained in memory for status queries. A value of zero or less disables time-based eviction.",
        GroupName = "Sub-agents",
        Order = 7)]
    [DefaultValue(15)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 7)]
    public int CompletedRecordRetentionMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum number of <em>completed</em> sub-agent records to retain regardless
    /// of age. When the count of completed records exceeds this cap, the oldest completed records are
    /// evicted first (and their timeout source disposed) - a burst-spawn backstop so the registry
    /// stays bounded even within the retention window. A value of zero or less disables the cap.
    /// Running records do not count against this cap and are never evicted.
    /// </summary>
    [Display(
        Name = "Max retained completed records",
        Description = "Maximum number of completed sub-agent records to retain regardless of age. A value of zero or less disables the cap.",
        GroupName = "Sub-agents",
        Order = 8)]
    [DefaultValue(200)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "sub-agents", Order = 8)]
    public int MaxRetainedCompletedRecords { get; set; } = 200;

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
