using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Result of a compaction operation.
/// </summary>
public sealed record CompactionResult
{
    /// <summary>The generated summary text.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// True when compaction produced a non-empty summary and the caller should apply
    /// <see cref="CompactedHistory"/> via <c>ReplaceHistory</c>. False when compaction
    /// was skipped or aborted (e.g. empty LLM response) — history is unchanged.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// The new session history to apply when <see cref="Succeeded"/> is true. After Phase 3a
    /// (#531) this contains the FULL original history with the summarised range marked
    /// <c>IsHistory = true</c>, plus the new summary entry inserted at the historical→preserved
    /// boundary, plus the preserved tail. The list grows on every cycle — historical fidelity
    /// is preserved in the session store while only the LLM-visible projection shrinks.
    /// Null when <see cref="Succeeded"/> is false.
    /// </summary>
    public IReadOnlyList<SessionEntry>? CompactedHistory { get; init; }

    /// <summary>
    /// Destructive-mutation version observed at snapshot time (#532). Captured
    /// by <c>SnapshotHistoryForCompaction</c> and used by
    /// <c>TryReplaceHistoryFromSnapshot</c> to detect concurrent destructive
    /// changes (other compactions, heartbeat history restores, crash-sentinel
    /// removals with effect) and choose the safe apply path. Always populated
    /// when <see cref="Succeeded"/> is true.
    /// </summary>
    public long SnapshotDestructiveVersion { get; init; }

    /// <summary>
    /// Length of the history snapshot at the moment compaction started (#532).
    /// Used together with <see cref="SnapshotDestructiveVersion"/> to detect
    /// concurrent appends and rebase the result. Always populated when
    /// <see cref="Succeeded"/> is true.
    /// </summary>
    public int SnapshotHistoryCount { get; init; }

    /// <summary>
    /// Number of entries that were folded into the new summary and marked
    /// <c>IsHistory = true</c>. They remain present in <see cref="CompactedHistory"/> for
    /// transcript fidelity but are excluded from the LLM context projection.
    /// </summary>
    public int EntriesSummarized { get; init; }

    /// <summary>Number of entries preserved verbatim in the LLM-visible tail.</summary>
    public int EntriesPreserved { get; init; }

    /// <summary>Approximate LLM-visible token count before compaction.</summary>
    public int TokensBefore { get; init; }

    /// <summary>Approximate LLM-visible token count after compaction.</summary>
    public int TokensAfter { get; init; }
}
