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
        IReadOnlyList<ParticipantDto>? participants = null) =>
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
            Participants: participants);

    // ── Cron detection ─────────────────────────────────────────────────────

    [Fact]
    public void IsCronConversation_true_for_cron_session_prefix()
    {
        var conv = Conv("c1", activeSessionId: "cron:job-1:20260710");
        Assert.True(ActivityDashboardProjection.IsCronConversation(conv));
    }

    [Fact]
    public void IsCronConversation_true_for_cron_session_on_any_conversation_id()
    {
        var conv = Conv("c1", activeSessionId: "cron:job-1:20260710");
        Assert.True(ActivityDashboardProjection.IsCronConversation(conv));
    }

    [Fact]
    public void IsCronConversation_false_for_normal_conversation()
    {
        var conv = Conv("c1", activeSessionId: "signal:+123");
        Assert.False(ActivityDashboardProjection.IsCronConversation(conv));
    }

    // ── Cron default-exclude + toggle ──────────────────────────────────────

    [Fact]
    public void Cron_conversations_excluded_by_default()
    {
        var conversations = new[]
        {
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710")
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
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710")
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
}
