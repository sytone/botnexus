using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Apply helpers for <see cref="CompactionResult"/> values produced by
/// <see cref="ISessionCompactor.CompactAsync"/>. Centralises the optimistic-
/// concurrency apply protocol introduced for #532 so the three production
/// callsites (auto-compaction, <c>/compact</c> command, SignalR
/// <c>CompactSession</c>) cannot drift apart on the apply semantics.
/// </summary>
public static class SessionCompactionExtensions
{
    /// <summary>
    /// Applies <paramref name="result"/>'s compacted history to <paramref name="session"/>
    /// using the snapshot identity recorded in the result. The runtime decides whether
    /// to apply verbatim, rebase concurrent additions, or abort because of a concurrent
    /// destructive change (see <see cref="HistoryReplaceOutcome"/>).
    /// </summary>
    /// <remarks>
    /// The caller must check the returned outcome and log/surface the Rebased or
    /// Aborted cases — <see cref="CompactionResult.TokensAfter"/> and the
    /// "summarized/preserved" counts on the result are computed against the snapshot
    /// and become approximate once additions are rebased on top.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when either argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="result"/> did not succeed or carries a null
    /// <see cref="CompactionResult.CompactedHistory"/> — callers should guard with
    /// <see cref="CompactionResult.Succeeded"/> before invoking.
    /// </exception>
    public static HistoryReplaceOutcome TryApplyCompactionResult(
        this GatewaySession session,
        CompactionResult result)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded || result.CompactedHistory is null)
        {
            throw new InvalidOperationException(
                "Cannot apply a compaction result that did not succeed or has a null CompactedHistory. " +
                "Check CompactionResult.Succeeded before calling TryApplyCompactionResult.");
        }

        return session.TryReplaceHistoryFromSnapshot(
            result.CompactedHistory,
            result.SnapshotDestructiveVersion,
            result.SnapshotHistoryCount);
    }
}
