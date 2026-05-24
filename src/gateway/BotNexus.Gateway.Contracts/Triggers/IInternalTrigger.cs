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
    public JobId? CronJobId { get; init; }

    /// <summary>
    /// Optional model override for this trigger execution.
    /// Supports either "model-id" (current provider) or "provider/model-id".
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Optional explicit conversation ID to route this trigger run into.
    /// When null, the trigger determines the conversation (e.g. per-job stable conversation for cron).
    /// </summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>
    /// Optional human-readable job name used as the conversation title when the trigger creates a new conversation.
    /// Provided by the caller (e.g. cron scheduler) so the trigger does not need to perform a separate job lookup.
    /// </summary>
    public string? JobName { get; init; }

    /// <summary>
    /// Written back by the trigger after the conversation for this run has been resolved or created.
    /// Callers can read this value to persist the conversation ID for fast-path reuse on subsequent runs.
    /// </summary>
    public ConversationId? ResolvedConversationId { get; set; }
}