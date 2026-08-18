namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// One row of the Activity page's conversation cost subsection (#2898): a projected dashboard row
/// plus the accumulated-spend signals derived server-side for that same conversation.
/// </summary>
/// <remarks>
/// <para>
/// The row <b>composes</b> an <see cref="ActivityRow"/> rather than re-deriving a title, an origin
/// badge or an agent list of its own. That is a structural guarantee, not a convention: there is no
/// second copy of the classification to drift (#2385's one-classifier rule). Anything the cost
/// table renders about identity - label, badge, navigation target - is read off
/// <see cref="Row"/> through the very same helpers the main activity table calls.
/// </para>
/// <para>
/// The count fields that the platform cannot presently measure are nullable and
/// <see langword="null"/> means <em>not measured</em>, never zero (#2554). A conversation whose
/// tokens were never recorded must not rank as a cheap one.
/// </para>
/// </remarks>
/// <param name="Row">The projected activity row this cost belongs to - the single source of identity.</param>
/// <param name="SessionCount">How many sessions the conversation spans. Always measured.</param>
/// <param name="MessageCount">How many transcript entries accumulated across those sessions. Always measured.</param>
/// <param name="CompactionSummaryCount">
/// How many compaction summaries the conversation carries - the context-pressure signal - or
/// <see langword="null"/> when the server did not measure it.
/// </param>
/// <param name="TotalTokens">
/// Total provider tokens attributed to the conversation, or <see langword="null"/> when no
/// provider-usage measurement exists. This is the ranking key, and a null one sorts LAST rather
/// than as a zero.
/// </param>
public sealed record ActivityCostRow(
    ActivityRow Row,
    int SessionCount,
    int MessageCount,
    int? CompactionSummaryCount = null,
    long? TotalTokens = null)
{
    /// <summary>The conversation this row addresses. Delegated, so it cannot disagree with <see cref="Row"/>.</summary>
    public string ConversationId => Row.ConversationId;

    /// <summary>The owning agent, used for navigation. Delegated for the same reason.</summary>
    public string OwningAgentId => Row.OwningAgentId;
}

/// <summary>
/// Pure projection for the Activity page's conversation cost subsection (#2898). Kept static and
/// dependency-free, mirroring <see cref="ActivityDashboardProjection"/>, so it is unit-testable
/// without bUnit and can be shared by any surface that needs the same ranking.
/// </summary>
public static class ActivityCostProjection
{
    /// <summary>
    /// Rendered text for a count the server did not measure. Deliberately a <em>word</em> and not a
    /// dash or a zero: the whole point of the nullable fields is that "we did not look" reads
    /// differently from "we looked and it was none".
    /// </summary>
    public const string NotMeasured = "not measured";

    /// <summary>
    /// Ranks the conversations matching <paramref name="filter"/> by accumulated cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtering is delegated wholesale to <see cref="ActivityDashboardProjection.Project"/>, so the
    /// subsection inherits the dashboard's agent / origin / cron / recency facets by construction
    /// (#2898 AC4) - there is no second predicate to keep in step, and a facet added to the
    /// dashboard applies here with no change at all.
    /// </para>
    /// <para>
    /// Ordering is by <see cref="ActivityCostRow.TotalTokens"/> descending, and a
    /// <see langword="null"/> total sorts <b>last</b> rather than as a zero: an unmeasured
    /// conversation is of unknown cost, and ranking it alongside genuinely-cheap ones would assert
    /// something the data does not support. Message count then session count break the tie -
    /// including the case, universal today, where no total is measured at all - and the
    /// conversation id makes the order total so equal rows never reshuffle between reads.
    /// </para>
    /// </remarks>
    /// <param name="conversations">Raw conversation summaries, as fed to the main activity table.</param>
    /// <param name="costs">Cost rollups keyed by conversation id, as returned by the gateway.</param>
    /// <param name="filter">The dashboard filter to inherit.</param>
    /// <param name="now">Reference "now" for the recency window; injected so the projection is deterministic.</param>
    public static IReadOnlyList<ActivityCostRow> Project(
        IEnumerable<ConversationSummaryDto> conversations,
        IEnumerable<ConversationCostDto> costs,
        ActivityDashboardFilter filter,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(filter);

        // First stamped cost wins, matching InvolvedAgents' de-duplication rule: a server that
        // repeated a conversation id must still yield exactly one row.
        var byConversation = new Dictionary<string, ConversationCostDto>(StringComparer.Ordinal);
        foreach (var cost in costs)
        {
            if (!byConversation.ContainsKey(cost.ConversationId))
                byConversation[cost.ConversationId] = cost;
        }

        return ActivityDashboardProjection.Project(conversations, filter, now)
            .Select(row => byConversation.TryGetValue(row.ConversationId, out var cost)
                ? new ActivityCostRow(row, cost.SessionCount, cost.MessageCount, cost.CompactionSummaryCount, cost.TotalTokens)
                // A conversation the rollup does not mention was not measured at all, so every
                // nullable field stays null. The session/message counts are 0 because the absence
                // of any session row IS the measurement.
                : new ActivityCostRow(row, 0, 0, null, null))
            .OrderByDescending(r => r.TotalTokens.HasValue)
            .ThenByDescending(r => r.TotalTokens ?? 0)
            .ThenByDescending(r => r.MessageCount)
            .ThenByDescending(r => r.SessionCount)
            .ThenBy(r => r.ConversationId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Renders a possibly-unmeasured count for display, keeping the null/zero distinction visible at
    /// the render layer as well as in the model (#2898 AC3).
    /// </summary>
    /// <remarks>
    /// A measured zero renders as <c>0</c>; an unmeasured value renders as
    /// <see cref="NotMeasured"/>. Formatting the null as <c>0</c> here would defeat the nullable
    /// model entirely - the defect this method exists to make impossible.
    /// </remarks>
    /// <param name="value">The count, or <see langword="null"/> when the server did not measure it.</param>
    public static string FormatCount(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? NotMeasured;

    /// <summary>
    /// The navigation target for a cost row, keyed on the row's OWN conversation id rather than its
    /// display position (#2898 AC5), so a re-sort can never send a reader to a different
    /// conversation than the one they clicked.
    /// </summary>
    /// <param name="row">A projected cost row.</param>
    public static string NavigationTarget(ActivityCostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return $"/chat/{Uri.EscapeDataString(row.OwningAgentId)}/{Uri.EscapeDataString(row.ConversationId)}";
    }
}
