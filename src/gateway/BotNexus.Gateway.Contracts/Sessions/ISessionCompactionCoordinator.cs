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
/// User notification is intentionally a separate step (<see cref="BuildNotificationText"/>
/// + <see cref="TrySendChannelNotificationAsync"/>) so callers can deliver the
/// canonical text through whichever transport they own — a channel adapter for
/// inbound-channel-driven compactions, the SignalR hub return value plus a
/// local system message for the Blazor portal — without forking the text.
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
    /// <see cref="TrySendChannelNotificationAsync"/> for that.
    /// </summary>
    /// <param name="agentId">Target agent.</param>
    /// <param name="session">The session to compact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="force">When true, compaction proceeds unconditionally
    /// regardless of token thresholds or preserved-turn limits. Used by
    /// user-initiated /compact commands where the user's intent overrides
    /// automatic heuristics.</param>
    Task<SessionCompactionOutcome> CompactAsync(
        AgentId agentId,
        GatewaySession session,
        CancellationToken cancellationToken,
        bool force = false);

    /// <summary>
    /// Build the canonical user-facing notification text for an outcome.
    /// The same text is used by all callers so users see consistent feedback
    /// regardless of which path triggered compaction.
    /// </summary>
    string BuildNotificationText(SessionCompactionOutcome outcome);

    /// <summary>
    /// Convenience helper that resolves the channel adapter for
    /// <paramref name="channelType"/> and sends the canonical notification
    /// text. Swallows transport failures (logs a warning) so a delivery
    /// problem never masks a successful compaction. Returns <c>false</c> if
    /// the adapter could not be resolved or the send threw.
    /// </summary>
    Task<bool> TrySendChannelNotificationAsync(
        SessionCompactionOutcome outcome,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        string sessionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of <see cref="ISessionCompactionCoordinator.CompactAndNotifyAsync"/>.
/// </summary>
/// <param name="Succeeded">Whether <see cref="ISessionCompactor"/> returned a valid summary.</param>
/// <param name="Applied">Whether the new history was actually applied to the session.</param>
/// <param name="HistoryOutcome">Outcome of the optimistic-concurrency apply step.</param>
/// <param name="EntriesSummarized">Number of older entries collapsed into the summary.</param>
/// <param name="EntriesPreserved">Number of recent entries kept verbatim.</param>
/// <param name="TokensBefore">Approximate token count before compaction.</param>
/// <param name="TokensAfter">Approximate token count after compaction.</param>
/// <param name="FailureReason">Human-readable reason when <paramref name="Applied"/> is false.</param>
public sealed record SessionCompactionOutcome(
    bool Succeeded,
    bool Applied,
    HistoryReplaceOutcome HistoryOutcome,
    int EntriesSummarized,
    int EntriesPreserved,
    int TokensBefore,
    int TokensAfter,
    string? FailureReason);
