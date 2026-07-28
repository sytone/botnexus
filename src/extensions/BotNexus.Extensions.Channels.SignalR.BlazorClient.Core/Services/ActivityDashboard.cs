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
public sealed record ActivityDashboardFilter(
    bool IncludeCron = false,
    string? AgentId = null,
    ActivityStatusFilter Status = ActivityStatusFilter.Active,
    ActivityRecencyWindow Recency = ActivityRecencyWindow.Any);

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
public sealed record ActivityRow(
    string ConversationId,
    string OwningAgentId,
    string Title,
    string Status,
    DateTimeOffset LastActivity,
    IReadOnlyList<string> InvolvedAgents,
    int ChannelCount,
    ConversationSource Source,
    ConversationKind Kind)
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
            .OrderByDescending(x => x.Dto.UpdatedAt)
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
                x.Kind))
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

        return (row.Source, row.Kind) switch
        {
            (ConversationSource.Cron, _) => "Scheduled",
            (ConversationSource.Webhook, _) => "Webhook",
            (ConversationSource.Agent, ConversationKind.AgentSubAgent) => "Sub-agent",
            (ConversationSource.Agent, ConversationKind.AgentAgent) => "Agent-to-agent",
            (ConversationSource.Agent, _) => "Agent-initiated",
            (ConversationSource.Channel, ConversationKind.AgentSubAgent) => "Sub-agent",
            (ConversationSource.Channel, ConversationKind.AgentAgent) => "Agent-to-agent",
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
