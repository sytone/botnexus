using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Single entry point for the full session-compaction pipeline so every caller
/// (auto-compact at the token threshold, inbound <c>control: compact</c>
/// messages, and the SignalR <c>/compact</c> RPC from the Blazor portal) gets
/// identical behaviour: optional pre-compaction memory flush, summarise older
/// history, apply + persist with optimistic concurrency, and evict the cached
/// agent handle so the next turn rebuilds from post-compaction history.
///
/// User notification is intentionally a separate post-compaction step
/// (<see cref="BuildNotificationText"/> + <see cref="TryPublishNotificationAsync"/>).
/// Channel-driven callers persist the canonical notification in the session, then publish the
/// committed item through the channel-neutral conversation event seam. SignalR RPC and command
/// callers may still use the canonical text as their direct return value without forking it.
///
/// Introduced to fix three subtly different compaction code paths that left
/// the manual paths missing agent-handle eviction (Bug 3 in PR #602),
/// missing the pre-compaction memory flush, and emitting inconsistent feedback.
/// </summary>
public interface ISessionCompactionCoordinator
{
    /// <summary>
    /// Run flush + compact + save + handle-eviction. Does not emit any user
    /// notification — callers use <see cref="BuildNotificationText"/> and
    /// <see cref="TryPublishNotificationAsync"/> for that.
    /// </summary>
    /// <param name="agentId">Target agent.</param>
    /// <param name="session">The session to compact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="force">When true, compaction proceeds unconditionally
    /// regardless of token thresholds or preserved-turn limits. Used by
    /// user-initiated /compact commands where the user's intent overrides
    /// automatic heuristics.</param>
    /// <param name="handlePolicy">Controls whether an applied compaction evicts the cached handle.
    /// Mid-loop callers must keep their executing handle and resynchronise its context instead.</param>
    /// <param name="resolvedOptions">The options snapshot used for the caller's scoped threshold
    /// decision. Flush and compaction must share this snapshot (with the force override applied).
    /// Null retains the configured global options for manual and default callers.</param>
    Task<SessionCompactionOutcome> CompactAsync(
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken,
        bool force = false,
        CompactionHandlePolicy handlePolicy = CompactionHandlePolicy.Evict,
        CompactionOptions? resolvedOptions = null);

    /// <summary>
    /// Build the canonical user-facing notification text for an outcome.
    /// The same text is used by all callers so users see consistent feedback
    /// regardless of which path triggered compaction.
    /// </summary>
    string BuildNotificationText(SessionCompactionOutcome outcome);

    /// <summary>
    /// Appends and persists the canonical notification, then publishes the exact committed
    /// session item through the conversation-event seam. Persistence failures propagate and publish
    /// no success fact. Publication rejection or failure is non-transactional: it is logged and
    /// returns <c>false</c>, while the durable session item remains available for reconciliation.
    /// </summary>
    Task<bool> TryPublishNotificationAsync(
        SessionCompactionOutcome outcome,
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken);
}


/// <summary>
/// Selects how the coordinator treats the cached agent handle after an applied compaction.
/// </summary>
public enum CompactionHandlePolicy
{
    /// <summary>Evict the cached handle so a later external turn rebuilds from persisted history.</summary>
    Evict,

    /// <summary>Keep the currently executing handle; the caller must resynchronise its live context.</summary>
    KeepCurrent,
}

/// <summary>
/// Result of <see cref="ISessionCompactionCoordinator.CompactAsync"/>.
/// </summary>
/// <param name="Succeeded">Whether <see cref="ISessionCompactor"/> returned a valid summary.</param>
/// <param name="Applied">Whether the new history was actually applied to the session.</param>
/// <param name="HistoryOutcome">Outcome of the optimistic-concurrency apply step.</param>
/// <param name="EntriesSummarized">Number of older entries collapsed into the summary.</param>
/// <param name="EntriesPreserved">Number of recent entries kept verbatim.</param>
/// <param name="TokensBefore">Approximate token count before compaction.</param>
/// <param name="TokensAfter">Approximate token count after compaction.</param>
/// <param name="FailureReason">Human-readable reason when <paramref name="Applied"/> is false.</param>
/// <param name="SkipReason">Stable machine-readable code naming the abort branch.</param>
/// <param name="FailureExceptionType">
/// #3362: the CLR type name of the exception that aborted compaction, when one was thrown
/// (e.g. <c>IOException</c>). Null when no exception was involved. Carried separately from
/// <paramref name="FailureReason"/> so an operator can tell an I/O fault from a deserialization
/// fault without parsing prose — the collapsed cause information is exactly what made a read
/// failure look like a summarization-model failure.
/// </param>
public sealed record SessionCompactionOutcome(
    bool Succeeded,
    bool Applied,
    HistoryReplaceOutcome HistoryOutcome,
    int EntriesSummarized,
    int EntriesPreserved,
    int TokensBefore,
    int TokensAfter,
    string? FailureReason,
    CompactionSkipReason? SkipReason = null,
    string? FailureExceptionType = null);
