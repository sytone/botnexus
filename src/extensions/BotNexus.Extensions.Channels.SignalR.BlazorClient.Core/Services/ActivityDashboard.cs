namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Recency window applied to the Home / Activity dashboard's last-activity signal. Composable with
/// the other <see cref="ActivityDashboardFilter"/> facets so the filter bar can be extended without
/// reworking the projection.
/// </summary>
public enum ActivityRecencyWindow
{
    /// <summary>No recency constraint - every conversation matches.</summary>
    Any,

    /// <summary>Only conversations whose last activity falls on the local calendar day.</summary>
    Today,

    /// <summary>Only conversations updated within a rolling 7-day window.</summary>
    Week,

    /// <summary>Only conversations updated within a rolling 30-day window.</summary>
    Month
}

/// <summary>
/// Status facet for the dashboard filter. Kept separate from the raw string status so the UI can
/// present a small fixed set of choices while the projection matches case-insensitively against the
/// server's status string.
/// </summary>
public enum ActivityStatusFilter
{
    /// <summary>Only active conversations (the default landing view).</summary>
    Active,

    /// <summary>Only archived conversations.</summary>
    Archived,

    /// <summary>Both active and archived conversations.</summary>
    All
}

/// <summary>
/// Origin facet for the dashboard filter (#2385). Selects rows by <em>why the conversation
/// exists</em>, keyed off the same <c>(Source, Kind)</c> classification the row badge renders, so
/// the filter can never disagree with what the reader sees on screen.
/// </summary>
/// <remarks>
/// Deliberately its own enum rather than a reuse of <see cref="ConversationSource"/>: the
/// user-visible origins are not one-to-one with the wire source. <c>Source=Agent</c> fans out into
/// three distinct badges (sub-agent, agent-to-agent, agent-initiated) via
/// <see cref="ConversationKind"/>, and the unbadged human/channel case is a single choice.
/// Filtering on the raw source would offer facets that match no badge on screen.
/// </remarks>
public enum ActivityOriginFilter
{
    /// <summary>
    /// No origin constraint - every row matches. The default, so the facet is inert until the user
    /// opts in and the existing landing view is unchanged.
    /// </summary>
    All,
    /// <summary>Only ordinary human-on-a-channel conversations - the deliberately unbadged rows.</summary>
    Human,
    /// <summary>Only cron/scheduled runs. Composes with, and does not override, the cron toggle.</summary>
    Scheduled,
    /// <summary>Only conversations triggered by an inbound webhook.</summary>
    Webhook,
    /// <summary>Only agent-minted conversations with no more specific pairing.</summary>
    Agent,
    /// <summary>Only agent-supervising-sub-agent conversations.</summary>
    SubAgent,
    /// <summary>Only peer agent-to-agent exchanges.</summary>
    AgentToAgent
}

/// <summary>
/// Pin facet for the dashboard filter (#2619). Tri-state rather than a one-way "pinned only"
/// toggle so the complement ("what have I not marked as mattering?") stays reachable, and so the
/// facet is inert by default like every other facet.
/// </summary>
public enum ActivityPinFilter
{
    /// <summary>No pin constraint - every row matches. The default, so the landing view is unchanged.</summary>
    All,

    /// <summary>Only conversations the user has explicitly pinned.</summary>
    Pinned,

    /// <summary>Only conversations the user has not pinned.</summary>
    Unpinned
}

