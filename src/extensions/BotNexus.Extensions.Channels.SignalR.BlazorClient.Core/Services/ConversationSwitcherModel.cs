namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// One selectable row in the conversation switcher: a conversation plus the agent that owns it.
/// </summary>
/// <remarks>
/// The owning agent is carried explicitly because <see cref="ConversationState"/> does not know it -
/// it has no <c>AgentId</c> field, since it is always stored inside the agent that owns it. Once the
/// switcher can list another agent's conversations, "which agent does this row belong to" stops
/// being answerable from ambient context and has to travel with the row, or selecting a row would
/// route to the wrong agent.
/// </remarks>
/// <param name="AgentId">The agent that owns <paramref name="Conversation"/>.</param>
/// <param name="AgentDisplayName">That agent's display name, for labelling a cross-agent row.</param>
/// <param name="Conversation">The conversation this row opens.</param>
/// <param name="GroupLabel">The label of the group this row renders under.</param>
/// <param name="IsOtherAgent">True when this row belongs to an agent other than the active one.</param>
/// <param name="Snippet">
/// The matching message, for a row found by CONTENT rather than by title. Null for title matches,
/// where the title is already the evidence. Appended last and optional so every existing
/// construction keeps compiling.
/// </param>
public sealed record ConversationSwitcherRow(
    string AgentId,
    string AgentDisplayName,
    ConversationState Conversation,
    string GroupLabel,
    bool IsOtherAgent,
    string? Snippet = null);

/// <summary>
/// One conversation the backend found by its content, with the line that proves why.
/// </summary>
/// <remarks>
/// A LIST of these rather than a dictionary keyed by id, because the order is information: the
/// endpoint returns hits in bm25 relevance order, and that ranking is the only signal saying which
/// match is the good one. A dictionary discards it, after which rows come out in whatever order the
/// roster happens to be in.
/// </remarks>
/// <param name="ConversationId">The conversation the match was found in.</param>
/// <param name="Snippet">The best-ranked matching line for it.</param>
public sealed record ConversationContentMatch(string ConversationId, string Snippet);

/// <summary>A labelled group of switcher rows, in render order. Never empty.</summary>
/// <param name="Label">The group heading.</param>
/// <param name="Rows">The rows in the group, already in display order.</param>
public sealed record ConversationSwitcherGroup(string Label, IReadOnlyList<ConversationSwitcherRow> Rows);

/// <summary>
/// The rendered shape of the conversation switcher: the grouped rows to draw, plus the same rows
/// flattened into the exact order they appear on screen.
/// </summary>
/// <param name="Groups">Labelled groups in render order. Never contains an empty group.</param>
/// <param name="Flattened">
/// Every row in <paramref name="Groups"/>, concatenated group by group in render order. The keyboard
/// highlight is an index into THIS list, which is why it is built here beside the groups rather than
/// re-derived by the component: a flatten computed separately from the render could disagree with
/// what the user sees, and arrow-down would then select a different row from the highlighted one.
/// </param>
public sealed record ConversationSwitcherView(
    IReadOnlyList<ConversationSwitcherGroup> Groups,
    IReadOnlyList<ConversationSwitcherRow> Flattened)
{
    /// <summary>An empty view - no groups, nothing to highlight.</summary>
    public static readonly ConversationSwitcherView Empty = new([], []);

    /// <summary>Total rows across all groups; the bound for a keyboard highlight index.</summary>
    public int Count => Flattened.Count;

    /// <summary>True when the query matched nothing (or there are no switchable conversations).</summary>
    public bool IsEmpty => Flattened.Count == 0;
}

/// <summary>
/// Builds the conversation switcher's contents: which conversations are switchable, how a typed
/// query narrows them, and how the survivors group.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a pure function rather than as logic inside the component so the rules that decide
/// what a user can reach - visibility, archived-ness, agent scope and the query match - are testable
/// without rendering, and so the switcher cannot drift from the sidebar's own notion of which
/// conversations exist. Grouping of the active agent's rows is <b>delegated</b> to
/// <see cref="PortalConversationGrouping.ForPicker"/>, the single client-wide partition the desktop
/// sidebar and the mobile picker already share; this type deliberately adds no group of its own to
/// that partition and re-implements no precedence rule.
/// </para>
/// <para>
/// <b>Sections are intentionally not honoured.</b> The desktop sidebar subtracts a section-assigned
/// conversation from its default list and renders it inside the section instead. The switcher does
/// the opposite: a section-assigned conversation still appears here, under
/// <see cref="PortalConversationGrouping.ConversationsLabel"/>. The switcher's whole purpose is that
/// one query reaches every conversation without the user first knowing where it was filed, so
/// honouring the subtraction would reproduce exactly the findability problem it was built to solve.
/// </para>
/// </remarks>
public static class ConversationSwitcherModel
{
    /// <summary>The heading for conversations belonging to agents other than the active one.</summary>
    public const string OtherAgentsLabel = "Other agents";

