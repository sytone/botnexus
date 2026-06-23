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

    /// <summary>
    /// Builds a "skipped / aborted" result (history unchanged): <see cref="Succeeded"/> = false,
    /// empty summary, no compacted history. Centralises the five previously-duplicated
    /// <c>Succeeded = false</c> literal blocks in <c>LlmSessionCompactor.CompactAsync</c> (breaker-open,
    /// empty-history, no-candidates, timeout, empty-summary) so a snapshot-stamping bug can only be
    /// written once (the #532 / #366 / #965 drift class).
    /// </summary>
    /// <param name="snapshotDestructiveVersion">Destructive version observed at snapshot time (0 when no snapshot was taken).</param>
    /// <param name="snapshotHistoryCount">History length at snapshot time (0 when no snapshot was taken).</param>
    /// <param name="entriesPreserved">Entries that would have been preserved verbatim.</param>
    /// <param name="tokensBefore">LLM-visible token count before the (skipped) compaction.</param>
    /// <param name="tokensAfter">LLM-visible token count after (equal to <paramref name="tokensBefore"/> since history is unchanged).</param>
    public static CompactionResult Skipped(
        long snapshotDestructiveVersion = 0,
        int snapshotHistoryCount = 0,
        int entriesPreserved = 0,
        int tokensBefore = 0,
        int tokensAfter = 0) => new()
        {
            Summary = string.Empty,
            Succeeded = false,
            EntriesSummarized = 0,
            EntriesPreserved = entriesPreserved,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SnapshotDestructiveVersion = snapshotDestructiveVersion,
            SnapshotHistoryCount = snapshotHistoryCount,
        };

    /// <summary>
    /// Builds a successful compaction result: <see cref="Succeeded"/> = true with the rebuilt
    /// <paramref name="compactedHistory"/> the caller applies via <c>ReplaceHistory</c>. Pairs with
    /// <see cref="Skipped"/> to replace the hand-rolled result literals in the compactor.
    /// </summary>
    public static CompactionResult ForSuccess(
        string summary,
        IReadOnlyList<SessionEntry> compactedHistory,
        int entriesSummarized,
        int entriesPreserved,
        int tokensBefore,
        int tokensAfter,
        long snapshotDestructiveVersion,
        int snapshotHistoryCount) => new()
        {
            Summary = summary,
            Succeeded = true,
            CompactedHistory = compactedHistory,
            EntriesSummarized = entriesSummarized,
            EntriesPreserved = entriesPreserved,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SnapshotDestructiveVersion = snapshotDestructiveVersion,
            SnapshotHistoryCount = snapshotHistoryCount,
        };
}
