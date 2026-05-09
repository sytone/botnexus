using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Triggers;

/// <summary>
/// Internal trigger contract for non-channel session creation paths.
/// </summary>
public interface IInternalTrigger
{
    /// <summary>
    /// Gets the trigger type identifier.
    /// </summary>
    TriggerType Type { get; }

    /// <summary>
    /// Gets a human-readable display name for this trigger.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Creates and executes a session for this trigger source.
    /// </summary>
    Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null);
}

/// <summary>
/// Optional trigger request options supplied by internal schedulers and background workflows.
/// </summary>
public sealed record InternalTriggerRequest
{
    /// <summary>
    /// Optional cron job identifier used for traceability and session-id composition.
    /// </summary>
    public string? CronJobId { get; init; }

    /// <summary>
    /// Optional model override for this trigger execution.
    /// Supports either "model-id" (current provider) or "provider/model-id".
    /// </summary>
    public string? ModelOverride { get; init; }
}
