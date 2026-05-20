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
    /// The new session history to apply when <see cref="Succeeded"/> is true.
    /// Null when <see cref="Succeeded"/> is false.
    /// </summary>
    public IReadOnlyList<SessionEntry>? CompactedHistory { get; init; }

    /// <summary>Number of entries that were summarized (removed).</summary>
    public int EntriesSummarized { get; init; }

    /// <summary>Number of entries preserved verbatim.</summary>
    public int EntriesPreserved { get; init; }

    /// <summary>Approximate token count before compaction.</summary>
    public int TokensBefore { get; init; }

    /// <summary>Approximate token count after compaction.</summary>
    public int TokensAfter { get; init; }
}
