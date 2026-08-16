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
/// Liveness facet for the dashboard filter (#1888). Selects rows by whether a session is running
/// in the conversation <em>right now</em>, keyed off the same server-stamped
/// <see cref="ConversationSummaryDto.ActiveSessionId"/> the row badge renders, so the filter can
/// never disagree with what the reader sees on screen.
/// </summary>
/// <remarks>
/// Tri-state rather than a one-way "live only" toggle, matching <see cref="ActivityPinFilter"/>:
/// the complement ("what is parked?") stays reachable and the facet is inert by default.
/// Liveness is the only facet whose value can change without any user action, which is exactly why
/// it belongs on a page that polls - every other facet answers a question about the past.
/// </remarks>
public enum ActivityLiveFilter
{
    /// <summary>No liveness constraint - every row matches. The default, so the landing view is unchanged.</summary>
    All,

    /// <summary>Only conversations with a session running right now.</summary>
    Live,

    /// <summary>Only conversations with no session running.</summary>
    Idle
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
/// <param name="Live">
/// Which liveness state to include (#1888). Defaults to <see cref="ActivityLiveFilter.All"/>, so
/// the facet is inert unless the user selects one. Like the other facets it <em>composes</em> and
/// overrides none of them - notably a live cron run stays hidden until <paramref name="IncludeCron"/>
/// reveals it, because running is a state, not a visibility override.
/// </param>
public sealed record ActivityDashboardFilter(
    bool IncludeCron = false,
    string? AgentId = null,
    ActivityStatusFilter Status = ActivityStatusFilter.Active,
    ActivityRecencyWindow Recency = ActivityRecencyWindow.Any,
    ActivityOriginFilter Origin = ActivityOriginFilter.All,
    ActivityPinFilter Pinned = ActivityPinFilter.All,
    ActivityLiveFilter Live = ActivityLiveFilter.All);

/// <summary>
/// One agent involved in a conversation, together with the role the gateway stamped for it (#2857).
/// Replaces the bare agent-id string the dashboard used to carry so the agents column can answer
/// <em>direction</em> ("who called whom") and not merely membership.
/// </summary>
/// <remarks>
/// The role is carried through verbatim from <see cref="ParticipantDto.Role"/> rather than parsed
/// into an enum: the gateway stamps free-form strings (<c>initiator</c> / <c>target</c> today) from
/// several call sites, and an unrecognised value must still render. Recognition happens only in
/// <see cref="ActivityDashboardProjection.RoleModifier"/>, which fails open to <see langword="null"/>
/// (no colour treatment) while <see cref="ActivityDashboardProjection.RoleLabel"/> still shows the
/// server's word - the same fail-open posture <c>ParseVisibility</c> takes on display.
/// </remarks>
/// <param name="AgentId">The involved agent's identifier.</param>
/// <param name="Role">
/// The participant role as stamped by the gateway, or <see langword="null"/> when the roster names
/// no role for this agent (the ordinary human/channel case). A blank server value normalises to
/// <see langword="null"/> so "present but empty" and "absent" cannot render differently.
/// </param>
public sealed record ActivityAgentRef(string AgentId, string? Role = null);

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
/// <param name="Visibility">
/// The server-stamped render-visibility class (#2340). Carried as the typed enum, parsed via the
/// existing <see cref="ConversationOrigin.ParseVisibility"/>, so the dashboard reads the modelled
/// field instead of probing the conversation id for an <c>internal:</c> prefix. Unknown or empty
/// wire values degrade to <see cref="ConversationVisibility.UserFacing"/>: the projection fails
/// OPEN on display, because silently hiding a user's conversation is strictly worse than showing an
/// unclassified one.
/// </param>
/// <param name="PinnedAt">
/// When the pin was stamped, or <see langword="null"/> when the conversation is not pinned. Carried
/// so a later surface can explain or order pins by age without a second round trip.
/// </param>
/// <param name="SourceId">
/// The stable identity of the thing that <em>minted</em> the conversation (#2121): the cron job id
/// when <paramref name="Source"/> is <see cref="ConversationSource.Cron"/>, the webhook
/// registration id when it is <see cref="ConversationSource.Webhook"/>, and <see langword="null"/>
/// otherwise. Carried straight through from the server-stamped
/// <see cref="ConversationSummaryDto.SourceId"/> rather than parsed out of the title or the session
/// id - the exact inference the field exists to retire. Meaningful <em>only</em> paired with
/// <paramref name="Source"/>, which is why <see cref="ActivityDashboardProjection.SourceLabel"/>
/// refuses to attribute a row whose source names no originator registry.
/// </param>
/// <param name="ActiveSessionId">
/// The session running in this conversation right now, or <see langword="null"/> when nothing is
/// running (#1888). Carried straight through from the server-stamped
/// <see cref="ConversationSummaryDto.ActiveSessionId"/> rather than inferred, so the dashboard and
/// the chat surface cannot disagree about what is live. This is the row's only <em>present-tense</em>
/// signal - <c>LastActivity</c>, <c>Status</c>, <c>Source</c> and the pin all describe the past.
/// </param>
/// <param name="Purpose">
/// The author's own one-line description of why this conversation exists (#3204), or
/// <see langword="null"/> when none was set. Carried straight through from the server-stamped
/// <see cref="ConversationSummaryDto.Purpose"/>, which the client DTO previously did not declare at
/// all - so the value was discarded at the wire boundary rather than merely unrendered.
/// <para>
/// This is the row's only <em>user-authored prose</em>. The pin is user-authored but boolean; every
/// other signal (origin, originator, roles, visibility, liveness) is machine-derived classification.
/// It is therefore the most explanatory string the platform holds about a row, and it matters most
/// on exactly the rows the derived title helps least - the ones whose title is a routing token.
/// </para>
/// <para>
/// Blank and absent collapse to <see langword="null"/> in <see cref="ActivityDashboardProjection.Project"/>,
/// matching <c>NormalizeRole</c> and <c>SourceLabel</c>, so "present but empty" cannot render
/// differently from "absent".
/// </para>
/// </param>
public sealed record ActivityRow(
    string ConversationId,
    string OwningAgentId,
    string Title,
    string Status,
    DateTimeOffset LastActivity,
    IReadOnlyList<ActivityAgentRef> InvolvedAgents,
    int ChannelCount,
    ConversationSource Source,
    ConversationKind Kind,
    bool IsPinned = false,
    ConversationVisibility Visibility = ConversationVisibility.UserFacing,
    DateTimeOffset? PinnedAt = null,
    string? ActiveSessionId = null,
    string? SourceId = null,
    string? Purpose = null)
{
    /// <summary>
    /// Whether a session is running in this conversation right now. Computed from
    /// <see cref="ActiveSessionId"/> rather than stored, so the flag and the id structurally cannot
    /// disagree - the same drift class the computed <see cref="IsCron"/> removes. Whitespace-only
    /// ids count as idle: a blank id is an absent id, and treating it as live would light up the
    /// badge for a conversation with nothing running.
    /// </summary>
    public bool IsLive => !string.IsNullOrWhiteSpace(ActiveSessionId);

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
/// <param name="LiveCount">
/// How many of the shown conversations have a session running right now (#1888). The only
/// present-tense number on the strip: every other stat counts things that have already happened.
/// </param>
/// <param name="LatestActivity">
/// The freshest last-activity timestamp across the shown rows, or <see langword="null"/> when there
/// are no rows. Lets the UI answer "how recently did anything happen?" without scanning the table.
/// </param>
public sealed record ActivitySummary(
    int ConversationCount,
    int AgentCount,
    int ScheduledCount,
    DateTimeOffset? LatestActivity,
    int LiveCount = 0);

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
    public static IReadOnlyList<ActivityAgentRef> InvolvedAgents(ConversationSummaryDto conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var agentParticipants = (conversation.Participants ?? [])
            .Where(p => string.Equals(p.Kind, "Agent", StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .ToList();

        // First stamped role wins, matching the de-duplication rule for ids: a roster that names the
        // same agent twice yields one chip, so it must also yield one role rather than the last-seen.
        var roles = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var participant in agentParticipants)
        {
            if (!roles.ContainsKey(participant.Id))
                roles[participant.Id] = NormalizeRole(participant.Role);
        }

        string? RoleFor(string agentId) => roles.TryGetValue(agentId, out var role) ? role : null;

        var ordered = new List<ActivityAgentRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The owner leads, as before - but it now contributes its OWN roster role when the roster
        // names it, so owner-first ordering stops masquerading as a direction cue.
        if (!string.IsNullOrWhiteSpace(conversation.AgentId) && seen.Add(conversation.AgentId))
            ordered.Add(new ActivityAgentRef(conversation.AgentId, RoleFor(conversation.AgentId)));

        var participantAgents = agentParticipants
            .Select(p => p.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

        foreach (var agentId in participantAgents)
        {
            if (seen.Add(agentId))
                ordered.Add(new ActivityAgentRef(agentId, RoleFor(agentId)));
        }

        return ordered;
    }

    // Blank is indistinguishable from absent for display purposes, so both collapse to null and the
    // chip renders exactly as an unroled one rather than growing an empty parenthetical.
    private static string? NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : role.Trim();

    // Same rule as NormalizeRole, applied to the purpose string: a whitespace-only purpose is an
    // absent purpose, and letting it through would render an empty line under the title.
    private static string? NormalizePurpose(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();

    /// <summary>
    /// The role text to show on an agent chip, or <see langword="null"/> when the agent has no
    /// stamped role and the chip should render exactly as it did before #2857.
    /// </summary>
    /// <remarks>
    /// Fails OPEN: an unrecognised role from a newer gateway is displayed verbatim rather than
    /// dropped, because losing a stamped distinction is worse than showing an unfamiliar word.
    /// </remarks>
    /// <param name="agent">An involved-agent reference from a projected row.</param>
    public static string? RoleLabel(ActivityAgentRef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent.Role;
    }

    /// <summary>
    /// CSS modifier suffix for the agent chip's role treatment, so the component gets colour without
    /// string-matching on display copy - the same split <see cref="OriginModifier"/> uses.
    /// </summary>
    /// <remarks>
    /// Only the two roles the gateway actually stamps (<c>initiator</c> / <c>target</c>, from
    /// <c>AgentExchangeService</c>, <c>CrossWorldExchangeRouter</c> and
    /// <c>CrossWorldFederationController</c>) get a modifier. Anything else returns
    /// <see langword="null"/>: the role still renders via <see cref="RoleLabel"/>, it simply gets no
    /// colour, so an unknown role degrades to plain text instead of an unstyled class name.
    /// </remarks>
    /// <param name="agent">An involved-agent reference from a projected row.</param>
    public static string? RoleModifier(ActivityAgentRef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent.Role?.ToLowerInvariant() switch
        {
            "initiator" => "initiator",
            "target" => "target",
            _ => null
        };
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
                Visibility = ConversationOrigin.ParseVisibility(c.Visibility),
                IsCron = IsCronConversation(c),
                Agents = InvolvedAgents(c)
            })
            // #2692: InternalHidden is excluded UNCONDITIONALLY and ahead of every facet. The enum's
            // contract is "never rendered to a user", so this is not a facet - no filter combination
            // can reveal these rows. Placed first so no later facet can be read as overriding it.
            .Where(x => x.Visibility != ConversationVisibility.InternalHidden)
            .Where(x => filter.IncludeCron || !x.IsCron)
            .Where(x => MatchesStatus(x.Dto.Status, filter.Status))
            .Where(x => filter.AgentId is null ||
                        x.Agents.Any(a => string.Equals(a.AgentId, filter.AgentId, StringComparison.Ordinal)))
            .Where(x => MatchesRecency(x.Dto.UpdatedAt, filter.Recency, now))
            .Where(x => MatchesOrigin(x.Source, x.Kind, filter.Origin))
            .Where(x => MatchesPinned(x.Dto.IsPinned, filter.Pinned))
            .Where(x => MatchesLive(!string.IsNullOrWhiteSpace(x.Dto.ActiveSessionId), filter.Live))
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
                x.Visibility,
                x.Dto.IsPinned ? x.Dto.PinnedAt : null,
                x.Dto.ActiveSessionId,
                x.Dto.SourceId,
                // Normalised at the projection boundary, not at render time, so every consumer of a
                // row sees the same collapsed value and "present but empty" cannot reach the DOM.
                NormalizePurpose(x.Dto.Purpose)))
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
            return new ActivitySummary(0, 0, 0, null, 0);

        var distinctAgents = new HashSet<string>(StringComparer.Ordinal);
        var scheduled = 0;
        var live = 0;
        DateTimeOffset latest = DateTimeOffset.MinValue;

        foreach (var row in rows)
        {
            // Counts distinct agent IDS, deliberately ignoring role: one agent that is the initiator
            // in one row and the target in another is still one agent on the strip.
            foreach (var agent in row.InvolvedAgents)
                distinctAgents.Add(agent.AgentId);

            if (row.IsCron)
                scheduled++;

            if (row.IsLive)
                live++;

            if (row.LastActivity > latest)
                latest = row.LastActivity;
        }

        return new ActivitySummary(
            rows.Count,
            distinctAgents.Count,
            scheduled,
            latest == DateTimeOffset.MinValue ? null : latest,
            live);
    }

