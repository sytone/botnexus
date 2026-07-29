namespace BotNexus.Memory.Embeddings;

/// <summary>
/// Bounds for the brute-force vector scan used by hybrid retrieval.
/// </summary>
/// <remarks>
/// v1 deliberately uses a brute-force agent-scoped scan rather than an ANN index, so the
/// cost of a search is linear in the number of embedded rows for that agent. That is
/// acceptable at current store sizes but must not be unbounded, hence the row ceiling: it
/// caps the worst case while leaving the FTS path — which remains the primary recall
/// mechanism — completely unaffected. Rows are considered newest-first, so the ceiling
/// trades exhaustiveness for recency, matching the temporal-decay bias of the ranker.
/// </remarks>
public sealed record MemoryVectorSearchOptions
{
    /// <summary>Default bounds: scan at most 5,000 embedded rows per search.</summary>
    public static MemoryVectorSearchOptions Default { get; } = new();

    /// <summary>
    /// Hard upper bound on embedded rows examined per search. <see langword="null"/> or a
    /// non-positive value disables the ceiling. Defaults to 5,000.
    /// </summary>
    public int? MaxScanRows { get; init; } = 5000;
}
