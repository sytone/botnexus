using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for the conversation cost projection that powers the Activity page's cost subsection
/// (#2898). These pin the ranking contract, the null-is-not-zero rule, the inherited dashboard
/// filters and the id-keyed navigation target without needing bUnit.
/// </summary>
/// <remarks>
/// The fixture deliberately reflects the measured live distribution the issue reports: an ~8,000x
/// spread across conversations, and the top four rows all automation rather than human chats. A
/// uniform fixture would let a filter test pass while proving nothing about ranking.
/// </remarks>
public sealed class ActivityCostProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static ConversationSummaryDto Conv(
        string id,
        string agentId = "alpha",
        string title = "Chat",
        string source = "Channel",
        string kind = "HumanAgent",
        DateTimeOffset? updatedAt = null) =>
        new(
            ConversationId: id,
            AgentId: agentId,
            Title: title,
            IsDefault: false,
            Status: "Active",
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: (updatedAt ?? Now).AddMinutes(-5),
            UpdatedAt: updatedAt ?? Now,
            Source: source,
            Kind: kind);

    private static ConversationCostDto Cost(
        string id,
        int sessions,
        int messages,
        int? compactions = 0,
        long? tokens = null) =>
        new(id, sessions, messages, compactions, tokens);

    /// <summary>
    /// The real-distribution fixture: the four most expensive conversations are all automation
    /// (cron / sub-agent / agent-to-agent), exactly as measured on the live instance, with a human
    /// conversation an order of magnitude cheaper below them.
    /// </summary>
    private static IReadOnlyList<ConversationSummaryDto> RealisticConversations() =>
    [
        Conv("cron-nightly", source: "Cron", title: "Nightly maintenance"),
        Conv("cron-hourly", source: "Cron", title: "Hourly sweep"),
        Conv("sub-run", source: "Agent", kind: "AgentSubAgent", title: "Sub-agent run"),
        Conv("a2a-peer", source: "Agent", kind: "AgentAgent", title: "Peer exchange"),
        Conv("human-chat", title: "Jon and Farnsworth"),
        Conv("human-quiet", title: "Quiet corner")
    ];

    private static IReadOnlyList<ConversationCostDto> RealisticCosts() =>
    [
        Cost("cron-nightly", sessions: 527, messages: 8_720_000, compactions: 28),
        Cost("cron-hourly", sessions: 210, messages: 3_100_000, compactions: 11),
        Cost("sub-run", sessions: 90, messages: 900_000, compactions: 4),
        Cost("a2a-peer", sessions: 40, messages: 400_000, compactions: 2),
        Cost("human-chat", sessions: 6, messages: 1_096, compactions: 0),
        Cost("human-quiet", sessions: 1, messages: 12, compactions: 0)
    ];

    private static ActivityDashboardFilter CronVisible() => new(IncludeCron: true);

    // ── AC1: ranking ───────────────────────────────────────────────────────

    /// <summary>
    /// AC1: the default ordering is by total accumulation, descending. Asserted on the whole
    /// ordered sequence, not merely on the top row, so a projection that happened to surface the
    /// right head with a scrambled tail still reddens.
    /// </summary>
    [Fact]
    public void Default_ordering_is_by_total_descending()
    {
        var rows = ActivityCostProjection.Project(
            RealisticConversations(), RealisticCosts(), CronVisible(), Now);

        rows.Select(r => r.ConversationId).ShouldBe(
            ["cron-nightly", "cron-hourly", "sub-run", "a2a-peer", "human-chat", "human-quiet"]);

        // The ordering really is monotonic, not merely the fixture's input order.
        rows.Select(r => r.MessageCount).ShouldBeInOrder(SortDirection.Descending);
    }

    /// <summary>
    /// AC1: every row carries the minimum column set the subsection must list. Reading each value
    /// off the projected row pins that the projection carries them, rather than the table
    /// re-deriving anything of its own.
    /// </summary>
    [Fact]
    public void Row_carries_agent_title_session_message_and_compaction_counts()
    {
        var rows = ActivityCostProjection.Project(
            RealisticConversations(), RealisticCosts(), CronVisible(), Now);

        var top = rows[0];
        top.OwningAgentId.ShouldBe("alpha");
        top.Row.Title.ShouldBe("Nightly maintenance");
        top.SessionCount.ShouldBe(527);
        top.MessageCount.ShouldBe(8_720_000);
        top.CompactionSummaryCount.ShouldBe(28);
    }

    /// <summary>
    /// AC1: a measured total ranks ahead of the message-count tie-break, and a conversation with NO
    /// measured total sorts LAST rather than as a zero - an unmeasured cost is unknown, not cheap.
    /// </summary>
    [Fact]
    public void Unmeasured_total_sorts_last_rather_than_as_zero()
    {
        IReadOnlyList<ConversationSummaryDto> conversations =
            [Conv("measured-small"), Conv("unmeasured-huge")];
        IReadOnlyList<ConversationCostDto> costs =
        [
            Cost("measured-small", sessions: 1, messages: 5, tokens: 10),
            // Far more messages, but no measured token total at all.
            Cost("unmeasured-huge", sessions: 900, messages: 5_000_000, tokens: null)
        ];

        var rows = ActivityCostProjection.Project(conversations, costs, new ActivityDashboardFilter(), Now);

        rows[0].ConversationId.ShouldBe("measured-small");
        rows[1].ConversationId.ShouldBe("unmeasured-huge");
        rows[1].TotalTokens.ShouldBeNull();
    }

    // ── AC2: shared classifier agreement ───────────────────────────────────

    /// <summary>
    /// AC2: the cost row and the main activity row agree on the label and the origin badge. Pins
    /// the AGREEMENT between the two surfaces rather than asserting each independently, so a change
    /// that shifted both in lockstep still passes while any divergence reddens.
    /// </summary>
    [Fact]
    public void Cost_rows_agree_with_the_main_activity_rows_on_label_and_origin_badge()
    {
        var conversations = RealisticConversations();
        var filter = CronVisible();

        var mainRows = ActivityDashboardProjection.Project(conversations, filter, Now)
            .ToDictionary(r => r.ConversationId, StringComparer.Ordinal);
        var costRows = ActivityCostProjection.Project(conversations, RealisticCosts(), filter, Now);

        costRows.Count.ShouldBe(mainRows.Count);
        foreach (var cost in costRows)
        {
            var main = mainRows[cost.ConversationId];

            ConversationLabel.DisplayTitle(cost.Row.Title, cost.Row.ConversationId, cost.Row.OwningAgentId)
                .ShouldBe(ConversationLabel.DisplayTitle(main.Title, main.ConversationId, main.OwningAgentId));

            ActivityDashboardProjection.OriginLabel(cost.Row)
                .ShouldBe(ActivityDashboardProjection.OriginLabel(main));
            ActivityDashboardProjection.OriginModifier(cost.Row)
                .ShouldBe(ActivityDashboardProjection.OriginModifier(main));
        }

        // Non-vacuity: the fixture really does exercise several distinct badges, so the agreement
        // above is not trivially "null equals null" on every row.
        costRows.Select(r => ActivityDashboardProjection.OriginLabel(r.Row))
            .Distinct()
            .Count()
            .ShouldBeGreaterThan(2);
    }

    // ── AC3: null is not zero ──────────────────────────────────────────────

    /// <summary>
    /// AC3: an unmeasured count is neither rendered nor aggregated as <c>0</c>. Pinned at both the
    /// model layer (the property stays null) and the render layer (the formatter emits the
    /// not-measured word), with a measured zero shown to render differently.
    /// </summary>
    [Fact]
    public void Unmeasured_count_is_not_rendered_or_modelled_as_zero()
    {
        IReadOnlyList<ConversationSummaryDto> conversations = [Conv("c1"), Conv("c2")];
        IReadOnlyList<ConversationCostDto> costs =
        [
            Cost("c1", sessions: 3, messages: 40, compactions: null),
            Cost("c2", sessions: 2, messages: 30, compactions: 0)
        ];

        var rows = ActivityCostProjection.Project(conversations, costs, new ActivityDashboardFilter(), Now)
            .ToDictionary(r => r.ConversationId, StringComparer.Ordinal);

        rows["c1"].CompactionSummaryCount.ShouldBeNull();
        rows["c2"].CompactionSummaryCount.ShouldBe(0);

        ActivityCostProjection.FormatCount(rows["c1"].CompactionSummaryCount)
            .ShouldBe(ActivityCostProjection.NotMeasured);
        ActivityCostProjection.FormatCount(rows["c2"].CompactionSummaryCount).ShouldBe("0");

        // The two must be DISTINGUISHABLE - that is the whole clause.
        ActivityCostProjection.FormatCount(rows["c1"].CompactionSummaryCount)
            .ShouldNotBe(ActivityCostProjection.FormatCount(rows["c2"].CompactionSummaryCount));
    }

    /// <summary>
    /// AC3 (sad path): a conversation the rollup does not mention at all keeps its unmeasurable
    /// fields null. Its session and message counts are genuinely 0 because the absence of a session
    /// row IS the measurement - the distinction the nullable model exists to preserve.
    /// </summary>
    [Fact]
    public void Conversation_absent_from_the_rollup_reports_null_not_zero_for_unmeasured_fields()
    {
        IReadOnlyList<ConversationSummaryDto> conversations = [Conv("orphan")];

        var rows = ActivityCostProjection.Project(
            conversations, Array.Empty<ConversationCostDto>(), new ActivityDashboardFilter(), Now);

        var row = rows.ShouldHaveSingleItem();
        row.CompactionSummaryCount.ShouldBeNull();
        row.TotalTokens.ShouldBeNull();
        row.SessionCount.ShouldBe(0);
        row.MessageCount.ShouldBe(0);
    }

    // ── AC4: inherited filters change the ranking ──────────────────────────

    /// <summary>
    /// AC4: filtering out cron conversations produces a MATERIALLY different top row, using the
    /// fixture that reflects the real distribution (the top four are all automation). This is the
    /// clause that proves the subsection inherits the dashboard's facets rather than ranking a
    /// fixed global set.
    /// </summary>
    [Fact]
    public void Hiding_cron_conversations_changes_the_top_row()
    {
        var conversations = RealisticConversations();
        var costs = RealisticCosts();

        var withCron = ActivityCostProjection.Project(conversations, costs, CronVisible(), Now);
        // The default filter excludes cron, matching the dashboard's own default.
        var withoutCron = ActivityCostProjection.Project(conversations, costs, new ActivityDashboardFilter(), Now);

        withCron[0].ConversationId.ShouldBe("cron-nightly");
        withoutCron[0].ConversationId.ShouldBe("sub-run");
        withoutCron[0].ConversationId.ShouldNotBe(withCron[0].ConversationId);

        withoutCron.Select(r => r.ConversationId).ShouldNotContain("cron-nightly");
        withoutCron.Select(r => r.ConversationId).ShouldNotContain("cron-hourly");
    }

    /// <summary>
    /// AC4: the agent, origin and recency facets are inherited too - not just the cron toggle.
    /// </summary>
    [Fact]
    public void Agent_origin_and_recency_facets_are_inherited()
    {
        IReadOnlyList<ConversationSummaryDto> conversations =
        [
            Conv("a-recent", agentId: "alpha", updatedAt: Now),
            Conv("b-old", agentId: "beta", updatedAt: Now.AddDays(-40)),
            Conv("b-sub", agentId: "beta", source: "Agent", kind: "AgentSubAgent", updatedAt: Now)
        ];
        IReadOnlyList<ConversationCostDto> costs =
        [
            Cost("a-recent", 1, 10), Cost("b-old", 1, 900), Cost("b-sub", 1, 500)
        ];

        ActivityCostProjection
            .Project(conversations, costs, new ActivityDashboardFilter(AgentId: "beta"), Now)
            .Select(r => r.ConversationId)
            .ShouldBe(["b-old", "b-sub"]);

        ActivityCostProjection
            .Project(conversations, costs, new ActivityDashboardFilter(Origin: ActivityOriginFilter.SubAgent), Now)
            .Select(r => r.ConversationId)
            .ShouldBe(["b-sub"]);

        ActivityCostProjection
            .Project(conversations, costs, new ActivityDashboardFilter(Recency: ActivityRecencyWindow.Week), Now)
            .Select(r => r.ConversationId)
            .ShouldBe(["b-sub", "a-recent"]);
    }

    // ── AC5: navigation keyed on the row's own id ──────────────────────────

    /// <summary>
    /// AC5: the navigation target still matches the row's own conversation id after a re-sort. The
    /// two orderings are computed from the SAME rows so a position-derived target would necessarily
    /// disagree between them.
    /// </summary>
    [Fact]
    public void Navigation_target_matches_the_rows_own_id_after_a_resort()
    {
        var conversations = RealisticConversations();
        var costs = RealisticCosts();

        var ranked = ActivityCostProjection.Project(conversations, costs, CronVisible(), Now);
        var resorted = ranked.OrderBy(r => r.ConversationId, StringComparer.Ordinal).ToList();

        // The re-sort really did move things, otherwise this proves nothing.
        resorted.Select(r => r.ConversationId).ShouldNotBe(ranked.Select(r => r.ConversationId).ToList());

        foreach (var row in resorted)
        {
            ActivityCostProjection.NavigationTarget(row)
                .ShouldBe($"/chat/{Uri.EscapeDataString(row.OwningAgentId)}/{Uri.EscapeDataString(row.ConversationId)}");
            ActivityCostProjection.NavigationTarget(row).ShouldContain(row.ConversationId);
        }

        // And a specific row's target is unchanged by the re-sort.
        var byId = ranked.ToDictionary(r => r.ConversationId, ActivityCostProjection.NavigationTarget, StringComparer.Ordinal);
        foreach (var row in resorted)
            ActivityCostProjection.NavigationTarget(row).ShouldBe(byId[row.ConversationId]);
    }

    /// <summary>
    /// Sad path: a duplicated conversation id in the rollup yields exactly one row, matching the
    /// first-stamped-wins de-duplication rule the involved-agent derivation already uses.
    /// </summary>
    [Fact]
    public void Duplicate_cost_rows_yield_a_single_row()
    {
        IReadOnlyList<ConversationSummaryDto> conversations = [Conv("dup")];
        IReadOnlyList<ConversationCostDto> costs =
        [
            Cost("dup", sessions: 4, messages: 100),
            Cost("dup", sessions: 999, messages: 999)
        ];

        var row = ActivityCostProjection
            .Project(conversations, costs, new ActivityDashboardFilter(), Now)
            .ShouldHaveSingleItem();

        row.SessionCount.ShouldBe(4);
        row.MessageCount.ShouldBe(100);
    }

    /// <summary>
    /// Sad path: null arguments are rejected rather than silently producing an empty ranking, which
    /// would read as "nothing costs anything".
    /// </summary>
    [Fact]
    public void Null_arguments_are_rejected()
    {
        Should.Throw<ArgumentNullException>(() =>
            ActivityCostProjection.Project(null!, [], new ActivityDashboardFilter(), Now));
        Should.Throw<ArgumentNullException>(() =>
            ActivityCostProjection.Project([], null!, new ActivityDashboardFilter(), Now));
        Should.Throw<ArgumentNullException>(() =>
            ActivityCostProjection.Project([], [], null!, Now));
        Should.Throw<ArgumentNullException>(() => ActivityCostProjection.NavigationTarget(null!));
    }
}
