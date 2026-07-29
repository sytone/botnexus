using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// The complete predicate for a paged session-summary read: which sessions match, and which
/// slice of the matching set to return.
/// </summary>
/// <remarks>
/// Issue #2532. Before this type existed the REST list endpoint paged the <b>store</b> and then
/// filtered the returned page by agent and status in the controller. That put
/// <c>limit</c>/<c>offset</c> in a different coordinate space from the rows the client actually
/// received: a client advancing its offset by the number of rows it got would creep forward one
/// row at a time and only terminate by walking the entire global session table. Carrying the
/// filter and the window together in one value makes that mismatch unrepresentable - every store
/// applies the predicate first and the window second, so <c>offset</c> always addresses the
/// filtered set.
/// </remarks>
/// <param name="UpdatedAfter">
/// Lower bound (inclusive) on session <c>UpdatedAt</c>. <see cref="DateTimeOffset.MinValue"/>
/// means "no time window" and is what the REST list endpoint passes.
/// </param>
/// <param name="AgentId">When set, only sessions owned by this agent match.</param>
/// <param name="ConversationId">When set, only sessions linked to this conversation match.</param>
/// <param name="IncludeInactive">
/// When <c>false</c> (the default) only <see cref="SessionStatus.Active"/> and
/// <see cref="SessionStatus.Suspended"/> sessions match, mirroring the portal's default view.
/// </param>
/// <param name="Limit">
/// Maximum rows in the returned page, or <c>null</c> for the explicit unbounded opt-in reserved
/// for background callers. Request-scoped callers must always pass a bound.
/// </param>
/// <param name="Offset">Rows to skip <b>within the filtered set</b>. Negative values are treated as zero.</param>
public sealed record SessionSummaryQuery(
    DateTimeOffset UpdatedAfter,
    string? AgentId = null,
    string? ConversationId = null,
    bool IncludeInactive = false,
    int? Limit = null,
    int Offset = 0)
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="summary"/> satisfies every filter clause. The
    /// window (<see cref="Limit"/>/<see cref="Offset"/>) is deliberately NOT applied here - it is
    /// applied after filtering by <see cref="SessionSummaryWindow"/>.
    /// </summary>
    public bool Matches(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.UpdatedAt < UpdatedAfter)
            return false;

        if (AgentId is not null && !string.Equals(summary.AgentId, AgentId, StringComparison.Ordinal))
            return false;

        if (ConversationId is not null && !string.Equals(summary.ConversationId, ConversationId, StringComparison.Ordinal))
            return false;

        if (!IncludeInactive && summary.Status is not (SessionStatus.Active or SessionStatus.Suspended))
            return false;

        return true;
    }
}

/// <summary>
/// One page of session summaries plus the size of the set it was drawn from.
/// </summary>
/// <remarks>
/// Issue #2532 / AC5. Without <see cref="TotalCount"/> or <see cref="HasMore"/> a paging client
/// has no way to know it is done except by requesting a page and observing that it came back
/// empty - and it cannot use "shorter than requested" as the signal either, because the server
/// clamps <c>limit</c> to its own maximum (#2499), so a short page is indistinguishable from a
/// clamped one. Making exhaustion an explicit part of the response removes the guesswork and the
/// trailing probe request.
/// </remarks>
/// <param name="Items">The requested slice of the filtered set, newest first.</param>
/// <param name="TotalCount">Total number of sessions matching the filter, ignoring the window.</param>
/// <param name="HasMore">
/// <c>true</c> when rows matching the filter exist beyond this page. Authoritative: a client must
/// terminate on <c>false</c> and must not infer exhaustion from the page length.
/// </param>
public sealed record SessionSummaryPage(
    IReadOnlyList<SessionSummary> Items,
    int TotalCount,
    bool HasMore)
{
    /// <summary>An empty page of an empty set.</summary>
    public static SessionSummaryPage Empty { get; } = new([], 0, false);
}
