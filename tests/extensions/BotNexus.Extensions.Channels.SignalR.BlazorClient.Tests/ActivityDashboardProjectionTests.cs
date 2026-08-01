using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for the pure <see cref="ActivityDashboard"/> projection that powers the Home /
/// Activity dashboard. These cover cron default-exclude, involved-agent derivation, and the
/// composable status / agent / recency filters without needing bUnit.
/// </summary>
public sealed class ActivityDashboardProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static ConversationSummaryDto Conv(
        string id,
        string agentId = "alpha",
        string title = "Chat",
        string status = "Active",
        string? activeSessionId = null,
        int bindingCount = 0,
        DateTimeOffset? updatedAt = null,
        IReadOnlyList<ParticipantDto>? participants = null,
        // #2305 (epic #2300): cron-ness comes from the SERVER-stamped source field, never from a
        // `cron:`-prefixed session id. Fixtures set it explicitly.
        string source = "Channel",
        string kind = "HumanAgent") =>
        new(
            ConversationId: id,
            AgentId: agentId,
            Title: title,
            IsDefault: false,
            Status: status,
            ActiveSessionId: activeSessionId,
            BindingCount: bindingCount,
            CreatedAt: (updatedAt ?? Now).AddMinutes(-5),
            UpdatedAt: updatedAt ?? Now,
            Source: source,
            Kind: kind,
            Participants: participants);

    // ── Cron detection ─────────────────────────────────────────────────────

    /// <summary>
    /// #2305: cron-ness is read from the authoritative server-stamped source, on any conversation id.
    /// </summary>
    [Fact]
    public void IsCronConversation_true_for_server_stamped_cron_source()
    {
        var conv = Conv("c1", source: "Cron");
        Assert.True(ActivityDashboardProjection.IsCronConversation(conv));
    }

    /// <summary>
    /// #2305 regression guard: a `cron:`-prefixed SESSION id is no longer evidence about the
    /// CONVERSATION. Only the typed source decides. This is the inference that was deleted.
    /// </summary>
    [Fact]
    public void IsCronConversation_false_for_cron_session_prefix_without_cron_source()
    {
        var conv = Conv("c1", activeSessionId: "cron:job-1:20260710");
        Assert.False(ActivityDashboardProjection.IsCronConversation(conv));
    }

    [Fact]
    public void IsCronConversation_false_for_normal_conversation()
    {
        var conv = Conv("c1", activeSessionId: "signal:+123");
        Assert.False(ActivityDashboardProjection.IsCronConversation(conv));
    }

    /// <summary>
    /// Tolerant parsing: an unknown source from a newer server degrades to Channel, not cron.
    /// </summary>
    [Fact]
    public void IsCronConversation_false_for_unknown_future_source()
    {
        var conv = Conv("c1", source: "SomethingNewerServerSent");
        Assert.False(ActivityDashboardProjection.IsCronConversation(conv));
    }

    // ── Cron default-exclude + toggle ──────────────────────────────────────

    [Fact]
    public void Cron_conversations_excluded_by_default()
    {
        var conversations = new[]
        {
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", source: "Cron")
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Single(rows);
        Assert.Equal("Normal", rows[0].Title);
    }

    [Fact]
    public void Cron_conversations_included_when_toggle_on()
    {
        var conversations = new[]
        {
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", source: "Cron")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(IncludeCron: true),
            Now);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.IsCron && r.Title == "Scheduled");
    }

    // ── Involved agents ────────────────────────────────────────────────────

    [Fact]
    public void InvolvedAgents_unions_owner_and_agent_participants()
    {
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "beta", "peer"),
            new ParticipantDto("User", "jon", "initiator"),
            new ParticipantDto("Agent", "gamma", "sub")
        });

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, agents);
        Assert.DoesNotContain("jon", agents);
    }

    [Fact]
    public void InvolvedAgents_deduplicates_owner_appearing_as_participant()
    {
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "alpha", "initiator"),
            new ParticipantDto("Agent", "beta", "peer")
        });

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha", "beta" }, agents);
    }

    [Fact]
    public void InvolvedAgents_owner_only_when_no_participants()
    {
        var conv = Conv("c1", agentId: "alpha", participants: null);

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha" }, agents);
    }

    [Fact]
    public void Project_row_carries_all_involved_agents()
    {
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "beta", "peer")
        });

        var rows = ActivityDashboardProjection.Project(new[] { conv }, new ActivityDashboardFilter(), Now);

        Assert.Equal(new[] { "alpha", "beta" }, rows[0].InvolvedAgents);
    }

    // ── Status filter ──────────────────────────────────────────────────────

    [Fact]
    public void Status_filter_active_excludes_archived()
    {
        var conversations = new[]
        {
            Conv("c1", status: "Active"),
            Conv("c2", status: "Archived")
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Single(rows);
        Assert.Equal("c1", rows[0].ConversationId);
    }

    [Fact]
    public void Status_filter_archived_returns_only_archived()
    {
        var conversations = new[]
        {
            Conv("c1", status: "Active"),
            Conv("c2", status: "Archived")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Status: ActivityStatusFilter.Archived),
            Now);

        Assert.Single(rows);
        Assert.Equal("c2", rows[0].ConversationId);
    }

    [Fact]
    public void Status_filter_all_returns_both()
    {
        var conversations = new[]
        {
            Conv("c1", status: "Active"),
            Conv("c2", status: "Archived")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Status: ActivityStatusFilter.All),
            Now);

        Assert.Equal(2, rows.Count);
    }

    // ── Agent filter ───────────────────────────────────────────────────────

    [Fact]
    public void Agent_filter_matches_owner_or_participant()
    {
        var conversations = new[]
        {
            Conv("c1", agentId: "alpha"),
            Conv("c2", agentId: "beta", participants: new[] { new ParticipantDto("Agent", "alpha", "peer") }),
            Conv("c3", agentId: "gamma")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(AgentId: "alpha"),
            Now);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ConversationId == "c1");
        Assert.Contains(rows, r => r.ConversationId == "c2");
    }

    // ── Recency filter ─────────────────────────────────────────────────────

    [Fact]
    public void Recency_week_excludes_older_than_seven_days()
    {
        var conversations = new[]
        {
            Conv("recent", updatedAt: Now.AddDays(-3)),
            Conv("old", updatedAt: Now.AddDays(-30))
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Recency: ActivityRecencyWindow.Week),
            Now);

        Assert.Single(rows);
        Assert.Equal("recent", rows[0].ConversationId);
    }

    [Fact]
    public void Recency_any_includes_everything()
    {
        var conversations = new[]
        {
            Conv("recent", updatedAt: Now.AddDays(-3)),
            Conv("old", updatedAt: Now.AddDays(-300))
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(2, rows.Count);
    }

    // ── Ordering + edge cases ──────────────────────────────────────────────

    [Fact]
    public void Rows_ordered_by_most_recent_activity_first()
    {
        var conversations = new[]
        {
            Conv("older", updatedAt: Now.AddHours(-5)),
            Conv("newest", updatedAt: Now.AddHours(-1)),
            Conv("middle", updatedAt: Now.AddHours(-3))
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(new[] { "newest", "middle", "older" }, rows.Select(r => r.ConversationId));
    }

    [Fact]
    public void Empty_input_yields_empty_projection()
    {
        var rows = ActivityDashboardProjection.Project(
            Array.Empty<ConversationSummaryDto>(),
            new ActivityDashboardFilter(),
            Now);

        Assert.Empty(rows);
    }

    [Fact]
    public void Blank_title_falls_back_to_untitled()
    {
        var rows = ActivityDashboardProjection.Project(
            new[] { Conv("c1", title: "  ") },
            new ActivityDashboardFilter(),
            Now);

        Assert.Equal("(untitled)", rows[0].Title);
    }

    [Fact]
    public void Project_throws_on_null_conversations()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ActivityDashboardProjection.Project(null!, new ActivityDashboardFilter(), Now));
    }

    // ── Summary strip ──────────────────────────────────────────────────────

    [Fact]
    public void Summarize_empty_rows_yields_zeros_and_null_freshness()
    {
        var summary = ActivityDashboardProjection.Summarize(Array.Empty<ActivityRow>());

        Assert.Equal(0, summary.ConversationCount);
        Assert.Equal(0, summary.AgentCount);
        Assert.Equal(0, summary.ScheduledCount);
        Assert.Null(summary.LatestActivity);
    }

    [Fact]
    public void Summarize_counts_conversations_and_latest_activity()
    {
        var rows = ActivityDashboardProjection.Project(
            new[]
            {
                Conv("a", updatedAt: Now.AddHours(-2)),
                Conv("b", updatedAt: Now.AddHours(-1))
            },
            new ActivityDashboardFilter(),
            Now);

        var summary = ActivityDashboardProjection.Summarize(rows);

        Assert.Equal(2, summary.ConversationCount);
        Assert.Equal(Now.AddHours(-1), summary.LatestActivity);
    }

    [Fact]
    public void Summarize_counts_distinct_agents_across_multi_agent_conversations()
    {
        var rows = ActivityDashboardProjection.Project(
            new[]
            {
                Conv("a", agentId: "alpha", participants: new[] { new ParticipantDto("Agent", "beta", "peer") }),
                Conv("b", agentId: "alpha")
            },
            new ActivityDashboardFilter(),
            Now);

        var summary = ActivityDashboardProjection.Summarize(rows);

        // alpha appears in both rows and beta in one -> 2 distinct agents.
        Assert.Equal(2, summary.AgentCount);
    }

    [Fact]
    public void Summarize_counts_scheduled_rows()
    {
        var rows = ActivityDashboardProjection.Project(
            new[]
            {
                Conv("a"),
                Conv("b", source: "Cron")
            },
            new ActivityDashboardFilter(IncludeCron: true),
            Now);

        var summary = ActivityDashboardProjection.Summarize(rows);

        Assert.Equal(2, summary.ConversationCount);
        Assert.Equal(1, summary.ScheduledCount);
    }

    [Fact]
    public void Summarize_throws_on_null_rows()
    {
        Assert.Throws<ArgumentNullException>(() => ActivityDashboardProjection.Summarize(null!));
    }
    // ── Origin badges (#2385, epic #2300) ──────────────────────────────────

    private static ActivityRow Row(string source, string kind) =>
        ActivityDashboardProjection.Project(
            new[] { Conv("a", source: source, kind: kind) },
            new ActivityDashboardFilter(IncludeCron: true),
            Now).Single();

    [Fact]
    public void Project_carries_the_typed_source_and_kind_onto_the_row()
    {
        var row = Row("Webhook", "AgentSubAgent");

        Assert.Equal(ConversationSource.Webhook, row.Source);
        Assert.Equal(ConversationKind.AgentSubAgent, row.Kind);
    }

    [Fact]
    public void IsCron_is_computed_from_source_so_it_cannot_disagree_with_it()
    {
        Assert.True(Row("Cron", "HumanAgent").IsCron);
        Assert.False(Row("Webhook", "HumanAgent").IsCron);
        Assert.False(Row("Agent", "AgentSubAgent").IsCron);
        Assert.False(Row("Channel", "HumanAgent").IsCron);
    }

    [Theory]
    // The ordinary human-on-a-channel case is the ONLY unbadged combination: badges must carry
    // signal, not decorate every row.
    [InlineData("Channel", "HumanAgent", null, null)]
    [InlineData("Cron", "HumanAgent", "Scheduled", "cron")]
    [InlineData("Webhook", "HumanAgent", "Webhook", "webhook")]
    [InlineData("Agent", "AgentSubAgent", "Sub-agent", "subagent")]
    [InlineData("Agent", "AgentAgent", "Agent-to-agent", "a2a")]
    // Source=Agent is deliberately coarse server-side; a default kind still deserves a badge
    // because an agent minting its own conversation is not the ordinary case.
    [InlineData("Agent", "HumanAgent", "Agent-initiated", "agent")]
    // A non-default kind on a channel-sourced conversation is the surprising part, so it badges
    // on kind even though the trigger was ordinary.
    [InlineData("Channel", "AgentSubAgent", "Sub-agent", "subagent")]
    [InlineData("Channel", "AgentAgent", "Agent-to-agent", "a2a")]
    public void OriginLabel_and_modifier_disambiguate_every_source_kind_pair(
        string source, string kind, string? expectedLabel, string? expectedModifier)
    {
        var row = Row(source, kind);

        Assert.Equal(expectedLabel, ActivityDashboardProjection.OriginLabel(row));
        Assert.Equal(expectedModifier, ActivityDashboardProjection.OriginModifier(row));
    }

    [Fact]
    public void Unknown_wire_values_degrade_to_the_unbadged_back_compat_default()
    {
        // A client older than its server must not render a bogus badge for a source/kind it does
        // not know: tolerant parsing falls back to Channel/HumanAgent, which is unbadged.
        var row = Row("SomethingNewerServerSideEntirely", "AlsoBrandNew");

        Assert.Equal(ConversationSource.Channel, row.Source);
        Assert.Equal(ConversationKind.HumanAgent, row.Kind);
        Assert.Null(ActivityDashboardProjection.OriginLabel(row));
    }

    [Fact]
    public void OriginLabel_and_modifier_throw_on_null_row()
    {
        Assert.Throws<ArgumentNullException>(() => ActivityDashboardProjection.OriginLabel(null!));
        Assert.Throws<ArgumentNullException>(() => ActivityDashboardProjection.OriginModifier(null!));
    }
    // ── Origin facet (#2385) ───────────────────────────────────────────────

    private static ConversationSummaryDto OriginConv(string id, string source, string kind = "HumanAgent",
        string status = "Active", DateTimeOffset? updatedAt = null) =>
        Conv(id, source: source, kind: kind, status: status, updatedAt: updatedAt);

    /// <summary>
    /// The default facet must be a no-op: adding Origin to the filter record cannot change what the
    /// existing landing view shows.
    /// </summary>
    [Fact]
    public void Origin_facet_defaults_to_all_and_changes_nothing()
    {
        Assert.Equal(ActivityOriginFilter.All, new ActivityDashboardFilter().Origin);

        var conversations = new[]
        {
            OriginConv("c1", "Channel"),
            OriginConv("c2", "Webhook"),
            OriginConv("c3", "Agent", "AgentSubAgent"),
            OriginConv("c4", "Agent", "AgentAgent"),
            OriginConv("c5", "Agent")
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(5, rows.Count);
    }

    [Theory]
    [InlineData(ActivityOriginFilter.Human, new[] { "c1" })]
    [InlineData(ActivityOriginFilter.Webhook, new[] { "c2" })]
    [InlineData(ActivityOriginFilter.SubAgent, new[] { "c3" })]
    [InlineData(ActivityOriginFilter.AgentToAgent, new[] { "c4" })]
    [InlineData(ActivityOriginFilter.Agent, new[] { "c5" })]
    public void Origin_facet_selects_exactly_the_matching_rows(
        ActivityOriginFilter origin, string[] expected)
    {
        var conversations = new[]
        {
            OriginConv("c1", "Channel"),
            OriginConv("c2", "Webhook"),
            OriginConv("c3", "Agent", "AgentSubAgent"),
            OriginConv("c4", "Agent", "AgentAgent"),
            OriginConv("c5", "Agent")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: origin), Now);

        Assert.Equal(expected, rows.Select(r => r.ConversationId).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// The facet keys off the same classifier the badge renders, so a channel-sourced conversation
    /// with a sub-agent pairing (badged "Sub-agent") filters as a sub-agent - the filter cannot
    /// disagree with what the reader sees.
    /// </summary>
    [Fact]
    public void Origin_facet_agrees_with_the_rendered_badge_for_kind_carried_rows()
    {
        var conversations = new[]
        {
            OriginConv("channel-plain", "Channel"),
            OriginConv("channel-sub", "Channel", "AgentSubAgent")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: ActivityOriginFilter.SubAgent), Now);

        Assert.Single(rows);
        Assert.Equal("channel-sub", rows[0].ConversationId);
        Assert.Equal("subagent", ActivityDashboardProjection.OriginModifier(rows[0]));
    }

    /// <summary>
    /// Origin does NOT override the cron default-exclude: selecting the scheduled origin without the
    /// cron toggle still yields nothing, because the facets compose rather than replace each other.
    /// </summary>
    [Fact]
    public void Origin_scheduled_still_respects_the_cron_default_exclude()
    {
        var conversations = new[] { OriginConv("c1", "Channel"), OriginConv("cron1", "Cron") };

        var excluded = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: ActivityOriginFilter.Scheduled), Now);
        Assert.Empty(excluded);

        var included = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(IncludeCron: true, Origin: ActivityOriginFilter.Scheduled),
            Now);
        Assert.Single(included);
        Assert.Equal("cron1", included[0].ConversationId);
    }

    [Fact]
    public void Origin_facet_composes_with_the_status_facet_rather_than_replacing_it()
    {
        var conversations = new[]
        {
            OriginConv("hook-active", "Webhook"),
            OriginConv("hook-archived", "Webhook", status: "Archived"),
            OriginConv("chat-archived", "Channel", status: "Archived")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Status: ActivityStatusFilter.Archived,
                                        Origin: ActivityOriginFilter.Webhook),
            Now);

        Assert.Single(rows);
        Assert.Equal("hook-archived", rows[0].ConversationId);
    }

    [Fact]
    public void Origin_facet_composes_with_the_recency_facet_rather_than_replacing_it()
    {
        var conversations = new[]
        {
            OriginConv("hook-recent", "Webhook", updatedAt: Now.AddDays(-2)),
            OriginConv("hook-stale", "Webhook", updatedAt: Now.AddDays(-40)),
            OriginConv("chat-recent", "Channel", updatedAt: Now.AddDays(-2))
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Recency: ActivityRecencyWindow.Week,
                                        Origin: ActivityOriginFilter.Webhook),
            Now);

        Assert.Single(rows);
        Assert.Equal("hook-recent", rows[0].ConversationId);
    }

    [Fact]
    public void Origin_facet_composes_with_the_agent_facet_rather_than_replacing_it()
    {
        var conversations = new[]
        {
            Conv("hook-alpha", agentId: "alpha", source: "Webhook"),
            Conv("hook-beta", agentId: "beta", source: "Webhook")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(AgentId: "beta", Origin: ActivityOriginFilter.Webhook),
            Now);

        Assert.Single(rows);
        Assert.Equal("hook-beta", rows[0].ConversationId);
    }

    /// <summary>Sad path: a facet with no matching rows yields an empty projection, not everything.</summary>
    [Fact]
    public void Origin_facet_with_no_matching_rows_yields_an_empty_projection()
    {
        var conversations = new[] { OriginConv("c1", "Channel"), OriginConv("c2", "Channel") };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: ActivityOriginFilter.Webhook), Now);

        Assert.Empty(rows);
    }

    /// <summary>
    /// An unknown wire source degrades to the unbadged Channel default, so it must be reachable via
    /// the Human facet and invisible to every badged facet - never silently dropped from all of them.
    /// </summary>
    [Fact]
    public void Unknown_wire_source_filters_as_the_unbadged_human_origin()
    {
        var conversations = new[] { OriginConv("mystery", "SomethingNewerServerSide", "AlsoBrandNew") };

        Assert.Single(ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: ActivityOriginFilter.Human), Now));
        Assert.Empty(ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Origin: ActivityOriginFilter.Agent), Now));
    }

    // ---- Pin state (#2619) ------------------------------------------------

    /// <summary>
    /// Fixture for the pin facet: the pin state is server-stamped on the DTO, so the fixture sets
    /// <c>isPinned</c>/<c>pinnedAt</c> explicitly rather than letting the projection infer it.
    /// </summary>
    private static ConversationSummaryDto PinConv(
        string id,
        bool isPinned = false,
        DateTimeOffset? pinnedAt = null,
        DateTimeOffset? updatedAt = null,
        string agentId = "alpha",
        string status = "Active",
        string source = "Channel") =>
        new(
            ConversationId: id,
            AgentId: agentId,
            Title: id,
            IsDefault: false,
            Status: status,
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: (updatedAt ?? Now).AddMinutes(-5),
            UpdatedAt: updatedAt ?? Now,
            Source: source,
            Kind: "HumanAgent",
            IsPinned: isPinned,
            PinnedAt: pinnedAt,
            Participants: null);

    /// <summary>
    /// AC1: the row carries the server-stamped pin state straight through from the DTO. A pinned DTO
    /// produces a pinned row and an unpinned DTO does not - no inference, no derived rule.
    /// </summary>
    [Fact]
    public void Project_carries_pin_state_from_the_dto()
    {
        var pinnedAt = Now.AddHours(-3);
        var conversations = new[]
        {
            PinConv("pinned", isPinned: true, pinnedAt: pinnedAt, updatedAt: Now),
            PinConv("loose", isPinned: false, updatedAt: Now.AddMinutes(-1))
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        var pinned = rows.Single(r => r.ConversationId == "pinned");
        var loose = rows.Single(r => r.ConversationId == "loose");

        Assert.True(pinned.IsPinned);
        Assert.Equal(pinnedAt, pinned.PinnedAt);
        Assert.False(loose.IsPinned);
        Assert.Null(loose.PinnedAt);
    }

    /// <summary>
    /// AC2: pinned rows sort ahead of unpinned rows even when the pinned row is staler. This is the
    /// whole point of the feature - the user's explicit signal outranks machine-derived recency.
    /// </summary>
    [Fact]
    public void Project_orders_pinned_rows_ahead_of_newer_unpinned_rows()
    {
        var conversations = new[]
        {
            PinConv("fresh-unpinned", isPinned: false, updatedAt: Now),
            PinConv("stale-pinned", isPinned: true, pinnedAt: Now, updatedAt: Now.AddDays(-9))
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Recency: ActivityRecencyWindow.Any), Now);

        Assert.Equal(new[] { "stale-pinned", "fresh-unpinned" }, rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC3: the pre-existing ordering contract inside the unpinned group is untouched -
    /// <c>UpdatedAt</c> descending, then <c>ConversationId</c> ordinal on a tie.
    /// </summary>
    [Fact]
    public void Project_preserves_existing_ordering_within_the_unpinned_group()
    {
        var tie = Now.AddHours(-1);
        var conversations = new[]
        {
            PinConv("b-tie", updatedAt: tie),
            PinConv("older", updatedAt: Now.AddHours(-5)),
            PinConv("a-tie", updatedAt: tie),
            PinConv("newest", updatedAt: Now)
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(
            new[] { "newest", "a-tie", "b-tie", "older" },
            rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC3 (pinned half): the same <c>UpdatedAt</c> descending / <c>ConversationId</c> ordinal rule
    /// applies inside the pinned group. Pinned-first is a grouping, not a second ordering rule.
    /// </summary>
    [Fact]
    public void Project_preserves_existing_ordering_within_the_pinned_group()
    {
        var tie = Now.AddHours(-1);
        var conversations = new[]
        {
            PinConv("p-b-tie", isPinned: true, updatedAt: tie),
            PinConv("p-older", isPinned: true, updatedAt: Now.AddHours(-5)),
            PinConv("p-a-tie", isPinned: true, updatedAt: tie),
            PinConv("p-newest", isPinned: true, updatedAt: Now),
            PinConv("unpinned", updatedAt: Now)
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(
            new[] { "p-newest", "p-a-tie", "p-b-tie", "p-older", "unpinned" },
            rows.Select(r => r.ConversationId));
    }

    /// <summary>The pin facet is inert by default, so the existing landing view is unchanged.</summary>
    [Fact]
    public void Pin_facet_defaults_to_all_and_shows_both_pinned_and_unpinned()
    {
        var conversations = new[]
        {
            PinConv("pinned", isPinned: true),
            PinConv("loose")
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);

        Assert.Equal(ActivityPinFilter.All, new ActivityDashboardFilter().Pinned);
        Assert.Equal(2, rows.Count);
    }

    /// <summary>The pinned-only facet selects exactly the pinned rows.</summary>
    [Fact]
    public void Pin_facet_pinned_only_selects_pinned_rows()
    {
        var conversations = new[]
        {
            PinConv("pinned", isPinned: true),
            PinConv("loose")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Pinned: ActivityPinFilter.Pinned), Now);

        Assert.Equal(new[] { "pinned" }, rows.Select(r => r.ConversationId));
    }

    /// <summary>The unpinned facet is the exact complement - a tri-state, not a one-way toggle.</summary>
    [Fact]
    public void Pin_facet_unpinned_only_selects_unpinned_rows()
    {
        var conversations = new[]
        {
            PinConv("pinned", isPinned: true),
            PinConv("loose")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Pinned: ActivityPinFilter.Unpinned), Now);

        Assert.Equal(new[] { "loose" }, rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC5: the pin facet composes with the agent facet - the result is the intersection, proving
    /// neither facet was reworked to accommodate the other.
    /// </summary>
    [Fact]
    public void Pin_facet_composes_with_the_agent_facet()
    {
        var conversations = new[]
        {
            PinConv("alpha-pinned", isPinned: true, agentId: "alpha"),
            PinConv("beta-pinned", isPinned: true, agentId: "beta"),
            PinConv("alpha-loose", agentId: "alpha")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(AgentId: "alpha", Pinned: ActivityPinFilter.Pinned),
            Now);

        Assert.Equal(new[] { "alpha-pinned" }, rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC5: the pin facet composes with the cron default-exclude and the status facet. A pinned
    /// cron conversation stays hidden until cron is revealed - pin must not become an override.
    /// </summary>
    [Fact]
    public void Pin_facet_composes_with_the_cron_and_status_facets()
    {
        var conversations = new[]
        {
            PinConv("pinned-cron", isPinned: true, source: "Cron"),
            PinConv("pinned-archived", isPinned: true, status: "Archived"),
            PinConv("pinned-active", isPinned: true)
        };

        var hidden = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Pinned: ActivityPinFilter.Pinned), Now);
        Assert.Equal(new[] { "pinned-active" }, hidden.Select(r => r.ConversationId));

        var withCron = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(IncludeCron: true, Pinned: ActivityPinFilter.Pinned),
            Now);
        Assert.Contains("pinned-cron", withCron.Select(r => r.ConversationId));

        var archivedOnly = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Status: ActivityStatusFilter.Archived, Pinned: ActivityPinFilter.Pinned),
            Now);
        Assert.Equal(new[] { "pinned-archived" }, archivedOnly.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC5: the pin facet composes with the recency window. A pinned but stale row is still excluded
    /// by a Today window - pinning changes ordering and selection, not the meaning of recency.
    /// </summary>
    [Fact]
    public void Pin_facet_composes_with_the_recency_facet()
    {
        var conversations = new[]
        {
            PinConv("pinned-stale", isPinned: true, updatedAt: Now.AddDays(-20)),
            PinConv("pinned-today", isPinned: true, updatedAt: Now)
        };

        var rows = ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(Recency: ActivityRecencyWindow.Week, Pinned: ActivityPinFilter.Pinned),
            Now);

        Assert.Equal(new[] { "pinned-today" }, rows.Select(r => r.ConversationId));
    }
}