    /// <summary>
    /// Heading for conversations matched by what was SAID in them rather than by their title.
    /// </summary>
    /// <remarks>
    /// A separate group rather than rows mixed into the title matches, because the two answer
    /// different questions: a title match is "this is called that", a content match is "someone
    /// said that in here". Mixing them would leave the reader unable to tell why a row appeared,
    /// which is exactly the information the snippet exists to supply.
    /// </remarks>
    public const string FoundInMessagesLabel = "Found in messages";

    /// <summary>
    /// Build the switcher view for the active agent, extending to every other agent once the user
    /// has typed something.
    /// </summary>
    /// <param name="currentAgentId">The agent whose panel the switcher is mounted in.</param>
    /// <param name="agents">
    /// Every agent in the store. All of them already carry their conversations: the portal fetches
    /// conversations for every agent during bootstrap (<c>PortalLoadService</c> fans
    /// <c>GetConversationsAsync</c> across the whole roster), so cross-agent search needs no
    /// additional request and no loading state.
    /// </param>
    /// <param name="selectionSource">Current view-selection source, fed to the render projection.</param>
    /// <param name="cronConversationIds">
    /// Authoritative cron-job to conversation-id map, or null. Passed straight through to the shared
    /// grouping helper; a null/empty set degrades to projection-only grouping exactly as it does for
    /// the sidebar and the mobile picker.
    /// </param>
    /// <param name="query">The typed filter. Null, empty or whitespace means "no filter".</param>
    /// <returns>The grouped and flattened view.</returns>
    public static ConversationSwitcherView Build(
        string currentAgentId,
        IEnumerable<AgentState> agents,
        SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds,
        string? query,
        IReadOnlyList<ConversationContentMatch>? contentMatches = null)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var roster = agents.ToList();
        var current = roster.FirstOrDefault(a => string.Equals(a.AgentId, currentAgentId, StringComparison.Ordinal));

        var groups = new List<ConversationSwitcherGroup>();

        if (current is not null)
            groups.AddRange(BuildCurrentAgentGroups(current, selectionSource, cronConversationIds, query));

        var otherAgents = BuildOtherAgentsGroup(currentAgentId, roster, query);
        if (otherAgents is not null)
            groups.Add(otherAgents);

        var foundInMessages = BuildContentGroup(currentAgentId, roster, groups, contentMatches);
        if (foundInMessages is not null)
            groups.Add(foundInMessages);

        if (groups.Count == 0)
            return ConversationSwitcherView.Empty;