/// <summary>
/// Immutable, composable filter for the Home / Activity dashboard. Each facet is independent so new
/// facets can be added without changing existing call sites, and the whole record is cheap to copy
/// with <c>with</c> when a single facet changes from the filter bar.
/// </summary>
/// <param name="IncludeCron">
/// When <see langword="false"/> (the default) cron/scheduled conversations are hidden - the
/// same default-exclude the sidebar and cron-noop-retention work (#1754/#1869) apply. Toggling
/// this on surfaces them.
/// </param>
/// <param name="AgentId">
/// When set, only conversations that <em>involve</em> this agent (owner or participant) are shown.
/// <see langword="null"/> means "all agents".
/// </param>
/// <param name="Status">Which lifecycle statuses to include.</param>
/// <param name="Recency">Recency window applied to the last-activity timestamp.</param>
/// <param name="Origin">
/// Which origination case to include (#2385). Defaults to <see cref="ActivityOriginFilter.All"/>,
/// so the facet is inert unless the user selects one. It <em>composes</em> with the other facets -
/// notably it does not override <paramref name="IncludeCron"/>, so selecting
/// <see cref="ActivityOriginFilter.Scheduled"/> still shows nothing until cron is revealed.
/// </param>
/// <param name="Pinned">
/// Which pin state to include (#2619). Defaults to <see cref="ActivityPinFilter.All"/>, so the
/// facet is inert unless the user selects one. Like <paramref name="Origin"/> it <em>composes</em>
/// with the other facets and overrides none of them - notably a pinned cron conversation stays
/// hidden until <paramref name="IncludeCron"/> reveals it, because pinning is a priority signal,
/// not a visibility override.
/// </param>
public sealed record ActivityDashboardFilter(
    bool IncludeCron = false,
    string? AgentId = null,
    ActivityStatusFilter Status = ActivityStatusFilter.Active,
    ActivityRecencyWindow Recency = ActivityRecencyWindow.Any,
    ActivityOriginFilter Origin = ActivityOriginFilter.All,
    ActivityPinFilter Pinned = ActivityPinFilter.All);

/// <summary>
/// A single projected row on the Home / Activity dashboard: one active conversation plus the derived
/// signals the row renders (involved agents, last-activity, status, cron flag).
/// </summary>
/// <param name="ConversationId">The routable conversation identifier.</param>
/// <param name="OwningAgentId">The agent that owns the conversation - used for row navigation.</param>
/// <param name="Title">Display title / name for the conversation.</param>
/// <param name="Status">Lifecycle status string (e.g. <c>Active</c>).</param>
/// <param name="LastActivity">When the conversation was last updated - the primary recency signal.</param>
/// <param name="InvolvedAgents">
/// All agents involved in the conversation, derived from the participant roster unioned with the
/// owning agent, so multi-agent / sub-agent / agent-to-agent conversations render every participant
/// rather than just the owner.
/// </param>
/// <param name="ChannelCount">Number of channel bindings - a secondary recency/reach signal.</param>
/// <param name="Source">
/// The server-stamped origination trigger (epic #2300) - <em>why</em> this conversation exists.
/// Carried as the typed enum rather than collapsed to a single bool so the row can answer more than
/// "is it cron": a webhook run, an agent-minted conversation and a human DM are distinguishable.
/// </param>
/// <param name="Kind">
/// The server-stamped citizen pairing (epic #2300) - <em>who</em> is talking to whom. Orthogonal to
/// <paramref name="Source"/>; together they disambiguate every origination case, which is what the
/// row badge renders.
/// </param>
/// <param name="IsPinned">
/// Whether the user has explicitly pinned this conversation (#2619). Carried straight through from
/// the server-stamped <see cref="ConversationSummaryDto.IsPinned"/> rather than inferred, so the
/// dashboard and the sidebar cannot disagree about what is pinned. This is the only
/// <em>user-authored</em> priority signal on the row - every other signal is machine-derived.
/// </param>
/// <param name="PinnedAt">
/// When the pin was stamped, or <see langword="null"/> when the conversation is not pinned. Carried
/// so a later surface can explain or order pins by age without a second round trip.
/// </param>
public sealed record ActivityRow(
    string ConversationId,
    string OwningAgentId,
    string Title,
    string Status,
    DateTimeOffset LastActivity,
    IReadOnlyList<string> InvolvedAgents,
    int ChannelCount,
    ConversationSource Source,
    ConversationKind Kind,
    bool IsPinned = false,
    DateTimeOffset? PinnedAt = null)
{
    /// <summary>
    /// Whether this is a cron/scheduled conversation. Computed from <see cref="Source"/> rather than
    /// stored, so widening the row to the typed origin cannot let the flag and the enum disagree -
    /// the exact drift class epic #2300 exists to remove. Existing call sites (the scheduled stat
    /// card, the cron row class) keep working unchanged.
    /// </summary>
    public bool IsCron => Source == ConversationSource.Cron;
}