    /// <summary>
    /// Maximum rendered length of a source id before it is elided (#3105). Long enough to keep a
    /// human-authored cron job slug whole, short enough that an opaque 32-character registration
    /// guid cannot grow the row - the same bounded-display posture <c>ConversationLabel.DisplayTitle</c>
    /// applies to titles.
    /// </summary>
    public const int SourceIdDisplayLength = 24;

    /// <summary>
    /// Renders the originator attribution for a row (#3105): <em>which</em> cron job or webhook
    /// registration minted this conversation, as opposed to the origin badge's <em>what class of
    /// thing</em>. Returns <see langword="null"/> when the row names no attributable originator, so
    /// the ordinary human/channel row is untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attribution is refused unless the source names an originator registry. <c>SourceId</c> is
    /// documented as meaningful <em>only</em> paired with <c>Source</c> (see
    /// <c>Conversation.SourceId</c>), so a value arriving on a <see cref="ConversationSource.Channel"/>
    /// or <see cref="ConversationSource.Agent"/> row cannot be attributed to anything a reader could
    /// look up. Rendering it anyway would present an opaque identifier as if it meant something -
    /// worse than showing nothing, because it invites a lookup that cannot succeed.
    /// </para>
    /// <para>
    /// Blank and absent collapse to the same answer, matching <c>NormalizeRole</c>: "present but
    /// empty" must not render differently from "absent", or an empty badge appears on rows the
    /// server declined to attribute.
    /// </para>
    /// <para>
    /// Shaped as a nullable label the component renders conditionally, exactly like
    /// <see cref="OriginLabel"/> and <see cref="ReadOnlyLabel"/>, so the page keeps one badge idiom.
    /// </para>
    /// </remarks>
    /// <param name="row">A projected dashboard row.</param>
    /// <returns>The bounded attribution text, or <see langword="null"/> when nothing is attributable.</returns>
    public static string? SourceLabel(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Source is not (ConversationSource.Cron or ConversationSource.Webhook))
            return null;

