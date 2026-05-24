using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Compacts session history by summarizing older messages while preserving recent turns.
/// </summary>
public interface ISessionCompactor
{
    /// <summary>
    /// Determines whether the session should be compacted based on history size and options.
    /// </summary>
    bool ShouldCompact(Session session, CompactionOptions options);

    /// <summary>
    /// Compacts the session history: snapshots history atomically together with the
    /// destructive-mutation version, summarises older messages off-lock, and returns
    /// the new history plus the snapshot's version + count so the caller can apply
    /// the result via <c>TryReplaceHistoryFromSnapshot</c> with optimistic-concurrency
    /// detection. The compactor never mutates <paramref name="session"/> directly.
    /// </summary>
    Task<CompactionResult> CompactAsync(
        GatewaySession session,
        CompactionOptions options,
        CancellationToken cancellationToken = default);
}