/// <summary>
/// At-a-glance summary of the currently-projected dashboard rows: how much work is live, how many
/// distinct agents are involved, how many rows are scheduled (cron), and how fresh the freshest
/// activity is. Derived from the already-filtered <see cref="ActivityRow"/> set so the strip always
/// reflects exactly what the table shows under the active filters. Kept as an immutable record so it
/// is trivially unit-testable and cheap to hand to the component.
/// </summary>
/// <param name="ConversationCount">Number of conversations (rows) currently shown.</param>
/// <param name="AgentCount">Number of distinct agents involved across the shown conversations.</param>
/// <param name="ScheduledCount">How many of the shown conversations are cron/scheduled.</param>
/// <param name="LatestActivity">
/// The freshest last-activity timestamp across the shown rows, or <see langword="null"/> when there
/// are no rows. Lets the UI answer "how recently did anything happen?" without scanning the table.
/// </param>
public sealed record ActivitySummary(
    int ConversationCount,
    int AgentCount,
    int ScheduledCount,
    DateTimeOffset? LatestActivity);

/// <summary>
/// Pure projection for the Home / Activity dashboard. Kept as a static, dependency-free helper so it
/// is unit-testable without bUnit and shared by any future surface (mobile, admin) that needs the
/// same active-conversation activity view. Mirrors the "pure ordering/filter helper" convention
/// established by <see cref="PortalListOrdering"/>.
/// </summary>
public static class ActivityDashboardProjection
{
    /// <summary>
    /// Determines whether a conversation summary is a cron/scheduled conversation from the
    /// authoritative, server-stamped <see cref="ConversationSummaryDto.Source"/> (#2305, epic
    /// #2300). This replaced the previous <c>cron:</c>-prefixed active-session-id probe: origin is
    /// a modelled field, never inferred from an id substring. Parsing is tolerant, so an unknown
    /// source from a newer server degrades to <see cref="ConversationSource.Channel"/> (not cron)
    /// rather than throwing.
    /// </summary>
    public static bool IsCronConversation(ConversationSummaryDto conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return ConversationOrigin.ParseSource(conversation.Source) == ConversationSource.Cron;
    }