        if (string.IsNullOrWhiteSpace(row.SourceId))
            return null;

        var id = row.SourceId.Trim();

        // Elide rather than hard-truncate so a clipped id is visibly clipped: a bare prefix reads as
        // a complete-but-unfamiliar id, which is how a reader ends up searching for something that
        // does not exist. The full value stays reachable via the origin badge's hover detail.
        return id.Length <= SourceIdDisplayLength
            ? id
            : string.Concat(id.AsSpan(0, SourceIdDisplayLength), "\u2026");
    }

    /// <summary>
    /// Maximum rendered length of a purpose before it is elided (#3204). Longer than the source-id
    /// bound because purpose is prose meant to be read, not an identifier meant to be matched, but
    /// still bounded: <c>ValidatePurpose</c> admits far more text than a table row can carry, and an
    /// unbounded string must never reach the DOM. Same structural-guarantee posture as
    /// <see cref="ConversationLabel.MaxTitleLength"/> - CSS may also ellipsize, but this cap
    /// survives a CSS regression.
    /// </summary>
    public const int PurposeDisplayLength = 96;

    /// <summary>
    /// Renders the author's stated purpose for a row (#3204): <em>why this conversation exists</em>
    /// in the words of whoever created it, as opposed to the origin badge's machine classification.
    /// Returns <see langword="null"/> when the row names no purpose, so a row without one is
    /// rendered exactly as it was before this shipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> shaped as a badge, unlike <see cref="OriginLabel"/>,
    /// <see cref="ReadOnlyLabel"/> and <see cref="SourceLabel"/>. Those are one-glance classifiers
    /// drawn from bounded value sets; purpose is free prose. Giving prose the badge idiom would
    /// swamp the badge row and destroy the scannability the badges exist to provide.
    /// </para>
    /// <para>
    /// Elides rather than hard-truncating, matching <see cref="SourceLabel"/>, so a clipped purpose
    /// is visibly clipped and the reader knows to hover for the rest. The component keeps the
    /// untruncated value on the element's <c>title</c>.
    /// </para>
    /// <para>
    /// Normalisation already happened in <see cref="Project"/>, so this method sees a value that is
    /// either <see langword="null"/> or non-blank. It re-checks anyway rather than trusting its
    /// caller, because it is public and a hand-constructed row must not be able to emit an empty
    /// element.
    /// </para>
    /// </remarks>
    /// <param name="row">A projected dashboard row.</param>
    /// <returns>The bounded purpose text, or <see langword="null"/> when the row states none.</returns>
    public static string? PurposeLabel(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.Purpose))
            return null;

        var purpose = row.Purpose.Trim();

        return purpose.Length <= PurposeDisplayLength
            ? purpose
            : string.Concat(purpose.AsSpan(0, PurposeDisplayLength), "\u2026");
    }

    /// <summary>
    /// Renders the read-only marker for a row, or <see langword="null"/> when the row needs none
    /// (#2692). Only <see cref="ConversationVisibility.InspectableReadOnly"/> is marked:
    /// <c>UserFacing</c> is the overwhelmingly common case and stays unmarked so the marker carries
    /// signal, and <c>InternalHidden</c> never reaches a row because <see cref="Project"/> drops it.
    /// </summary>
    /// <remarks>
    /// Deliberately shaped like <see cref="OriginLabel"/> - a nullable label the component renders
    /// conditionally - so the dashboard has one badge idiom rather than two.
    /// </remarks>
    /// <param name="row">A projected dashboard row.</param>
    /// <returns>The marker text, or <see langword="null"/> when no marker should render.</returns>
    public static string? ReadOnlyLabel(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.Visibility == ConversationVisibility.InspectableReadOnly ? "Read-only" : null;
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

    // All short-circuits rather than comparing, so the default filter costs nothing per row on the
    // common unfiltered landing view - the same shape as MatchesOrigin/MatchesPinned.
    private static bool MatchesLive(bool isLive, ActivityLiveFilter filter) => filter switch
    {
        ActivityLiveFilter.Live => isLive,
        ActivityLiveFilter.Idle => !isLive,
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