        return new ConversationSwitcherView(groups, groups.SelectMany(g => g.Rows).ToList());
    }

    /// <summary>
    /// Conversations matched by what was said in them, minus any the title match already listed.
    /// </summary>
    /// <remarks>
    /// Built HERE rather than appended by the component, so content rows land in the same
    /// <c>Flattened</c> list the keyboard walks. A group bolted on at render time would be visible
    /// and unreachable by arrow key, which is worse than not showing it.
    /// <para>
    /// Rows already listed by title are skipped: the same conversation appearing twice makes the
    /// list longer without making it more useful, and the title match is the stronger signal.
    /// </para>
    /// </remarks>
    private static ConversationSwitcherGroup? BuildContentGroup(
        string currentAgentId,
        IReadOnlyList<AgentState> roster,
        IReadOnlyList<ConversationSwitcherGroup> existing,
        IReadOnlyList<ConversationContentMatch>? contentMatches)
    {
        if (contentMatches is not { Count: > 0 })
            return null;

        var alreadyShown = existing
            .SelectMany(g => g.Rows)
            .Select(r => r.Conversation.ConversationId)
            .ToHashSet(StringComparer.Ordinal);

        // Every conversation a content hit is ALLOWED to resolve to, indexed for lookup.
        //
        // Read-only agents are excluded for the same reason BuildOtherAgentsGroup excludes them:
        // the sidebar's own agent dropdown lists only !IsReadOnly agents, so offering one of their
        // conversations as a destination contradicts the rest of the portal.
        //
        // Reachable(), not agent.Conversations.Values. The search endpoint walks all of session
        // history and knows nothing about archived rows or runtime-internal threads, so this is the
        // ONLY place those can be excluded - and without it, content search surfaces conversations
        // every other group in this model deliberately hides. Measured against a real instance, the
        // endpoint returned 31 distinct archived conversations, and for the query "memory" 16 of 27
        // hits were archived.
        var candidates = new Dictionary<string, (AgentState Agent, ConversationState Conversation)>(StringComparer.Ordinal);
        foreach (var agent in roster)
        {
            if (agent.IsReadOnly)
                continue;

            foreach (var conversation in Reachable(agent))
            {
                if (conversation.ConversationId is { Length: > 0 } id)
                    candidates.TryAdd(id, (agent, conversation));
            }
        }

        // Driven by the MATCH list, not the roster: hits arrive in bm25 relevance order, and
        // walking the roster instead would re-order them by whatever order agents happen to be in,
        // discarding the only signal that says which match is the good one.
        var rows = new List<ConversationSwitcherRow>();
        foreach (var match in contentMatches)
        {
            if (string.IsNullOrEmpty(match.ConversationId) || !alreadyShown.Add(match.ConversationId))
                continue;

            if (!candidates.TryGetValue(match.ConversationId, out var found))
                continue;

            rows.Add(new ConversationSwitcherRow(
                found.Agent.AgentId,
                found.Agent.DisplayName,
                found.Conversation,
                FoundInMessagesLabel,
                // A hit can belong to any agent. Marking it as the current agent's would hide the
                // owning-agent label and let the row be styled active for a conversation that is
                // not the one on screen.
                IsOtherAgent: !string.Equals(found.Agent.AgentId, currentAgentId, StringComparison.Ordinal),
                Snippet: match.Snippet));
        }

        return rows.Count == 0 ? null : new ConversationSwitcherGroup(FoundInMessagesLabel, rows);
    }

    /// <summary>
    /// The active agent's rows, partitioned by the shared picker grouping.
    /// </summary>
    private static List<ConversationSwitcherGroup> BuildCurrentAgentGroups(
        AgentState agent,
        SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds,
        string? query)
    {
        var candidates = Reachable(agent).Where(c => Matches(c, query)).ToList();
        if (candidates.Count == 0)
            return [];

        return PortalConversationGrouping.ForPicker(candidates, selectionSource, cronConversationIds)
            .Select(g => new ConversationSwitcherGroup(
                g.Label,
                g.Conversations
                    .Select(c => new ConversationSwitcherRow(agent.AgentId, agent.DisplayName, c, g.Label, IsOtherAgent: false))
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Matches from every OTHER agent, as a single trailing group, or null when there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only when the user has typed something.</b> With an empty query this returns null, so an
    /// unfiltered switcher lists exactly what it always did - the current agent. Listing every
    /// conversation of every agent by default would put hundreds of rows behind a control whose
    /// entire purpose is to make one conversation quick to reach.
    /// </para>
    /// <para>
    /// <b>One flat group, not one group per agent.</b> A deployment with sixteen agents would
    /// otherwise render sixteen headings above one or two rows each. The owning agent is shown on
    /// each row instead, and rows sort by agent then title so an agent's matches stay together.
    /// </para>
    /// <para>
    /// Observer/read-only agents are excluded, matching the sidebar's own agent dropdown, which
    /// lists only <c>!IsReadOnly</c> agents. Surfacing conversations here for an agent the user
    /// cannot otherwise select would offer a destination the rest of the portal hides.
    /// </para>
    /// </remarks>
    private static ConversationSwitcherGroup? BuildOtherAgentsGroup(
        string currentAgentId,
        IEnumerable<AgentState> roster,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var rows = roster
            .Where(a => !string.Equals(a.AgentId, currentAgentId, StringComparison.Ordinal))
            .Where(a => !a.IsReadOnly)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.AgentId, StringComparer.Ordinal)
            .SelectMany(a => Reachable(a)
                .Where(c => Matches(c, query))
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new ConversationSwitcherRow(a.AgentId, a.DisplayName, c, OtherAgentsLabel, IsOtherAgent: true)))
            .ToList();

        return rows.Count == 0 ? null : new ConversationSwitcherGroup(OtherAgentsLabel, rows);
    }

    /// <summary>
    /// The conversations of one agent that the user may switch to at all.
    /// </summary>
    /// <remarks>
    /// Visibility first: <see cref="PortalListOrdering.IsUserFacingConversation"/> is the ONE
    /// predicate for "may the user see this". <see cref="PortalConversationGrouping.ForPicker"/> does
    /// not apply it - it partitions whatever it is handed - so omitting it would let runtime-internal
    /// bookkeeping threads into the switcher even though the sidebar hides them. Archived rows are
    /// dropped for the same reason the cold-start resolver drops them: they are not somewhere the
    /// user can switch TO.
    /// </remarks>
    private static IEnumerable<ConversationState> Reachable(AgentState agent) =>
        agent.Conversations.Values
            .ToArray() // the live dictionary is mutated by SignalR handlers mid-render (#2320)
            .Where(PortalListOrdering.IsUserFacingConversation)
            .Where(c => !PortalListOrdering.IsArchivedConversation(c));

    /// <summary>
    /// True when a conversation survives the typed query.
    /// </summary>
    /// <remarks>
    /// Case-insensitive substring over the title, and nothing cleverer. A subsequence or fuzzy match
    /// would rank "Daily Cost Monitor Report" as a hit for "dcm", but it also makes near-miss typing
    /// return a list the user cannot explain, and there is no relevance score here to push the good
    /// hits back to the top. Substring is the behaviour a user can predict from what they typed.
    /// The conversation id is deliberately NOT searched: ids are opaque server-minted tokens, and
    /// matching them would surface rows whose visible title has nothing to do with the query.
    /// </remarks>
    /// <param name="conversation">The conversation to test.</param>
    /// <param name="query">The typed filter; null/empty/whitespace matches everything.</param>
    /// <returns>True when the conversation should be listed.</returns>
    public static bool Matches(ConversationState conversation, string? query)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (string.IsNullOrWhiteSpace(query))
            return true;

        return conversation.Title?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// True when a switcher row leads to a conversation the user can read but not write to, so the
    /// row can say so before it is clicked.
    /// </summary>
    /// <remarks>
    /// Two clauses, because neither alone is complete. <see cref="ConversationRenderProjection.IsUnattended"/>
    /// catches the agent-initiated, sub-agent and ralph rows that group under
    /// <see cref="PortalConversationGrouping.ConversationsLabel"/> alongside ordinary chats and are
    /// otherwise indistinguishable. The group label catches the inverse case: a channel-created
    /// conversation later adopted by a cron job keeps <c>Source = Channel</c> forever (#2304), so
    /// the projection cannot see that it is unattended, and only its membership of the Scheduled
    /// group - which came from the authoritative cron id map - reveals it. Without the second
    /// clause such a row would sit under a "Scheduled" heading with no read-only marker.
    /// <para>
    /// The store's ambient selection source is deliberately NOT threaded in, and
    /// <see cref="ConversationRenderProjection.IsReadOnly"/> is deliberately not the property read.
    /// Both are view state about the conversation currently on screen: while the active view was
    /// promoted by "view sub-agent" they report read-only for EVERY conversation, which would mark
    /// every row in this list read-only for as long as the user observes one. A row must describe
    /// the conversation it points at, not the one being looked at, so a fixed neutral
    /// <see cref="SelectionSource.UserClick"/> is passed and only the selection-independent
    /// <see cref="ConversationRenderProjection.IsUnattended"/> is read.
    /// </para>
    /// </remarks>
    /// <param name="conversation">The conversation the row points at.</param>
    /// <param name="groupLabel">The label of the group the row is rendered under.</param>
    /// <returns>True when the row should carry a read-only marker.</returns>
    public static bool IsReadOnlyRow(ConversationState conversation, string groupLabel)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Project(SelectionSource.UserClick).IsUnattended)
            return true;

        return string.Equals(groupLabel, PortalConversationGrouping.ScheduledLabel, StringComparison.Ordinal)
            || string.Equals(groupLabel, PortalConversationGrouping.WebhooksLabel, StringComparison.Ordinal);
    }
}
