using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The three rules that decide what the conversation switcher can reach - visibility,
/// archived-ness and the typed query - plus the read-only marker. Pure model tests: no render, so a
/// change to these rules fails here rather than in a component test that could pass for the wrong
/// reason.
/// </summary>
public sealed class ConversationSwitcherModelTests
{
    private static ConversationState Conv(
        string id,
        string title,
        bool pinned = false,
        string status = "Active",
        ConversationSource source = ConversationSource.Channel,
        ConversationKind kind = ConversationKind.HumanAgent,
        ConversationVisibility visibility = ConversationVisibility.UserFacing) =>
        new()
        {
            ConversationId = id,
            Title = title,
            IsPinned = pinned,
            Status = status,
            Source = source,
            Kind = kind,
            Visibility = visibility,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static AgentState Agent(string agentId, string displayName, bool observer = false, params ConversationState[] conversations)
    {
        var agent = new AgentState { AgentId = agentId, DisplayName = displayName, IsObserverAgent = observer };
        foreach (var c in conversations)
            agent.Conversations[c.ConversationId] = c;
        return agent;
    }

    /// <summary>Single-agent build - the common case, with only the active agent on the roster.</summary>
    private static ConversationSwitcherView Build(IEnumerable<ConversationState> conversations, string? query = null) =>
        ConversationSwitcherModel.Build(
            "a-1",
            [Agent("a-1", "Alpha", false, conversations.ToArray())],
            SelectionSource.UserClick,
            null,
            query);

    /// <summary>Multi-agent build, for the cross-agent behaviour.</summary>
    private static ConversationSwitcherView BuildRoster(string currentAgentId, IEnumerable<AgentState> roster, string? query) =>
        ConversationSwitcherModel.Build(currentAgentId, roster, SelectionSource.UserClick, null, query);

    [Fact]
    public void NoQuery_ReturnsEveryReachableConversation()
    {
        var view = Build([Conv("c-1", "Alpha"), Conv("c-2", "Beta")]);

        Assert.Equal(2, view.Count);
        Assert.False(view.IsEmpty);
    }

    [Fact]
    public void Query_MatchesTitleCaseInsensitively()
    {
        var view = Build([Conv("c-1", "Daily Cost Monitor"), Conv("c-2", "Skill Review")], "COST");

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].Conversation.ConversationId);
    }

    [Fact]
    public void Query_MatchesMidTitleSubstring()
    {
        var view = Build([Conv("c-1", "Exact Reply Instruction Followed")], "reply inst");

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void Query_IsTrimmedBeforeMatching()
    {
        var view = Build([Conv("c-1", "Alpha")], "  alpha  ");

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void Query_WhitespaceOnly_IsTreatedAsNoFilter()
    {
        var view = Build([Conv("c-1", "Alpha"), Conv("c-2", "Beta")], "   ");

        Assert.Equal(2, view.Count);
    }

    [Fact]
    public void Query_DoesNotMatchConversationId()
    {
        // Ids are opaque server tokens. Matching them would surface a row whose visible title has
        // nothing to do with what the user typed.
        var view = Build([Conv("conv-abc123", "Alpha")], "abc123");

        Assert.True(view.IsEmpty);
    }

    [Fact]
    public void Query_MatchingNothing_YieldsEmptyView()
    {
        var view = Build([Conv("c-1", "Alpha")], "zzz");

        Assert.True(view.IsEmpty);
        Assert.Empty(view.Groups);
    }

    [Fact]
    public void InternalHiddenConversations_AreNeverListed()
    {
        // ForPicker partitions whatever it is handed; the visibility predicate is this model's job.
        var view = Build(
        [
            Conv("c-1", "Visible"),
            Conv("internal-1", "Bookkeeping", visibility: ConversationVisibility.InternalHidden),
        ]);

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].Conversation.ConversationId);
    }

    [Fact]
    public void InspectableReadOnlyConversations_AreListed()
    {
        // Visible-but-not-writable is still somewhere the user can go.
        var view = Build([Conv("c-1", "Observer", visibility: ConversationVisibility.InspectableReadOnly)]);

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void ArchivedConversations_AreNotListed()
    {
        var view = Build([Conv("c-1", "Live"), Conv("c-2", "Old", status: "Archived")]);

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].Conversation.ConversationId);
    }

    [Fact]
    public void ArchivedStatus_IsMatchedIgnoringCase()
    {
        // Status is a free-form server string; casing was never promised.
        var view = Build([Conv("c-1", "Old", status: "archived")]);

        Assert.True(view.IsEmpty);
    }

    [Fact]
    public void FlattenedOrder_MatchesGroupRenderOrder()
    {
        // The keyboard highlight is an index into Flattened, so it MUST be the groups concatenated
        // in render order - otherwise arrow-down highlights one row and Enter opens another.
        var view = Build(
        [
            Conv("c-normal", "Normal"),
            Conv("c-pinned", "Pinned One", pinned: true),
            Conv("c-cron", "Nightly", source: ConversationSource.Cron),
        ]);

        var expected = view.Groups.SelectMany(g => g.Rows.Select(r => r.Conversation.ConversationId)).ToList();
        Assert.Equal(expected, view.Flattened.Select(r => r.Conversation.ConversationId).ToList());
    }

    [Fact]
    public void Grouping_IsDelegatedToTheSharedPickerPartition()
    {
        var view = Build(
        [
            Conv("c-normal", "Normal"),
            Conv("c-pinned", "Pinned One", pinned: true),
            Conv("c-cron", "Nightly", source: ConversationSource.Cron),
            Conv("c-hook", "Hooked", source: ConversationSource.Webhook),
        ]);

        var expectedLabels = new List<string>
        {
            PortalConversationGrouping.PinnedLabel,
            PortalConversationGrouping.ConversationsLabel,
            PortalConversationGrouping.ScheduledLabel,
            PortalConversationGrouping.WebhooksLabel,
        };

        Assert.Equal(expectedLabels, view.Groups.Select(g => g.Label).ToList());
    }

    [Fact]
    public void Grouping_DropsEmptyGroups()
    {
        var view = Build([Conv("c-1", "Only A Normal One")]);

        Assert.Single(view.Groups);
        Assert.Equal(PortalConversationGrouping.ConversationsLabel, view.Groups[0].Label);
    }

    [Fact]
    public void ReadOnlyRow_TrueForUnattendedConversationInTheNormalGroup()
    {
        // A sub-agent transcript groups under "Conversations" beside ordinary chats, so without the
        // projection clause it would be indistinguishable from a writable one.
        var subAgent = Conv("c-1", "Sub-agent run", kind: ConversationKind.AgentSubAgent);

        Assert.True(ConversationSwitcherModel.IsReadOnlyRow(subAgent, PortalConversationGrouping.ConversationsLabel));
    }

    [Fact]
    public void ReadOnlyRow_TrueForCronAdoptedConversationViaItsGroup()
    {
        // Source is write-once (#2304): a channel-created conversation later adopted by a cron job
        // keeps Source=Channel forever, so only its Scheduled group membership reveals it.
        var adopted = Conv("c-1", "Adopted by cron", source: ConversationSource.Channel);

        Assert.False(adopted.Project(SelectionSource.UserClick).IsUnattended);
        Assert.True(ConversationSwitcherModel.IsReadOnlyRow(adopted, PortalConversationGrouping.ScheduledLabel));
    }

    [Fact]
    public void ReadOnlyRow_FalseForAnOrdinaryChat()
    {
        var chat = Conv("c-1", "Ordinary");

        Assert.False(ConversationSwitcherModel.IsReadOnlyRow(chat, PortalConversationGrouping.ConversationsLabel));
    }

    [Fact]
    public void ReadOnlyRow_FalseForAPinnedOrdinaryChat()
    {
        // Pinning must not imply read-only: the Pinned group holds ordinary writable chats.
        var chat = Conv("c-1", "Pinned ordinary", pinned: true);

        Assert.False(ConversationSwitcherModel.IsReadOnlyRow(chat, PortalConversationGrouping.PinnedLabel));
    }

    // ---- cross-agent search -------------------------------------------------------------------

    [Fact]
    public void WithNoQuery_OtherAgentsAreNotListed()
    {
        // An unfiltered switcher must list exactly what it always did: the current agent. Dumping
        // every agent's conversations by default would bury the control's whole purpose.
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Mine")),
            Agent("a-2", "Beta", false, Conv("c-2", "Theirs")),
        ], query: null);

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].Conversation.ConversationId);
        Assert.DoesNotContain(ConversationSwitcherModel.OtherAgentsLabel, view.Groups.Select(g => g.Label));
    }

    [Fact]
    public void WithAQuery_MatchesFromOtherAgentsAppearUnderTheirOwnGroup()
    {
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes")),
            Agent("a-2", "Beta", false, Conv("c-2", "Deploy runbook")),
        ], "deploy");

        Assert.Equal(2, view.Count);
        Assert.Equal(ConversationSwitcherModel.OtherAgentsLabel, view.Groups[^1].Label);

        var foreign = view.Flattened.Single(r => r.IsOtherAgent);
        Assert.Equal("c-2", foreign.Conversation.ConversationId);
        Assert.Equal("a-2", foreign.AgentId);
        Assert.Equal("Beta", foreign.AgentDisplayName);
    }

    [Fact]
    public void OtherAgentsGroupIsAlwaysLast()
    {
        // It is the widening of the search, so it must never push the current agent's own matches
        // down the list.
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes", pinned: true), Conv("c-3", "Deploy plan")),
            Agent("a-2", "Beta", false, Conv("c-2", "Deploy runbook")),
        ], "deploy");

        Assert.Equal(ConversationSwitcherModel.OtherAgentsLabel, view.Groups[^1].Label);
        Assert.All(view.Groups.Take(view.Groups.Count - 1), g => Assert.All(g.Rows, r => Assert.False(r.IsOtherAgent)));
    }

    [Fact]
    public void CurrentAgentRowsAreNeverMarkedAsOtherAgent()
    {
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes")),
            Agent("a-2", "Beta", false, Conv("c-2", "Deploy runbook")),
        ], "deploy");

        var own = view.Flattened.Single(r => r.Conversation.ConversationId == "c-1");
        Assert.False(own.IsOtherAgent);
        Assert.Equal("a-1", own.AgentId);
    }

    [Fact]
    public void ObserverAgentsAreExcludedFromCrossAgentResults()
    {
        // The sidebar's agent dropdown lists only !IsReadOnly agents, so surfacing an observer
        // agent's conversation here would offer a destination the rest of the portal hides.
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes")),
            Agent("a-obs", "Observer", true, Conv("c-obs", "Deploy watched")),
        ], "deploy");

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].Conversation.ConversationId);
    }

    [Fact]
    public void CrossAgentResultsRespectVisibilityAndArchivedRules()
    {
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes")),
            Agent("a-2", "Beta", false,
                Conv("c-hidden", "Deploy internal", visibility: ConversationVisibility.InternalHidden),
                Conv("c-old", "Deploy archived", status: "Archived"),
                Conv("c-ok", "Deploy live")),
        ], "deploy");

        var foreignIds = view.Flattened.Where(r => r.IsOtherAgent).Select(r => r.Conversation.ConversationId).ToList();
        Assert.Equal(new List<string> { "c-ok" }, foreignIds);
    }

    [Fact]
    public void OtherAgentRowsSortByAgentNameThenRecency()
    {
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        var zed = Agent("a-z", "Zed", false, Conv("c-z", "Deploy z"));
        var beta = new AgentState { AgentId = "a-2", DisplayName = "Beta" };
        beta.Conversations["c-old"] = Conv("c-old", "Deploy older");
        beta.Conversations["c-old"].UpdatedAt = older;
        beta.Conversations["c-new"] = Conv("c-new", "Deploy newer");

        var view = BuildRoster("a-1", [Agent("a-1", "Alpha", false), beta, zed], "deploy");

        Assert.Equal(
            new List<string> { "c-new", "c-old", "c-z" },
            view.Flattened.Where(r => r.IsOtherAgent).Select(r => r.Conversation.ConversationId).ToList());
    }

    [Fact]
    public void FlattenedOrderStillMatchesRenderOrderWithOtherAgents()
    {
        // The keyboard highlight indexes into Flattened, so the invariant has to survive the extra
        // group - otherwise arrowing into cross-agent results opens the wrong conversation.
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Deploy notes"), Conv("c-p", "Deploy pinned", pinned: true)),
            Agent("a-2", "Beta", false, Conv("c-2", "Deploy runbook")),
        ], "deploy");

        var expected = view.Groups.SelectMany(g => g.Rows.Select(r => r.Conversation.ConversationId)).ToList();
        Assert.Equal(expected, view.Flattened.Select(r => r.Conversation.ConversationId).ToList());
    }

    [Fact]
    public void AQueryMatchingOnlyAnotherAgent_StillReturnsThatAgentsRows()
    {
        // The point of the feature: the current agent has no match at all, and the user still finds
        // the conversation.
        var view = BuildRoster("a-1",
        [
            Agent("a-1", "Alpha", false, Conv("c-1", "Nothing relevant")),
            Agent("a-2", "Beta", false, Conv("c-2", "Gateway restart guide")),
        ], "gateway");

        Assert.Single(view.Flattened);
        Assert.True(view.Flattened[0].IsOtherAgent);
        Assert.Equal("c-2", view.Flattened[0].Conversation.ConversationId);
        Assert.Single(view.Groups);
        Assert.Equal(ConversationSwitcherModel.OtherAgentsLabel, view.Groups[0].Label);
    }

    [Fact]
    public void AnUnknownCurrentAgentStillSearchesEveryOtherAgent()
    {
        // Defensive: the parameter names an agent the store does not hold. The switcher degrades to
        // cross-agent results rather than throwing or rendering empty.
        var view = BuildRoster("a-missing", [Agent("a-2", "Beta", false, Conv("c-2", "Deploy runbook"))], "deploy");

        Assert.Single(view.Flattened);
        Assert.True(view.Flattened[0].IsOtherAgent);
    }

    [Fact]
    public void Build_WithNoConversations_YieldsTheEmptyView()
    {
        var view = Build([]);

        Assert.True(view.IsEmpty);
        Assert.Empty(view.Groups);
        Assert.Equal(0, view.Count);
    }

    // ── content matches (Interface Review P1 hookup) ──────────────────────────
    // Titles were the only thing the switcher could see. These cover the rows that appear because
    // of what was SAID in a conversation, which is the half that could not work before.

    private static ConversationSwitcherView BuildWithContent(
        IEnumerable<ConversationState> conversations,
        string? query,
        IReadOnlyList<ConversationContentMatch> contentMatches) =>
        ConversationSwitcherModel.Build(
            "a-1",
            [Agent("a-1", "Alpha", false, conversations.ToArray())],
            SelectionSource.UserClick,
            null,
            query,
            contentMatches);

    [Fact]
    public void A_conversation_matched_only_by_content_still_appears()
    {
        // The title says nothing about "gateway"; the transcript does. Before this, the row was
        // unreachable from the switcher no matter what was in it.
        var conversations = new[] { Conv("c-1", "Tuesday standup") };

        var view = BuildWithContent(conversations, "gateway", [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        var group = view.Groups.ShouldHaveSingleItem();
        group.Label.ShouldBe(ConversationSwitcherModel.FoundInMessagesLabel);
        group.Rows.ShouldHaveSingleItem().Conversation.ConversationId.ShouldBe("c-1");
    }

    [Fact]
    public void The_snippet_travels_with_the_row_because_it_is_why_the_row_is_there()
    {
        var view = BuildWithContent(
            [Conv("c-1", "Tuesday standup")],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Groups.ShouldHaveSingleItem().Rows.ShouldHaveSingleItem()
            .Snippet.ShouldBe("the gateway restart lost its pid file");
    }

    // ── content matches must obey the same visibility rules as every other group ──
    // The search endpoint walks all of session history. It knows nothing about archived rows,
    // runtime-internal threads or read-only agents, so this model is the only place those can be
    // excluded. Against a real instance the endpoint returned 31 distinct ARCHIVED conversations;
    // for the query "memory", 16 of 27 hits were archived.

    [Fact]
    public void An_archived_conversation_matched_by_content_stays_hidden()
    {
        var view = BuildWithContent(
            [Conv("c-1", "Tuesday standup", status: "Archived")],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Groups.ShouldNotContain(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel);
    }

    [Fact]
    public void A_runtime_internal_conversation_matched_by_content_stays_hidden()
    {
        // The sidebar hides these outright. Search must not be the one surface that reveals them.
        var view = BuildWithContent(
            [Conv("c-1", "Bookkeeping", visibility: ConversationVisibility.InternalHidden)],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Groups.ShouldNotContain(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel);
    }

    [Fact]
    public void A_read_only_agents_conversation_matched_by_content_stays_hidden()
    {
        // BuildOtherAgentsGroup already excludes observer agents because the sidebar's own agent
        // dropdown does. Offering one of their conversations here would contradict that.
        var view = ConversationSwitcherModel.Build(
            "a-1",
            [
                Agent("a-1", "Alpha", false, Conv("c-1", "Tuesday standup")),
                Agent("a-2", "Observer", true, Conv("c-2", "Watched thread")),
            ],
            SelectionSource.UserClick,
            null,
            "gateway",
            [new ConversationContentMatch("c-2", "the gateway restart lost its pid file")]);

        view.Groups.ShouldNotContain(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel);
        view.Flattened.ShouldNotContain(r => r.Conversation.ConversationId == "c-2");
    }

    [Fact]
    public void A_conversation_already_matched_by_title_is_not_listed_twice()
    {
        // The same row appearing under two headings makes the list longer without making it more
        // useful, and the title match is the stronger signal.
        var view = BuildWithContent(
            [Conv("c-1", "Gateway restart")],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Flattened.Count(r => r.Conversation.ConversationId == "c-1").ShouldBe(1);
        view.Groups.ShouldNotContain(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel);
    }

    [Fact]
    public void Content_rows_are_in_the_flattened_list_so_the_keyboard_reaches_them()
    {
        // A group bolted on at render time would be visible and unreachable by arrow key, which is
        // worse than not showing it at all. Flattened is what the keyboard walks.
        var view = BuildWithContent(
            [Conv("c-1", "Tuesday standup"), Conv("c-2", "Gateway notes")],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Flattened.Select(r => r.Conversation.ConversationId).ShouldBe(["c-2", "c-1"]);
    }

    [Fact]
    public void No_content_matches_leaves_the_view_exactly_as_it_was()
    {
        var withNone = BuildWithContent([Conv("c-1", "Gateway notes")], "gateway", []);
        var withNull = Build([Conv("c-1", "Gateway notes")], "gateway");

        withNone.Groups.Count.ShouldBe(withNull.Groups.Count);
        withNone.Flattened.Count.ShouldBe(withNull.Flattened.Count);
    }

    [Fact]
    public void A_content_match_for_a_conversation_the_client_does_not_know_is_ignored()
    {
        // The server searches every transcript; the roster in this client may not hold all of them
        // (another agent's conversation, one pruned locally). A row with no conversation behind it
        // could not be opened, so it must not be offered.
        var view = BuildWithContent([Conv("c-1", "Tuesday standup")], "gateway", [new ConversationContentMatch("c-999", "gateway")]);

        view.Flattened.ShouldNotContain(r => r.Conversation.ConversationId == "c-999");
    }

    // ── relevance order and agent attribution ────────────────────────────────

    [Fact]
    public void Content_rows_keep_the_servers_relevance_order()
    {
        // Hits arrive in bm25 order. Walking the roster instead re-orders them by whatever order
        // agents and their conversations happen to be in, discarding the only signal that says
        // which match is the good one.
        var view = BuildWithContent(
            [Conv("c-1", "First"), Conv("c-2", "Second"), Conv("c-3", "Third")],
            "gateway",
            [
                new ConversationContentMatch("c-3", "best"),
                new ConversationContentMatch("c-1", "middling"),
                new ConversationContentMatch("c-2", "worst"),
            ]);

        view.Groups.Single(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel)
            .Rows.Select(r => r.Conversation.ConversationId)
            .ShouldBe(["c-3", "c-1", "c-2"]);
    }

    [Fact]
    public void A_content_row_for_another_agent_is_marked_as_such()
    {
        // IsOtherAgent drives both the owning-agent label and the active-row styling. Reporting
        // another agent's conversation as the current agent's hides who owns it, and lets the row
        // be styled active for a conversation that is not the one on screen.
        var view = ConversationSwitcherModel.Build(
            "a-1",
            [
                Agent("a-1", "Alpha", false, Conv("c-1", "Mine")),
                Agent("a-2", "Beta", false, Conv("c-2", "Theirs")),
            ],
            SelectionSource.UserClick,
            null,
            "gateway",
            [new ConversationContentMatch("c-2", "the gateway restart lost its pid file")]);

        var row = view.Groups.Single(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel)
            .Rows.ShouldHaveSingleItem();
        row.AgentId.ShouldBe("a-2");
        row.AgentDisplayName.ShouldBe("Beta");
        row.IsOtherAgent.ShouldBeTrue();
    }

    [Fact]
    public void A_content_row_for_the_current_agent_is_not_marked_as_another_agents()
    {
        var view = BuildWithContent(
            [Conv("c-1", "Tuesday standup")],
            "gateway",
            [new ConversationContentMatch("c-1", "the gateway restart lost its pid file")]);

        view.Groups.Single(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel)
            .Rows.ShouldHaveSingleItem().IsOtherAgent.ShouldBeFalse();
    }

    [Fact]
    public void A_duplicated_hit_is_listed_once()
    {
        // The endpoint groups by conversation, but a defensive de-dupe here keeps a repeated id
        // from rendering the same row twice with different snippets.
        var view = BuildWithContent(
            [Conv("c-1", "Tuesday standup")],
            "gateway",
            [
                new ConversationContentMatch("c-1", "first"),
                new ConversationContentMatch("c-1", "second"),
            ]);

        view.Groups.Single(g => g.Label == ConversationSwitcherModel.FoundInMessagesLabel)
            .Rows.ShouldHaveSingleItem().Snippet.ShouldBe("first");
    }
}
