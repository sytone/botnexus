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
        string kind = "HumanAgent",
        // #2692: visibility is server-stamped and already on the wire; fixtures set it explicitly.
        string visibility = "UserFacing",
        // #3105: the originating job / registration id, server-stamped alongside source (#2121).
        string? sourceId = null,
        // #3204: the author's stated purpose, server-persisted and now declared on the client DTO.
        string? purpose = null) =>
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
            Visibility: visibility,
            Participants: participants,
            SourceId: sourceId,
            Purpose: purpose);

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

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, agents.Select(a => a.AgentId));
        Assert.DoesNotContain(agents, a => a.AgentId == "jon");
    }

    [Fact]
    public void InvolvedAgents_carries_initiator_and_target_roles_onto_the_right_agents()
    {
        // #2857: the exact shape AgentExchangeService/CrossWorldExchangeRouter stamp.
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "alpha", "initiator"),
            new ParticipantDto("Agent", "beta", "target")
        });

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha", "beta" }, agents.Select(a => a.AgentId));
        Assert.Equal("initiator", agents[0].Role);
        Assert.Equal("target", agents[1].Role);
        Assert.Equal("initiator", ActivityDashboardProjection.RoleModifier(agents[0]));
        Assert.Equal("target", ActivityDashboardProjection.RoleModifier(agents[1]));
    }

    [Fact]
    public void InvolvedAgents_fails_open_on_unrecognised_null_and_blank_roles()
    {
        // An unknown role must still be RETURNED and DISPLAYED - only the colour modifier declines.
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "alpha", "quartermaster"),
            new ParticipantDto("Agent", "beta", null),
            new ParticipantDto("Agent", "gamma", "   ")
        });

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, agents.Select(a => a.AgentId));
        Assert.Equal("quartermaster", agents[0].Role);
        Assert.Equal("quartermaster", ActivityDashboardProjection.RoleLabel(agents[0]));
        Assert.Null(ActivityDashboardProjection.RoleModifier(agents[0]));
        // Blank and absent both collapse to null so they render identically.
        Assert.Null(agents[1].Role);
        Assert.Null(agents[2].Role);
        Assert.Null(ActivityDashboardProjection.RoleLabel(agents[2]));
    }

    [Fact]
    public void Summarize_agent_count_ignores_roles()
    {
        // One agent playing two roles across two rows is still ONE agent on the stat strip.
        var conversations = new[]
        {
            Conv("c1", agentId: "alpha", participants: new[]
            {
                new ParticipantDto("Agent", "alpha", "initiator"),
                new ParticipantDto("Agent", "beta", "target")
            }),
            Conv("c2", agentId: "beta", participants: new[]
            {
                new ParticipantDto("Agent", "beta", "initiator"),
                new ParticipantDto("Agent", "alpha", "target")
            })
        };

        var rows = ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now);
        var summary = ActivityDashboardProjection.Summarize(rows);

        Assert.Equal(2, summary.ConversationCount);
        Assert.Equal(2, summary.AgentCount);
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

        Assert.Equal(new[] { "alpha", "beta" }, agents.Select(a => a.AgentId));
    }

    [Fact]
    public void InvolvedAgents_owner_only_when_no_participants()
    {
        var conv = Conv("c1", agentId: "alpha", participants: null);

        var agents = ActivityDashboardProjection.InvolvedAgents(conv);

        Assert.Equal(new[] { "alpha" }, agents.Select(a => a.AgentId));
        Assert.Null(agents[0].Role);
    }

    [Fact]
    public void Project_row_carries_all_involved_agents()
    {
        var conv = Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "beta", "peer")
        });

        var rows = ActivityDashboardProjection.Project(new[] { conv }, new ActivityDashboardFilter(), Now);

        Assert.Equal(new[] { "alpha", "beta" }, rows[0].InvolvedAgents.Select(a => a.AgentId));
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

    // ── Visibility (#2692) ─────────────────────────────────────────────────

    /// <summary>
    /// AC1: the row carries the typed visibility parsed via the existing
    /// <see cref="ConversationOrigin.ParseVisibility"/>.
    /// </summary>
    [Fact]
    public void Project_carries_typed_visibility_on_the_row()
    {
        var rows = ActivityDashboardProjection.Project(
            [Conv("c1", visibility: "InspectableReadOnly")],
            new ActivityDashboardFilter(),
            Now);

        Assert.Equal(ConversationVisibility.InspectableReadOnly, Assert.Single(rows).Visibility);
    }

    /// <summary>
    /// AC1: unknown / empty wire values degrade to <c>UserFacing</c>, never to hidden. Failing OPEN
    /// matters here: silently hiding a user's conversation is far worse than showing an
    /// unclassified one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomethingFromANewerServer")]
    public void Project_unknown_visibility_degrades_to_user_facing_and_is_kept(string wire)
    {
        var rows = ActivityDashboardProjection.Project(
            [Conv("c1", visibility: wire)],
            new ActivityDashboardFilter(),
            Now);

        var row = Assert.Single(rows);
        Assert.Equal(ConversationVisibility.UserFacing, row.Visibility);
        Assert.Equal("c1", row.ConversationId);
    }

    /// <summary>AC2: InternalHidden rows are dropped unconditionally.</summary>
    [Fact]
    public void Project_excludes_internal_hidden_conversations()
    {
        var rows = ActivityDashboardProjection.Project(
            [Conv("c1"), Conv("c2", visibility: "InternalHidden")],
            new ActivityDashboardFilter(),
            Now);

        Assert.Equal(["c1"], rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// AC2: no facet combination can reveal a hidden row. The enum's contract is "never rendered to
    /// a user", so exclusion is unconditional rather than a toggle.
    /// </summary>
    [Fact]
    public void Project_no_filter_combination_reveals_internal_hidden()
    {
        var hidden = Conv("h1", visibility: "InternalHidden");

        foreach (var status in Enum.GetValues<ActivityStatusFilter>())
        foreach (var recency in Enum.GetValues<ActivityRecencyWindow>())
        foreach (var origin in Enum.GetValues<ActivityOriginFilter>())
        foreach (var pinned in Enum.GetValues<ActivityPinFilter>())
        foreach (var includeCron in new[] { true, false })
        {
            var rows = ActivityDashboardProjection.Project(
                [hidden],
                new ActivityDashboardFilter(includeCron, null, status, recency, origin, pinned),
                Now);

            Assert.Empty(rows);
        }
    }

    /// <summary>AC3: InspectableReadOnly rows are retained (they are not InternalHidden).</summary>
    [Fact]
    public void Project_retains_inspectable_read_only_conversations()
    {
        var rows = ActivityDashboardProjection.Project(
            [Conv("c1", visibility: "InspectableReadOnly")],
            new ActivityDashboardFilter(),
            Now);

        Assert.Equal(["c1"], rows.Select(r => r.ConversationId));
    }

    /// <summary>AC3: the marker fires for InspectableReadOnly only, so it carries signal.</summary>
    [Fact]
    public void ReadOnlyLabel_marks_inspectable_and_leaves_user_facing_unmarked()
    {
        var inspectable = ActivityDashboardProjection.Project(
            [Conv("c1", visibility: "InspectableReadOnly")], new ActivityDashboardFilter(), Now)[0];
        var userFacing = ActivityDashboardProjection.Project(
            [Conv("c2")], new ActivityDashboardFilter(), Now)[0];

        Assert.Equal("Read-only", ActivityDashboardProjection.ReadOnlyLabel(inspectable));
        Assert.Null(ActivityDashboardProjection.ReadOnlyLabel(userFacing));
    }

    /// <summary>
    /// AC4: the strip and the table agree, because Summarize derives from the already-filtered row
    /// set. A hidden cron conversation must not inflate any of the three counts.
    /// </summary>
    [Fact]
    public void Summary_excludes_internal_hidden_rows_and_agrees_with_table()
    {
        var conversations = new[]
        {
            Conv("c1", agentId: "alpha"),
            Conv("h1", agentId: "ghost", visibility: "InternalHidden"),
            Conv("h2", agentId: "phantom", source: "Cron", visibility: "InternalHidden")
        };

        var rows = ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(IncludeCron: true), Now);
        var summary = ActivityDashboardProjection.Summarize(rows);

        Assert.Equal(rows.Count, summary.ConversationCount);
        Assert.Equal(1, summary.ConversationCount);
        Assert.Equal(1, summary.AgentCount);
        Assert.Equal(0, summary.ScheduledCount);
    }
    // ---- #1888: live (running-session) facet ------------------------------

    /// <summary>
    /// The row's liveness comes from the server-stamped ActiveSessionId, never inferred. A present
    /// id is live; absent is idle.
    /// </summary>
    [Fact]
    public void Project_carries_active_session_id_and_derives_is_live()
    {
        var rows = ActivityDashboardProjection.Project(
            new[] { Conv("c1", activeSessionId: "sess-1"), Conv("c2") },
            new ActivityDashboardFilter(),
            Now);

        var live = rows.Single(r => r.ConversationId == "c1");
        var idle = rows.Single(r => r.ConversationId == "c2");

        Assert.Equal("sess-1", live.ActiveSessionId);
        Assert.True(live.IsLive);
        Assert.Null(idle.ActiveSessionId);
        Assert.False(idle.IsLive);
    }

    /// <summary>
    /// A blank id is an ABSENT id. Treating whitespace as live would light the badge for a
    /// conversation with nothing running, which is the whole point of the signal.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_active_session_id_is_not_live(string sessionId)
    {
        var row = Assert.Single(ActivityDashboardProjection.Project(
            new[] { Conv("c1", activeSessionId: sessionId) }, new ActivityDashboardFilter(), Now));

        Assert.False(row.IsLive);
    }

    /// <summary>The live facet is inert by default: the landing view is unchanged.</summary>
    [Fact]
    public void Live_filter_defaults_to_all_and_is_inert()
    {
        Assert.Equal(ActivityLiveFilter.All, new ActivityDashboardFilter().Live);

        var rows = ActivityDashboardProjection.Project(
            new[] { Conv("c1", activeSessionId: "s"), Conv("c2") },
            new ActivityDashboardFilter(),
            Now);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Live_filter_selects_only_running_conversations()
    {
        var rows = ActivityDashboardProjection.Project(
            new[] { Conv("c1", activeSessionId: "s"), Conv("c2") },
            new ActivityDashboardFilter(Live: ActivityLiveFilter.Live),
            Now);

        Assert.Equal(new[] { "c1" }, rows.Select(r => r.ConversationId));
    }

    [Fact]
    public void Idle_filter_selects_only_non_running_conversations()
    {
        var rows = ActivityDashboardProjection.Project(
            new[] { Conv("c1", activeSessionId: "s"), Conv("c2") },
            new ActivityDashboardFilter(Live: ActivityLiveFilter.Idle),
            Now);

        Assert.Equal(new[] { "c2" }, rows.Select(r => r.ConversationId));
    }

    /// <summary>
    /// The live facet COMPOSES and overrides nothing: a live cron run stays hidden until the cron
    /// toggle reveals it, because running is a state, not a visibility override.
    /// </summary>
    [Fact]
    public void Live_filter_does_not_override_the_cron_default_exclude()
    {
        var conversations = new[] { Conv("cron1", activeSessionId: "s", source: "Cron") };

        Assert.Empty(ActivityDashboardProjection.Project(
            conversations, new ActivityDashboardFilter(Live: ActivityLiveFilter.Live), Now));

        Assert.Single(ActivityDashboardProjection.Project(
            conversations,
            new ActivityDashboardFilter(IncludeCron: true, Live: ActivityLiveFilter.Live),
            Now));
    }

    /// <summary>
    /// InternalHidden stays unconditionally excluded (#2692) - the live facet is not an escape hatch.
    /// </summary>
    [Fact]
    public void Live_filter_cannot_reveal_internal_hidden_conversations()
    {
        Assert.Empty(ActivityDashboardProjection.Project(
            new[] { Conv("h1", activeSessionId: "s", visibility: "InternalHidden") },
            new ActivityDashboardFilter(Live: ActivityLiveFilter.Live),
            Now));
    }

    /// <summary>
    /// The strip's live count mirrors the FILTERED row set, like every other stat - it counts what
    /// the table shows, not what the server returned.
    /// </summary>
    [Fact]
    public void Summarize_counts_live_rows_from_the_filtered_set()
    {
        var conversations = new[]
        {
            Conv("c1", activeSessionId: "s1"),
            Conv("c2", activeSessionId: "s2"),
            Conv("c3"),
            Conv("cron1", activeSessionId: "s4", source: "Cron")
        };

        var visible = ActivityDashboardProjection.Summarize(
            ActivityDashboardProjection.Project(conversations, new ActivityDashboardFilter(), Now));
        Assert.Equal(2, visible.LiveCount);

        var withCron = ActivityDashboardProjection.Summarize(
            ActivityDashboardProjection.Project(
                conversations, new ActivityDashboardFilter(IncludeCron: true), Now));
        Assert.Equal(3, withCron.LiveCount);
    }

    [Fact]
    public void Summarize_empty_rows_yields_zero_live_count()
    {
        Assert.Equal(0, ActivityDashboardProjection.Summarize(Array.Empty<ActivityRow>()).LiveCount);
    }

    // ── #3105: originator attribution (SourceId) ───────────────────────────

    private static ActivityRow ProjectOne(ConversationSummaryDto conv) =>
        ActivityDashboardProjection.Project(
            [conv], new ActivityDashboardFilter(IncludeCron: true), Now).Single();

    /// <summary>
    /// AC2: the server-stamped originator reaches the row. Without this the label has nothing to
    /// read and every other clause is vacuous.
    /// </summary>
    [Fact]
    public void Project_carries_the_server_stamped_source_id_onto_the_row()
    {
        var row = ProjectOne(Conv("c1", source: "Cron", sourceId: "daily-log-analysis"));

        Assert.Equal("daily-log-analysis", row.SourceId);
    }

    /// <summary>
    /// AC3 (positive, cron): a cron row carrying an originator is attributed verbatim.
    /// </summary>
    [Fact]
    public void SourceLabel_attributes_a_cron_row_to_its_job_id()
    {
        var row = ProjectOne(Conv("c1", source: "Cron", sourceId: "daily-log-analysis"));

        Assert.Equal("daily-log-analysis", ActivityDashboardProjection.SourceLabel(row));
    }

    /// <summary>
    /// AC3 (positive, webhook): the other source that names an originator registry. Asserted
    /// separately from cron because the two are independent arms of the guard - a fix that
    /// attributed only cron would pass a cron-only test.
    /// </summary>
    [Fact]
    public void SourceLabel_attributes_a_webhook_row_to_its_registration_id()
    {
        var row = ProjectOne(Conv("c1", source: "Webhook", sourceId: "wh_farnsworth_1"));

        Assert.Equal("wh_farnsworth_1", ActivityDashboardProjection.SourceLabel(row));
    }

    /// <summary>
    /// AC3 (negative, the load-bearing one): SourceId is meaningful ONLY paired with a source that
    /// names an originator registry. A value on a Channel- or Agent-sourced row is not attributable
    /// to anything a reader could look up, so rendering it would invite a lookup that cannot
    /// succeed. This is the clause that distinguishes "attribute the pair" from "print the field".
    /// </summary>
    [Theory]
    [InlineData("Channel")]
    [InlineData("Agent")]
    public void SourceLabel_refuses_to_attribute_a_row_whose_source_names_no_registry(string source)
    {
        var row = ProjectOne(Conv("c1", source: source, sourceId: "not-attributable"));

        Assert.Equal("not-attributable", row.SourceId);
        Assert.Null(ActivityDashboardProjection.SourceLabel(row));
    }

    /// <summary>
    /// AC3 / AC7: blank and absent must collapse to the same answer, or an empty element appears on
    /// rows the server declined to attribute. Whitespace is the case a null-check alone misses.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SourceLabel_is_null_when_the_originator_is_blank_or_absent(string? sourceId)
    {
        var row = ProjectOne(Conv("c1", source: "Cron", sourceId: sourceId));

        Assert.Null(ActivityDashboardProjection.SourceLabel(row));
    }

    /// <summary>
    /// AC4: an id at exactly the bound renders whole - the boundary a truncation off-by-one would
    /// break, and the case that proves the elision is not applied indiscriminately.
    /// </summary>
    [Fact]
    public void SourceLabel_renders_an_id_at_the_bound_untruncated()
    {
        var id = new string('j', ActivityDashboardProjection.SourceIdDisplayLength);
        var row = ProjectOne(Conv("c1", source: "Cron", sourceId: id));

        Assert.Equal(id, ActivityDashboardProjection.SourceLabel(row));
    }

    /// <summary>
    /// AC4: a longer id is elided so a pathological originator cannot grow the row, and the ellipsis
    /// marks it as clipped rather than letting a bare prefix read as a complete id.
    /// </summary>
    [Fact]
    public void SourceLabel_elides_an_id_longer_than_the_bound()
    {
        var id = new string('j', ActivityDashboardProjection.SourceIdDisplayLength + 10);
        var row = ProjectOne(Conv("c1", source: "Cron", sourceId: id));

        var label = ActivityDashboardProjection.SourceLabel(row);

        Assert.NotNull(label);
        Assert.EndsWith("\u2026", label, StringComparison.Ordinal);
        Assert.Equal(ActivityDashboardProjection.SourceIdDisplayLength + 1, label.Length);
        // The untruncated value stays on the row so the tooltip can still show it in full.
        Assert.Equal(id, row.SourceId);
    }

    /// <summary>
    /// AC1: a payload from a server that predates the field still deserialises, and the row is
    /// simply unattributed. Guards the additive-DTO contract rather than asserting it by inspection.
    /// </summary>
    [Fact]
    public void A_payload_without_the_field_deserialises_to_an_unattributed_row()
    {
        const string json = """
        {
          "conversationId": "c1",
          "agentId": "alpha",
          "title": "Chat",
          "isDefault": false,
          "status": "Active",
          "activeSessionId": null,
          "bindingCount": 0,
          "createdAt": "2026-07-10T11:55:00+00:00",
          "updatedAt": "2026-07-10T12:00:00+00:00",
          "source": "Cron"
        }
        """;

        var dto = System.Text.Json.JsonSerializer.Deserialize<ConversationSummaryDto>(json);

        Assert.NotNull(dto);
        Assert.Null(dto.SourceId);
        Assert.Null(ActivityDashboardProjection.SourceLabel(ProjectOne(dto)));
    }

    /// <summary>
    /// AC1: the field round-trips off the wire under its documented JSON name. A property declared
    /// with the wrong <c>JsonPropertyName</c> would leave every other clause green while the real
    /// gateway payload silently produced null.
    /// </summary>
    [Fact]
    public void The_wire_field_binds_from_its_documented_json_name()
    {
        const string json = """
        {
          "conversationId": "c1",
          "agentId": "alpha",
          "title": "Chat",
          "isDefault": false,
          "status": "Active",
          "activeSessionId": null,
          "bindingCount": 0,
          "createdAt": "2026-07-10T11:55:00+00:00",
          "updatedAt": "2026-07-10T12:00:00+00:00",
          "source": "Cron",
          "sourceId": "daily-log-analysis"
        }
        """;

        var dto = System.Text.Json.JsonSerializer.Deserialize<ConversationSummaryDto>(json);

        Assert.NotNull(dto);
        Assert.Equal("daily-log-analysis", dto.SourceId);
        Assert.Equal("daily-log-analysis", ActivityDashboardProjection.SourceLabel(ProjectOne(dto)));
    }

    // ── #3204: author-stated purpose ───────────────────────────────────────

    /// <summary>
    /// AC1: the field binds off the wire under its documented JSON name. The client DTO did not
    /// declare <c>purpose</c> at ALL before this change, so the value was discarded at the wire
    /// boundary - a property bound to the wrong name would leave every other clause green while the
    /// real gateway payload silently produced null, which is exactly the failure mode that hid this
    /// gap for six runs.
    /// </summary>
    [Fact]
    public void The_purpose_wire_field_binds_from_its_documented_json_name()
    {
        const string json = """
        {
          "conversationId": "c1",
          "agentId": "alpha",
          "title": "Chat",
          "isDefault": false,
          "status": "Active",
          "activeSessionId": null,
          "bindingCount": 0,
          "createdAt": "2026-07-10T11:55:00+00:00",
          "updatedAt": "2026-07-10T12:00:00+00:00",
          "purpose": "Track the Q3 migration rollout"
        }
        """;

        var dto = System.Text.Json.JsonSerializer.Deserialize<ConversationSummaryDto>(json);

        Assert.NotNull(dto);
        Assert.Equal("Track the Q3 migration rollout", dto.Purpose);
    }

    /// <summary>
    /// AC1 (second half): a payload that omits <c>purpose</c> yields null rather than throwing, so
    /// the new optional parameter is genuinely back-compatible with a pre-#3204 gateway.
    /// </summary>
    [Fact]
    public void A_payload_without_purpose_deserializes_to_a_null_purpose()
    {
        const string json = """
        {
          "conversationId": "c1",
          "agentId": "alpha",
          "title": "Chat",
          "isDefault": false,
          "status": "Active",
          "activeSessionId": null,
          "bindingCount": 0,
          "createdAt": "2026-07-10T11:55:00+00:00",
          "updatedAt": "2026-07-10T12:00:00+00:00"
        }
        """;

        var dto = System.Text.Json.JsonSerializer.Deserialize<ConversationSummaryDto>(json);

        Assert.NotNull(dto);
        Assert.Null(dto.Purpose);
        Assert.Null(ActivityDashboardProjection.PurposeLabel(ProjectOne(dto)));
    }

    /// <summary>
    /// AC2: the purpose survives <see cref="ActivityDashboardProjection.Project"/> end to end onto
    /// the row. Without this the label has nothing to read and every remaining clause is vacuous.
    /// </summary>
    [Fact]
    public void Project_carries_the_stated_purpose_onto_the_row()
    {
        var row = ProjectOne(Conv("c1", purpose: "Track the Q3 migration rollout"));

        Assert.Equal("Track the Q3 migration rollout", row.Purpose);
    }

    /// <summary>
    /// AC3: blank, whitespace-only and absent purposes all collapse to null on the row, so "present
    /// but empty" cannot render differently from "absent" - the same normalisation rule
    /// <c>NormalizeRole</c> and <c>SourceLabel</c> apply.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Blank_and_absent_purposes_collapse_to_null(string? purpose)
    {
        var row = ProjectOne(Conv("c1", purpose: purpose));

        Assert.Null(row.Purpose);
        Assert.Null(ActivityDashboardProjection.PurposeLabel(row));
    }

    /// <summary>
    /// AC3 (surrounding whitespace): a purpose padded with whitespace is trimmed rather than
    /// rendered with its padding, so the display value cannot depend on incidental input spacing.
    /// </summary>
    [Fact]
    public void A_padded_purpose_is_trimmed_on_the_row()
    {
        var row = ProjectOne(Conv("c1", purpose: "  Track the rollout  "));

        Assert.Equal("Track the rollout", row.Purpose);
        Assert.Equal("Track the rollout", ActivityDashboardProjection.PurposeLabel(row));
    }

    /// <summary>
    /// AC4: a purpose at exactly the display bound renders verbatim - the boundary case that
    /// separates a correct <c>&lt;=</c> from an off-by-one <c>&lt;</c>.
    /// </summary>
    [Fact]
    public void A_purpose_at_the_display_bound_renders_verbatim()
    {
        var purpose = new string('p', ActivityDashboardProjection.PurposeDisplayLength);

        var label = ActivityDashboardProjection.PurposeLabel(ProjectOne(Conv("c1", purpose: purpose)));

        Assert.Equal(purpose, label);
        Assert.DoesNotContain('\u2026', label!);
    }

    /// <summary>
    /// AC4: a purpose over the bound is elided to exactly bound+1 characters, ending in a single
    /// ellipsis. Asserting the exact LENGTH (not merely "contains an ellipsis") is what pins the
    /// bound itself: an implementation that emitted the whole string plus an ellipsis would satisfy
    /// a contains-check and still put an unbounded value into the DOM, which is the defect the cap
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void A_purpose_over_the_display_bound_is_elided()
    {
        var purpose = new string('p', ActivityDashboardProjection.PurposeDisplayLength + 40);

        var label = ActivityDashboardProjection.PurposeLabel(ProjectOne(Conv("c1", purpose: purpose)));

        Assert.NotNull(label);
        Assert.Equal(ActivityDashboardProjection.PurposeDisplayLength + 1, label.Length);
        Assert.EndsWith("\u2026", label, StringComparison.Ordinal);
        Assert.Equal(new string('p', ActivityDashboardProjection.PurposeDisplayLength), label[..^1]);
    }

    /// <summary>
    /// AC8: the purpose is per-row prose and contributes to no count, so widening the row must leave
    /// the summary strip byte-identical. Pins the deliberate non-change rather than assuming it.
    /// </summary>
    [Fact]
    public void Purpose_does_not_affect_the_summary_strip()
    {
        var filter = new ActivityDashboardFilter(IncludeCron: true);
        var without = ActivityDashboardProjection.Summarize(
            ActivityDashboardProjection.Project([Conv("c1")], filter, Now));
        var with = ActivityDashboardProjection.Summarize(
            ActivityDashboardProjection.Project([Conv("c1", purpose: "Track the rollout")], filter, Now));

        Assert.Equal(without, with);
    }
}