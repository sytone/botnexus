namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Applies the <c>ListSummariesAsync</c> paging window (issue #2411) to an in-memory
/// projection, for stores that cannot push <c>LIMIT</c>/<c>OFFSET</c> into a query engine.
/// </summary>
/// <remarks>
/// This exists so every <see cref="ISessionStore"/> implementation exposes the <b>same</b>
/// observable page contract - newest-first ordering, offset applied before limit - even
/// though only the SQLite store can make it cheap. Without a single shared helper the
/// interface default, the base-class default and the SQLite override would each be free to
/// drift on ordering, which is exactly the class of inconsistency that makes a paged API
/// unusable (a client walking offsets would silently skip or repeat rows).
/// </remarks>
public static class SessionSummaryWindow
{
    /// <summary>
    /// Orders <paramref name="summaries"/> newest-first and returns the requested page.
    /// </summary>
    /// <param name="summaries">The matching summaries, in any order.</param>
    /// <param name="limit">
    /// Maximum rows to return, or <c>null</c> for the explicit unbounded opt-in used by
    /// background callers (warmup, cron) that genuinely need every row.
    /// </param>
    /// <param name="offset">Rows to skip. Negative values are treated as zero.</param>
    public static IReadOnlyList<SessionSummary> Apply(
        IEnumerable<SessionSummary> summaries,
        int? limit,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        // Ordering must be total and deterministic: UpdatedAt alone ties on rows written in
        // the same tick, and a non-deterministic tiebreak would let a paging client skip or
        // repeat rows between requests. SessionId is unique, so it closes the tie.
        IEnumerable<SessionSummary> ordered = summaries
            .OrderByDescending(summary => summary.UpdatedAt)
            .ThenBy(summary => summary.SessionId, StringComparer.Ordinal);

        if (offset > 0)
            ordered = ordered.Skip(offset);

        if (limit is { } bound)
            ordered = ordered.Take(bound);

        return ordered.ToList();
    }

    /// <summary>
    /// Filters <paramref name="summaries"/> by <paramref name="query"/>, then applies the query's
    /// window, returning the page together with the total size of the filtered set (#2532).
    /// </summary>
    /// <remarks>
    /// The order here is the whole point of issue #2532: filter FIRST, window SECOND, so
    /// <c>Offset</c> addresses the filtered set rather than the raw store. This is the single
    /// implementation every non-SQL store shares, so the interface default, the base-class default
    /// and any test double cannot disagree about that ordering.
    /// </remarks>
    public static SessionSummaryPage ApplyQuery(
        IEnumerable<SessionSummary> summaries,
        SessionSummaryQuery query)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(query);

        var matching = summaries.Where(query.Matches).ToList();
        var offset = Math.Max(query.Offset, 0);
        var items = Apply(matching, query.Limit, offset);
        return new SessionSummaryPage(
            items,
            matching.Count,
            offset + items.Count < matching.Count);
    }
}
