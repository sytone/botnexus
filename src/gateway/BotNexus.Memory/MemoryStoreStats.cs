namespace BotNexus.Memory;

/// <summary>
/// Store-level health and capacity facts for one agent's memory database.
/// </summary>
/// <remarks>
/// <paramref name="EmbeddedEntryCount"/> and <paramref name="VectorScanCeiling"/> exist so an
/// operator can see the #3244 condition directly: when the embedded row count exceeds the
/// ceiling, vector recall is silently bounded to a recency window and the oldest rows are only
/// reachable lexically. The two numbers are useless apart — a count means nothing without the
/// bound it is being compared against — so they are reported together.
/// </remarks>
/// <param name="EntryCount">Total rows in the store.</param>
/// <param name="DatabaseSizeBytes">On-disk size of the SQLite file.</param>
/// <param name="LastIndexedAt">Timestamp of the newest row, or <see langword="null"/> when empty.</param>
/// <param name="EmbeddedEntryCount">Live (non-archived) rows carrying an embedding vector.</param>
/// <param name="VectorScanCeiling">
/// Configured per-search vector scan ceiling, or <see langword="null"/> when no ceiling applies.
/// </param>
public sealed record MemoryStoreStats(
    int EntryCount,
    long DatabaseSizeBytes,
    DateTimeOffset? LastIndexedAt,
    int EmbeddedEntryCount = 0,
    int? VectorScanCeiling = null)
{
    /// <summary>
    /// True when the store holds more embedded rows than a single search may scan, so vector
    /// recall is bounded to a recency window rather than covering the corpus.
    /// </summary>
    public bool ExceedsVectorScanCeiling =>
        VectorScanCeiling is { } ceiling && ceiling > 0 && EmbeddedEntryCount > ceiling;
}
