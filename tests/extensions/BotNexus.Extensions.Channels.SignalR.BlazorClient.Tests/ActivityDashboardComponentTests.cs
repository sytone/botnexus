using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Bunit.TestDoubles;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit component tests for the Home / Activity dashboard (#1888). Covers loading, empty, and
/// populated states, cron default-exclude + toggle, involved-agent rendering, and row navigation.
/// </summary>
public sealed class ActivityDashboardComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IGatewayRestClient _rest;

    public ActivityDashboardComponentTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _rest = Substitute.For<IGatewayRestClient>();

        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns((AgentState?)null);
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(Array.Empty<ConversationSummaryDto>()));

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_rest);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conv(
        string id,
        string agentId = "alpha",
        string title = "Chat",
        string status = "Active",
        string? activeSessionId = null,
        int bindingCount = 0,
        IReadOnlyList<ParticipantDto>? participants = null) =>
        new(id, agentId, title, false, status, activeSessionId, bindingCount,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Participants: participants);

    private void SetupConversations(params ConversationSummaryDto[] conversations) =>
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(conversations));

    // ── Structure ──────────────────────────────────────────────────────────

    [Fact]
    public void Renders_header_and_filter_bar()
    {
        var cut = _ctx.Render<ActivityDashboard>();

        cut.Find("[data-testid='activity-dashboard']");
        cut.Find("[data-testid='activity-filter-bar']");
        cut.Find("[data-testid='activity-filter-cron']");
        cut.Find("[data-testid='activity-filter-agent']");
        cut.Find("[data-testid='activity-filter-status']");
        cut.Find("[data-testid='activity-filter-recency']");
    }

    // ── Empty state ────────────────────────────────────────────────────────

    [Fact]
    public void Shows_empty_state_when_no_conversations()
    {
        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-empty']").Count == 1);
        cut.Find("[data-testid='activity-empty']");
    }

    // ── Populated ──────────────────────────────────────────────────────────

    [Fact]
    public void Renders_row_per_active_conversation()
    {
        SetupConversations(Conv("c1", title: "Alpha chat"), Conv("c2", title: "Beta chat"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        Assert.Contains("Alpha chat", cut.Markup);
        Assert.Contains("Beta chat", cut.Markup);
    }

    [Fact]
    public void Cron_conversation_excluded_by_default()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        Assert.Contains("Normal", cut.Markup);
        Assert.DoesNotContain("Scheduled", cut.Markup);
    }

    [Fact]
    public void Cron_toggle_reveals_scheduled_conversations()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        cut.Find("[data-testid='activity-filter-cron']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        Assert.Contains("Scheduled", cut.Markup);
    }

    [Fact]
    public void Renders_all_involved_agents_for_multi_agent_conversation()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["alpha"] = new() { AgentId = "alpha", DisplayName = "Alpha" },
            ["beta"] = new() { AgentId = "beta", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci => agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        SetupConversations(Conv("c1", agentId: "alpha", participants: new[]
        {
            new ParticipantDto("Agent", "beta", "peer")
        }));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        var chips = cut.FindAll(".activity-agent-chip");
        Assert.Equal(2, chips.Count);
        Assert.Contains("Alpha", cut.Markup);
        Assert.Contains("Beta", cut.Markup);
    }

    [Fact]
    public void Clicking_row_navigates_to_conversation()
    {
        var navMan = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;
        SetupConversations(Conv("c1", agentId: "alpha", title: "Alpha chat"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        cut.Find("[data-testid='activity-row']").Click();

        Assert.Equal("http://localhost/chat/alpha/c1", navMan?.Uri);
    }

    // ── Sad paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Shows_error_state_when_load_fails()
    {
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ConversationSummaryDto>>>(_ => throw new HttpRequestException("boom"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-error']").Count == 1);
        cut.Find("[data-testid='activity-error']");
    }

    [Fact]
    public void Archived_conversation_hidden_by_default_active_filter()
    {
        SetupConversations(
            Conv("c1", title: "Active one", status: "Active"),
            Conv("c2", title: "Archived one", status: "Archived"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        Assert.Contains("Active one", cut.Markup);
        Assert.DoesNotContain("Archived one", cut.Markup);
    }

    [Fact]
    public void Agent_filter_dropdown_lists_store_agents()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["alpha"] = new() { AgentId = "alpha", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.GetAgent(Arg.Any<string>()).Returns(ci => agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        var cut = _ctx.Render<ActivityDashboard>();

        var select = cut.Find("[data-testid='activity-filter-agent']");
        Assert.Contains("Alpha", select.InnerHtml);
    }

    // ── Summary strip ────────────────────────────────────────────────────

    [Fact]
    public void Summary_strip_reflects_projected_row_counts()
    {
        SetupConversations(Conv("c1", title: "Alpha chat"), Conv("c2", title: "Beta chat"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        cut.Find("[data-testid='activity-summary-strip']");
        Assert.Contains("2", cut.Find("[data-testid='activity-summary-conversations']").TextContent);
        cut.Find("[data-testid='activity-summary-agents']");
        cut.Find("[data-testid='activity-summary-scheduled']");
        cut.Find("[data-testid='activity-summary-freshness']");
    }

    [Fact]
    public void Summary_strip_scheduled_count_tracks_cron_toggle()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        // Cron hidden by default: 1 conversation, 0 scheduled.
        Assert.Contains("1", cut.Find("[data-testid='activity-summary-conversations']").TextContent);
        Assert.Contains("0", cut.Find("[data-testid='activity-summary-scheduled']").TextContent);

        cut.Find("[data-testid='activity-filter-cron']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        Assert.Contains("1", cut.Find("[data-testid='activity-summary-scheduled']").TextContent);
    }

    [Fact]
    public void Clicking_conversations_stat_card_clears_all_filters()
    {
        SetupConversations(
            Conv("c1", agentId: "alpha", title: "Active normal"),
            Conv("c2", agentId: "beta", title: "Active scheduled", activeSessionId: "cron:job:20260719"),
            Conv("c3", agentId: "beta", title: "Archived scheduled", status: "Archived", activeSessionId: "cron:job:20260718"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        cut.Find("[data-testid='activity-filter-cron']").Click();
        cut.Find("[data-testid='activity-filter-agent']").Change("beta");
        cut.Find("[data-testid='activity-filter-status']").Change(ActivityStatusFilter.Archived.ToString());
        cut.Find("[data-testid='activity-filter-recency']").Change(ActivityRecencyWindow.Month.ToString());
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        cut.Find("[data-testid='activity-summary-conversations']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        Assert.Contains("Active normal", cut.Markup);
        Assert.DoesNotContain("Active scheduled", cut.Markup);
        Assert.DoesNotContain("Archived scheduled", cut.Markup);
        Assert.Equal("", cut.Find("[data-testid='activity-filter-agent']").GetAttribute("value"));
        Assert.Equal(ActivityStatusFilter.Active.ToString(), cut.Find("[data-testid='activity-filter-status']").GetAttribute("value"));
        Assert.Equal(ActivityRecencyWindow.Any.ToString(), cut.Find("[data-testid='activity-filter-recency']").GetAttribute("value"));
        Assert.False(cut.Find("[data-testid='activity-filter-cron']").HasAttribute("aria-pressed"));
    }

    [Fact]
    public void Clicking_scheduled_stat_card_toggles_cron_visibility()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", activeSessionId: "cron:job:20260710"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        // Clicking the scheduled stat card reveals the cron rows (acts as the cron toggle).
        cut.Find("[data-testid='activity-summary-scheduled']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        Assert.Contains("Scheduled", cut.Markup);
        Assert.Contains("active", cut.Find("[data-testid='activity-summary-scheduled']").GetAttribute("class"));

        // Clicking again hides them.
        cut.Find("[data-testid='activity-summary-scheduled']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
    }
}
