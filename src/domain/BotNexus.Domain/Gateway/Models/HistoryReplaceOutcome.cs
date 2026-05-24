namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Outcome of <see cref="GatewaySessionRuntime.TryReplaceHistoryFromSnapshot"/>.
/// Encodes the optimistic-concurrency decision made when a caller (typically a
/// session compactor) wants to swap the live history with a derived list built
/// from an earlier snapshot.
/// </summary>
public enum HistoryReplaceOutcome
{
    /// <summary>
    /// The snapshot was still current — no mutations happened during the
    /// caller's work window. The replacement is applied verbatim.
    /// </summary>
    Applied,

    /// <summary>
    /// Only additions happened (one or more <c>AddEntry</c>/<c>AddEntries</c>
    /// calls). The replacement is applied with the concurrent tail appended
    /// after it so no entries are dropped.
    /// </summary>
    Rebased,

    /// <summary>
    /// A destructive change happened during the work window
    /// (another <c>ReplaceHistory</c>, or a <c>RemoveCrashSentinels</c> that
    /// removed at least one entry). The apply is refused; the live history is
    /// left unchanged. Callers should treat this like a transient conflict
    /// and may retry from a fresh snapshot.
    /// </summary>
    Aborted
}
