using BotNexus.Memory.Models;

namespace BotNexus.Memory.Embeddings;

/// <summary>
/// Outcome of the bounded brute-force vector scan for a single search.
/// </summary>
/// <remarks>
/// Three states rather than a boolean, because "the scan did not run at all" and "the scan ran
/// and saw everything" are different facts and a caller that conflates them draws the wrong
/// conclusion from an empty result set (#3244).
/// </remarks>
public enum MemoryVectorScanStatus
{
    /// <summary>
    /// No vector scan was performed: embeddings are disabled, no model is active, or the query
    /// embedding could not be generated. The result is lexical-only and paid no scan cost.
    /// </summary>
    NotAttempted,

    /// <summary>
    /// The scan examined every eligible embedded row for this query, so a row that did not
    /// surface genuinely did not match rather than merely not being looked at.
    /// </summary>
    Complete,

    /// <summary>
    /// The scan returned exactly as many rows as the configured ceiling allows, so rows older
    /// than the recency window it covered may exist and were never scored. The result is the
    /// best match <em>within an arbitrary recency window</em>, not within the corpus.
    /// </summary>
    PossiblyTruncated
}

/// <summary>
/// Structured, per-search account of what the vector leg of hybrid retrieval actually examined.
/// </summary>
/// <remarks>
/// <para>
/// The scan ceiling (<see cref="MemoryVectorSearchOptions.MaxScanRows"/>) is deliberate cost
/// control and is not going away — there is no ANN index, so an unbounded cosine pass over a
/// growing corpus is not acceptable. The defect this record closes is that crossing the ceiling
/// used to be <em>undetectable</em>: a truncated scan and a complete scan produced
/// indistinguishable results, so silent recall loss on the longest-lived agents looked exactly
/// like "nothing older matched" (#3244).
/// </para>
/// <para>
/// This is a report, not a log line, precisely so a caller — the memory tab, an operator, or an
/// agent reasoning about its own recall — can branch on it.
/// </para>
/// </remarks>
/// <param name="Status">Whether the scan ran, and whether it saw everything.</param>
/// <param name="RowsScanned">Embedded rows examined by the recency-ordered pass.</param>
/// <param name="ScanCeiling">
/// The configured ceiling in force for this search, or <see langword="null"/> when no ceiling
/// applies (the scan is then exhaustive by construction).
/// </param>
/// <param name="LexicalUnionRowsScanned">
/// Additional rows scored only because they were in the lexical candidate set for the same query
/// and fell outside the recency window. This is the escape hatch that stops an old but
/// lexically-plausible row from being permanently unscorable.
/// </param>
public sealed record MemoryVectorScanReport(
    MemoryVectorScanStatus Status,
    int RowsScanned,
    int? ScanCeiling,
    int LexicalUnionRowsScanned)
{
    /// <summary>The canonical "no vector work was done" report.</summary>
    public static MemoryVectorScanReport NotAttempted { get; } =
        new(MemoryVectorScanStatus.NotAttempted, RowsScanned: 0, ScanCeiling: null, LexicalUnionRowsScanned: 0);

    /// <summary>
    /// True when older embedded rows may exist that were never scored. Callers should render this
    /// as "older memories were not searched semantically" rather than treating the result as complete.
    /// </summary>
    public bool IsPossiblyTruncated => Status == MemoryVectorScanStatus.PossiblyTruncated;

    /// <summary>Human- and agent-readable explanation of what the vector leg covered.</summary>
    public string Explain() => Status switch
    {
        MemoryVectorScanStatus.NotAttempted =>
            "Vector search did not run (embeddings unavailable); results are lexical-only.",
        MemoryVectorScanStatus.PossiblyTruncated =>
            $"Vector scan hit its ceiling of {ScanCeiling} row(s) ordered newest-first, so older embedded "
            + $"memories may exist that were never scored ({LexicalUnionRowsScanned} older row(s) were still "
            + "scored because they matched lexically).",
        _ =>
            $"Vector scan examined all {RowsScanned} eligible embedded row(s); nothing was excluded by the ceiling."
    };
}

/// <summary>
/// A ranked search result paired with the vector-scan report that produced it.
/// </summary>
/// <param name="Entries">The ranked rows, highest fused relevance first.</param>
/// <param name="VectorScan">What the vector leg of retrieval actually examined.</param>
public sealed record MemorySearchResult(
    IReadOnlyList<ScoredMemoryEntry> Entries,
    MemoryVectorScanReport VectorScan);
