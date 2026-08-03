namespace BotNexus.Memory;

/// <summary>
/// Per-term hit count for a single sanitized query term.
/// </summary>
/// <param name="Term">The sanitized term as it was submitted to FTS.</param>
/// <param name="RowCount">Number of live (non-archived) rows matching that term alone.</param>
public sealed record MemoryTermHit(string Term, int RowCount);

/// <summary>
/// Diagnostic detail for a search, so an empty result set is never silently ambiguous
/// (issue #2740). Before this existed, "nothing was ever stored" and "the query could not
/// match by construction" were indistinguishable to the caller, and agents reasonably
/// concluded the memory did not exist and re-derived it.
/// </summary>
/// <param name="Query">The original query text.</param>
/// <param name="MatchExpression">The MATCH expression actually submitted to FTS.</param>
/// <param name="LiveRowCount">Total live (non-archived) rows in the store.</param>
/// <param name="TermHits">Per-term hit counts.</param>
/// <param name="ConjunctionRowCount">Rows matching every term (the old implicit-AND form).</param>
/// <param name="MatchedRowCount">Rows matching the expression actually used.</param>
public sealed record MemorySearchDiagnostics(
    string Query,
    string MatchExpression,
    int LiveRowCount,
    IReadOnlyList<MemoryTermHit> TermHits,
    int ConjunctionRowCount,
    int MatchedRowCount)
{
    /// <summary>The store holds no live rows at all, so no query could have matched.</summary>
    public bool CorpusIsEmpty => LiveRowCount == 0;

    /// <summary>
    /// The corpus is non-empty and terms hit individually, but no row carries all of them -
    /// the failure mode that made 85% of searches return nothing.
    /// </summary>
    public bool ConjunctionImpossible =>
        !CorpusIsEmpty && ConjunctionRowCount == 0 && TermHits.Any(hit => hit.RowCount > 0);

    /// <summary>Renders a human- and agent-readable explanation of the result.</summary>
    public string Explain()
    {
        if (CorpusIsEmpty)
            return $"No results for '{Query}': the store is empty (0 live rows), so nothing could match.";

        var terms = string.Join(", ", TermHits.Select(hit => $"{hit.Term}={hit.RowCount}"));

        if (ConjunctionImpossible)
        {
            return $"No row contains every term of '{Query}' (conjunction of all terms matches 0 rows of "
                 + $"{LiveRowCount} live rows); per-term hits: {terms}. The query matched "
                 + $"{MatchedRowCount} row(s) using '{MatchExpression}'.";
        }

        return $"Query '{Query}' matched {MatchedRowCount} row(s) of {LiveRowCount} live rows using "
             + $"'{MatchExpression}'; per-term hits: {terms}.";
    }
}
