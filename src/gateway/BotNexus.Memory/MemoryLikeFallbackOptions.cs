namespace BotNexus.Memory;

/// <summary>
/// Bounds for the <see cref="SqliteMemoryStore"/> LIKE-based search fallback.
/// </summary>
/// <remarks>
/// The LIKE fallback is reached only when the FTS primary path errors out (query
/// syntax / index corruption) or the database is transiently busy. Its
/// <c>content LIKE '%term%'</c> predicate uses a leading wildcard and therefore cannot
/// use an index — left unbounded it would full-scan the entire <c>memories</c> table on
/// a path that is, by definition, hit when the store is already degraded. These options
/// keep that degraded-mode cost finite by constraining the scan to a recency window and
/// a hard row ceiling. The fallback is consequently best-effort and non-exhaustive; the
/// FTS primary path is unaffected.
/// </remarks>
public sealed record MemoryLikeFallbackOptions
{
    /// <summary>
    /// Default fallback bounds: a one-year recency window and a 5,000-row scan ceiling.
    /// Generous enough that the common degraded-mode search still returns useful results,
    /// tight enough that a multi-million-row store cannot trigger an unbounded scan.
    /// </summary>
    public static MemoryLikeFallbackOptions Default { get; } = new();

    /// <summary>
    /// Only memories created within this many days are considered by the fallback scan.
    /// <see langword="null"/> or a non-positive value disables the recency window
    /// (full history is eligible). Defaults to 365 days.
    /// </summary>
    public double? RecencyWindowDays { get; init; } = 365d;

    /// <summary>
    /// Hard upper bound on the number of candidate rows the fallback scan will read before
    /// ranking. <see langword="null"/> or a non-positive value disables the ceiling.
    /// Defaults to 5,000 rows.
    /// </summary>
    public int? MaxScanRows { get; init; } = 5000;
}