    /// <summary>
    /// Derives the full set of agents involved in a conversation. Unions the owning agent with every
    /// participant whose citizen kind is <c>Agent</c>, so multi-agent, sub-agent, and agent-to-agent
    /// conversations surface all involved agents rather than just the owner. Ordered deterministically
    /// with the owner first, then the remaining agents alphabetically, and de-duplicated.
    /// </summary>
    public static IReadOnlyList<string> InvolvedAgents(ConversationSummaryDto conversation)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(conversation.AgentId) && seen.Add(conversation.AgentId))
            ordered.Add(conversation.AgentId);

        var participantAgents = (conversation.Participants ?? [])
            .Where(p => string.Equals(p.Kind, "Agent", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

        foreach (var agentId in participantAgents)
        {
            if (seen.Add(agentId))
                ordered.Add(agentId);
        }

        return ordered;
    }

    /// <summary>
    /// Projects and filters a set of conversation summaries into ordered dashboard rows. Applies the
    /// cron default-exclude, agent-involvement filter, status filter, and recency window, then orders
    /// by most-recent activity so the top of the dashboard is the freshest work.
    /// </summary>
    /// <param name="conversations">Raw conversation summaries (e.g. from the global conversations list).</param>
    /// <param name="filter">The composable filter to apply.</param>
    /// <param name="now">
    /// The reference "now" for recency windows. Injected rather than read from the clock so the
    /// projection is deterministic and unit-testable.
    /// </param>
    public static IReadOnlyList<ActivityRow> Project(
        IEnumerable<ConversationSummaryDto> conversations,
        ActivityDashboardFilter filter,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(filter);

        return conversations
            .Select(c => new
            {
                Dto = c,
                Source = ConversationOrigin.ParseSource(c.Source),
                Kind = ConversationOrigin.ParseKind(c.Kind),
                IsCron = IsCronConversation(c),
                Agents = InvolvedAgents(c)
            })
            .Where(x => filter.IncludeCron || !x.IsCron)
            .Where(x => MatchesStatus(x.Dto.Status, filter.Status))
            .Where(x => filter.AgentId is null ||
                        x.Agents.Contains(filter.AgentId, StringComparer.Ordinal))
            .Where(x => MatchesRecency(x.Dto.UpdatedAt, filter.Recency, now))
            .Where(x => MatchesOrigin(x.Source, x.Kind, filter.Origin))
            .Where(x => MatchesPinned(x.Dto.IsPinned, filter.Pinned))
            // Pinned-first is a GROUPING key applied ahead of the existing ordering keys, mirroring
            // ConversationsController's pinned-first list ordering rather than inventing a second
            // rule. The UpdatedAt-descending / ConversationId-ordinal contract is untouched and
            // still decides the order *within* each group.
            .OrderByDescending(x => x.Dto.IsPinned)
            .ThenByDescending(x => x.Dto.UpdatedAt)
            .ThenBy(x => x.Dto.ConversationId, StringComparer.Ordinal)
            .Select(x => new ActivityRow(
                x.Dto.ConversationId,
                x.Dto.AgentId,
                string.IsNullOrWhiteSpace(x.Dto.Title) ? "(untitled)" : x.Dto.Title,
                x.Dto.Status,
                x.Dto.UpdatedAt,
                x.Agents,
                x.Dto.BindingCount,
                x.Source,
                x.Kind,
                x.Dto.IsPinned,
                x.Dto.IsPinned ? x.Dto.PinnedAt : null))
            .ToList();
    }

    /// <summary>
    /// Summarizes an already-projected set of dashboard rows into the at-a-glance stat strip. Pure and
    /// dependency-free so it is unit-testable and can be reused by any surface that renders the same
    /// activity view. Counts distinct involved agents across every row (so a multi-agent conversation
    /// contributes each participant once to the fleet-wide agent count).
    /// </summary>
    /// <param name="rows">The rows already produced by <see cref="Project"/>.</param>
    public static ActivitySummary Summarize(IReadOnlyList<ActivityRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
            return new ActivitySummary(0, 0, 0, null);

        var distinctAgents = new HashSet<string>(StringComparer.Ordinal);
        var scheduled = 0;
        DateTimeOffset latest = DateTimeOffset.MinValue;

        foreach (var row in rows)
        {
            foreach (var agentId in row.InvolvedAgents)
                distinctAgents.Add(agentId);

            if (row.IsCron)
                scheduled++;

            if (row.LastActivity > latest)
                latest = row.LastActivity;
        }

        return new ActivitySummary(
            rows.Count,
            distinctAgents.Count,
            scheduled,
            latest == DateTimeOffset.MinValue ? null : latest);
    }

    /// <summary>
    /// Renders the human-readable origin badge for a row: the short answer to "why does this
    /// conversation exist, and between whom?". Returns <see langword="null"/> for the ordinary
    /// human-on-a-channel case so the common row stays unbadged and the badges that <em>are</em>
    /// shown carry signal instead of decorating every line.
    /// </summary>
    /// <remarks>
    /// <see cref="ConversationSource.Agent"/> is deliberately coarse server-side, so this is where
    /// <see cref="ConversationKind"/> earns its keep: peer converse and sub-agent supervision are the
    /// two agent-minted cases a reader most needs to tell apart, and they differ only by kind.
    /// A human/channel conversation that nonetheless carries a non-default kind (an agent pulled into
    /// a human thread) is still badged, because the pairing is the surprising part.
    /// </remarks>
    /// <param name="row">A projected dashboard row.</param>
    /// <returns>The badge text, or <see langword="null"/> when no badge should render.</returns>
    public static string? OriginLabel(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return ClassifyOrigin(row.Source, row.Kind) switch
        {
            ActivityOriginFilter.Scheduled => "Scheduled",
            ActivityOriginFilter.Webhook => "Webhook",
            ActivityOriginFilter.SubAgent => "Sub-agent",
            ActivityOriginFilter.AgentToAgent => "Agent-to-agent",
            ActivityOriginFilter.Agent => "Agent-initiated",
            _ => null
        };
    }

    /// <summary>
    /// CSS modifier suffix for the origin badge, so each origin gets its own colour treatment without
    /// the component string-matching on the display label (which would couple styling to copy).
    /// </summary>
    /// <param name="row">A projected dashboard row.</param>
    /// <returns>A lowercase, hyphen-free modifier token, or <see langword="null"/> when unbadged.</returns>
    public static string? OriginModifier(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return OriginLabel(row) switch
        {
            "Scheduled" => "cron",
            "Webhook" => "webhook",
            "Sub-agent" => "subagent",
            "Agent-to-agent" => "a2a",
            "Agent-initiated" => "agent",
            _ => null
        };
    }

    /// <summary>
    /// Classifies a <c>(source, kind)</c> pair into the user-visible origin facet (#2385). This is
    /// the single classification the badge label, the badge colour modifier and the Origin filter
    /// all read from, so a facet and the badge on the row it selected cannot drift apart - the same
    /// duplicated-rule defect class epic #2300 exists to remove.
    /// </summary>
    /// <remarks>
    /// Never returns <see cref="ActivityOriginFilter.All"/>: <c>All</c> is a filter choice ("do not
    /// constrain"), not a property a row can have.
    /// </remarks>
    /// <param name="source">The parsed origination trigger.</param>
    /// <param name="kind">The parsed citizen pairing.</param>
    /// <returns>The facet this row belongs to.</returns>
    public static ActivityOriginFilter ClassifyOrigin(ConversationSource source, ConversationKind kind) =>
        (source, kind) switch
        {
            (ConversationSource.Cron, _) => ActivityOriginFilter.Scheduled,
            (ConversationSource.Webhook, _) => ActivityOriginFilter.Webhook,
            (ConversationSource.Agent, ConversationKind.AgentSubAgent) => ActivityOriginFilter.SubAgent,
            (ConversationSource.Agent, ConversationKind.AgentAgent) => ActivityOriginFilter.AgentToAgent,
            (ConversationSource.Agent, _) => ActivityOriginFilter.Agent,
            (ConversationSource.Channel, ConversationKind.AgentSubAgent) => ActivityOriginFilter.SubAgent,
            (ConversationSource.Channel, ConversationKind.AgentAgent) => ActivityOriginFilter.AgentToAgent,
            _ => ActivityOriginFilter.Human
        };

    // The All choice short-circuits rather than falling through the classifier, so the default
    // filter costs nothing per row on the common unfiltered landing view.
    private static bool MatchesOrigin(ConversationSource source, ConversationKind kind, ActivityOriginFilter filter) =>
        filter == ActivityOriginFilter.All || ClassifyOrigin(source, kind) == filter;

    // All short-circuits rather than comparing, so the default filter costs nothing per row on the
    // common unfiltered landing view - the same shape as MatchesOrigin.
    private static bool MatchesPinned(bool isPinned, ActivityPinFilter filter) => filter switch
    {
        ActivityPinFilter.Pinned => isPinned,
        ActivityPinFilter.Unpinned => !isPinned,
        _ => true
    };

    private static bool MatchesStatus(string status, ActivityStatusFilter filter) => filter switch
    {
        ActivityStatusFilter.Active => string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase),
        ActivityStatusFilter.Archived => string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private static bool MatchesRecency(DateTimeOffset updatedAt, ActivityRecencyWindow window, DateTimeOffset now) =>
        window switch
        {
            ActivityRecencyWindow.Today => updatedAt.ToLocalTime().Date == now.ToLocalTime().Date,
            ActivityRecencyWindow.Week => updatedAt >= now.AddDays(-7),
            ActivityRecencyWindow.Month => updatedAt >= now.AddDays(-30),
            _ => true
        };
}
