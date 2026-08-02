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
        IReadOnlyList<ParticipantDto>? participants = null,
        // #2305 (epic #2300): cron-ness is the SERVER-stamped source, never a `cron:` session-id
        // prefix. Fixtures set it explicitly.
        string source = "Channel",
        string kind = "HumanAgent") =>
        new(id, agentId, title, false, status, activeSessionId, bindingCount,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Kind: kind, Source: source, Participants: participants);

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

    // The cron assertions below are scoped to the rendered ROWS rather than the whole component
    // markup. The filter bar legitimately contains the word "Scheduled" (it is an origin choice,
    // #2385), so a whole-markup scan would answer a question about the chrome instead of the
    // question under test. Keying on the row's conversation id is also strictly stronger than
    // substring-matching a title, which any unrelated copy change could satisfy by accident.
    private static IReadOnlyList<string> RowIds(IRenderedComponent<ActivityDashboard> cut) =>
        cut.FindAll("[data-testid='activity-row']")
            .Select(r => r.GetAttribute("data-conversation-id")!)
            .ToList();

    [Fact]
    public void Cron_conversation_excluded_by_default()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", source: "Cron"));

        var cut = _ctx.Render<ActivityDashboard>();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        Assert.Equal(new[] { "c1" }, RowIds(cut));
        Assert.Contains("Normal", cut.Find("[data-testid='activity-table']").TextContent);
        Assert.DoesNotContain("Scheduled", cut.Find("[data-testid='activity-table']").TextContent);
    }

    [Fact]
    public void Cron_toggle_reveals_scheduled_conversations()
    {
        SetupConversations(
            Conv("c1", title: "Normal"),
            Conv("c2", title: "Scheduled", source: "Cron"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);

        cut.Find("[data-testid='activity-filter-cron']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);
        Assert.Equal(new[] { "c1", "c2" }, RowIds(cut).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("Scheduled", cut.Find("[data-testid='activity-table']").TextContent);
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
            Conv("c2", title: "Scheduled", source: "Cron"));

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
            Conv("c2", agentId: "beta", title: "Active scheduled", source: "Cron"),
            Conv("c3", agentId: "beta", title: "Archived scheduled", status: "Archived", source: "Cron"));

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
            Conv("c2", title: "Scheduled", source: "Cron"));

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

    [Fact]
    public void Agents_stat_card_is_an_interactive_button_tied_to_the_agent_filter()
    {
        SetupConversations(
            Conv("c1", agentId: "alpha", title: "Alpha chat"),
            Conv("c2", agentId: "beta", title: "Beta chat"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 2);

        // The agents card is a button (navigation affordance), not inert display text.
        var card = cut.Find("[data-testid='activity-summary-agents']");
        Assert.Equal("button", card.NodeName, ignoreCase: true);
        Assert.DoesNotContain("active", card.GetAttribute("class"));

        // Selecting an agent lights up the card's active treatment (single source of truth = the picker).
        cut.Find("[data-testid='activity-filter-agent']").Change("beta");
        cut.WaitForState(() => cut.FindAll("[data-testid='activity-row']").Count == 1);
        Assert.Contains("active", cut.Find("[data-testid='activity-summary-agents']").GetAttribute("class"));

        // Clicking the card is a no-throw focus affordance that leaves the filter untouched.
        cut.Find("[data-testid='activity-summary-agents']").Click();
        Assert.Equal("beta", cut.Find("[data-testid='activity-filter-agent']").GetAttribute("value"));
    }
    // ── Origin badges (#2385, epic #2300) ──────────────────────────────────

    [Fact]
    public void Renders_an_origin_badge_per_row_and_leaves_the_ordinary_human_channel_row_unbadged()
    {
        SetupConversations(
            Conv("c1", title: "Jon DM"),
            Conv("c2", title: "Nightly run", source: "Cron"),
            Conv("c3", title: "Inbound hook", source: "Webhook"),
            Conv("c4", title: "Worker", source: "Agent", kind: "AgentSubAgent"),
            Conv("c5", title: "Peer", source: "Agent", kind: "AgentAgent"));

        var cut = _ctx.Render<ActivityDashboard>();
        // Scheduled rows are default-excluded, so reveal them first - the badge set under test spans
        // every origin including cron.
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-filter-cron']"));
        cut.Find("[data-testid='activity-filter-cron']").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));

        // Assert the OBSERVABLE badge set actually rendered into the DOM, keyed by row, rather than
        // that the helper returns a string: a correct helper wired to nothing would pass the latter.
        var badgesByRow = cut.FindAll("[data-testid='activity-row']")
            .ToDictionary(
                r => r.GetAttribute("data-conversation-id")!,
                r => r.QuerySelector("[data-testid='activity-origin-badge']"));

        Assert.Null(badgesByRow["c1"]);
        Assert.Equal("Scheduled", badgesByRow["c2"]!.TextContent);
        Assert.Equal("cron", badgesByRow["c2"]!.GetAttribute("data-origin"));
        Assert.Equal("Webhook", badgesByRow["c3"]!.TextContent);
        Assert.Equal("webhook", badgesByRow["c3"]!.GetAttribute("data-origin"));
        Assert.Equal("Sub-agent", badgesByRow["c4"]!.TextContent);
        Assert.Equal("subagent", badgesByRow["c4"]!.GetAttribute("data-origin"));
        Assert.Equal("Agent-to-agent", badgesByRow["c5"]!.TextContent);
        Assert.Equal("a2a", badgesByRow["c5"]!.GetAttribute("data-origin"));
    }

    [Fact]
    public void Origin_badge_carries_the_full_source_and_kind_as_hover_detail()
    {
        SetupConversations(Conv("c1", title: "Worker", source: "Agent", kind: "AgentSubAgent"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-origin-badge']"));

        var badge = cut.Find("[data-testid='activity-origin-badge']");
        Assert.Equal("Source: Agent \u00b7 Kind: AgentSubAgent", badge.GetAttribute("title"));
    }

    // ── Title truncation and derived labels (#2528) ────────────────────────

    private const string RoutingId =
        "servicebus:a:1lexPcP4_GMPlgVVbjGrdGzyqu_vhKl8pYMbpdTsQtXOvY1lWpznwGCftUS0BRbXu4Bu3TbCzOO5xGw7E4sRVj9w1J1";

    [Fact]
    public void Table_is_wrapped_in_a_horizontally_scrollable_container()
    {
        SetupConversations(Conv("c1", title: "Chat"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-row']"));

        var scroller = cut.Find("[data-testid='activity-table-scroll']");
        Assert.NotNull(scroller.QuerySelector("[data-testid='activity-table']"));
    }

    [Fact]
    public void Row_with_a_raw_routing_id_as_title_renders_a_derived_label_not_the_raw_token()
    {
        SetupConversations(Conv("c1", agentId: "farnsworth", title: RoutingId));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-row']"));

        var title = cut.Find(".activity-conversation-title");
        Assert.NotEqual(RoutingId, title.TextContent);
        Assert.Contains("farnsworth", title.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("servicebus", title.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_title_is_length_bounded_in_the_dom_and_the_full_value_is_available_on_hover()
    {
        var longTitle = string.Join(" ", Enumerable.Repeat("verbose", 200));
        SetupConversations(Conv("c1", title: longTitle));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-row']"));

        var title = cut.Find(".activity-conversation-title");
        Assert.True(title.TextContent.Length <= ConversationLabel.MaxTitleLength);
        Assert.Equal(longTitle, title.GetAttribute("title"));
    }

    [Fact]
    public void Origin_badge_survives_alongside_a_truncated_title()
    {
        SetupConversations(Conv("c1", agentId: "farnsworth", title: RoutingId, source: "Webhook"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-row']"));

        var cell = cut.Find(".activity-cell-title");
        Assert.NotNull(cell.QuerySelector(".activity-conversation-title"));
        Assert.Equal("Webhook", cell.QuerySelector("[data-testid='activity-origin-badge']")!.TextContent);
    }
    // ── Origin facet interaction (#2385) ───────────────────────────────────

    private void SetupOriginMix() =>
        SetupConversations(
            Conv("c1", title: "Jon DM"),
            Conv("c2", title: "Inbound hook", source: "Webhook"),
            Conv("c3", title: "Worker", source: "Agent", kind: "AgentSubAgent"),
            Conv("c4", title: "Peer", source: "Agent", kind: "AgentAgent"),
            Conv("c5", title: "Self start", source: "Agent"));

    [Fact]
    public void Origin_filter_control_is_rendered_and_defaults_to_all()
    {
        var cut = _ctx.Render<ActivityDashboard>();

        var select = cut.Find("[data-testid='activity-filter-origin']");
        Assert.Equal(nameof(ActivityOriginFilter.All), select.GetAttribute("value"));
    }

    [Fact]
    public void Selecting_an_origin_narrows_the_table_to_that_origin_only()
    {
        SetupOriginMix();
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.Webhook));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));
        var row = cut.Find("[data-testid='activity-row']");
        Assert.Equal("c2", row.GetAttribute("data-conversation-id"));
        Assert.Equal("webhook", row.QuerySelector("[data-testid='activity-origin-badge']")!.GetAttribute("data-origin"));
        Assert.DoesNotContain("Jon DM", cut.Markup);
    }

    [Fact]
    public void Selecting_the_sub_agent_origin_excludes_the_peer_agent_row()
    {
        SetupOriginMix();
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.SubAgent));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));
        Assert.Equal("c3", cut.Find("[data-testid='activity-row']").GetAttribute("data-conversation-id"));
        Assert.DoesNotContain("Peer", cut.Markup);
    }

    /// <summary>
    /// The unbadged human/channel origin is still selectable: "no badge" is a real origin, not an
    /// absence the filter bar cannot express.
    /// </summary>
    [Fact]
    public void Selecting_the_human_origin_keeps_only_the_unbadged_rows()
    {
        SetupOriginMix();
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.Human));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));
        Assert.Empty(cut.FindAll("[data-testid='activity-origin-badge']"));
        Assert.Contains("Jon DM", cut.Markup);
    }

    [Fact]
    public void Origin_filter_with_no_matches_shows_the_empty_state()
    {
        SetupConversations(Conv("c1", title: "Jon DM"));
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.Webhook));

        cut.WaitForAssertion(() => cut.Find("[data-testid='activity-empty']"));
        Assert.Empty(cut.FindAll("[data-testid='activity-row']"));
    }

    /// <summary>
    /// Origin composes with the cron toggle rather than overriding it: the scheduled origin shows
    /// nothing until cron is revealed, mirroring the projection contract.
    /// </summary>
    [Fact]
    public void Origin_scheduled_composes_with_the_cron_toggle()
    {
        SetupConversations(
            Conv("c1", title: "Jon DM"),
            Conv("c2", title: "Nightly run", source: "Cron"));
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.Scheduled));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='activity-row']")));

        cut.Find("[data-testid='activity-filter-cron']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));
        Assert.Equal("c2", cut.Find("[data-testid='activity-row']").GetAttribute("data-conversation-id"));
    }

    /// <summary>
    /// The conversations stat card resets every facet, including the new one - a filter the reset
    /// affordance cannot clear is a trap.
    /// </summary>
    [Fact]
    public void Clearing_filters_resets_the_origin_facet_too()
    {
        SetupOriginMix();
        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-origin']")
           .Change(nameof(ActivityOriginFilter.Webhook));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));

        cut.Find("[data-testid='activity-summary-conversations']").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='activity-row']").Count));
        Assert.Equal(nameof(ActivityOriginFilter.All),
            cut.Find("[data-testid='activity-filter-origin']").GetAttribute("value"));
    }

    // ---- Pin state (#2619) ------------------------------------------------

    private static ConversationSummaryDto PinnedConv(
        string id,
        bool isPinned,
        DateTimeOffset updatedAt,
        string title = "Chat") =>
        new(id, "alpha", title, false, "Active", null, 0,
            updatedAt.AddMinutes(-5), updatedAt, Kind: "HumanAgent", Source: "Channel",
            IsPinned: isPinned, PinnedAt: isPinned ? updatedAt : null, Participants: null);

    /// <summary>
    /// AC4: the pin indicator renders in the pinned row's DOM and is absent from the unpinned row's
    /// DOM. Scoped per row rather than to the whole markup so the filter chrome cannot satisfy it.
    /// </summary>
    [Fact]
    public void Pin_indicator_renders_on_pinned_rows_only()
    {
        var now = DateTimeOffset.UtcNow;
        SetupConversations(
            PinnedConv("pinned", isPinned: true, updatedAt: now.AddHours(-4), title: "Pinned chat"),
            PinnedConv("loose", isPinned: false, updatedAt: now, title: "Loose chat"));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));

        var rows = cut.FindAll("[data-testid='activity-row']");
        var pinnedRow = rows.Single(r => r.GetAttribute("data-conversation-id") == "pinned");
        var looseRow = rows.Single(r => r.GetAttribute("data-conversation-id") == "loose");

        Assert.Single(pinnedRow.QuerySelectorAll("[data-testid='activity-pin-badge']"));
        Assert.Empty(looseRow.QuerySelectorAll("[data-testid='activity-pin-badge']"));
        Assert.Contains("activity-row-pinned", pinnedRow.GetAttribute("class"));
        Assert.DoesNotContain("activity-row-pinned", looseRow.GetAttribute("class") ?? string.Empty);
    }

    /// <summary>
    /// AC2 at the rendered-DOM layer: the pinned row is painted above a newer unpinned row.
    /// </summary>
    [Fact]
    public void Pinned_row_renders_above_a_newer_unpinned_row()
    {
        var now = DateTimeOffset.UtcNow;
        SetupConversations(
            PinnedConv("loose", isPinned: false, updatedAt: now),
            PinnedConv("pinned", isPinned: true, updatedAt: now.AddDays(-3)));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));

        Assert.Equal(new[] { "pinned", "loose" }, RowIds(cut));
    }

    /// <summary>The pin facet is present in the filter bar and defaults to the inert All choice.</summary>
    [Fact]
    public void Renders_pin_filter_defaulted_to_all()
    {
        var cut = _ctx.Render<ActivityDashboard>();

        var select = cut.Find("[data-testid='activity-filter-pinned']");
        Assert.Equal(nameof(ActivityPinFilter.All), select.GetAttribute("value"));
    }

    /// <summary>
    /// AC5 at the DOM layer: selecting the pinned facet narrows the table to the pinned rows, and it
    /// composes with the cron toggle rather than overriding it.
    /// </summary>
    [Fact]
    public void Pin_filter_narrows_the_table_and_composes_with_cron()
    {
        var now = DateTimeOffset.UtcNow;
        SetupConversations(
            PinnedConv("pinned", isPinned: true, updatedAt: now),
            PinnedConv("loose", isPinned: false, updatedAt: now),
            new("pinned-cron", "alpha", "Cron chat", false, "Active", null, 0,
                now.AddMinutes(-5), now, Kind: "HumanAgent", Source: "Cron",
                IsPinned: true, PinnedAt: now, Participants: null));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-pinned']").Change(nameof(ActivityPinFilter.Pinned));
        cut.WaitForAssertion(() => Assert.Equal(new[] { "pinned" }, RowIds(cut)));

        cut.Find("[data-testid='activity-filter-cron']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));
        Assert.Contains("pinned-cron", RowIds(cut));
        Assert.DoesNotContain("loose", RowIds(cut));
    }

    /// <summary>Clearing filters must reset the pin facet too - a filter reset cannot miss one.</summary>
    [Fact]
    public void Clearing_filters_resets_the_pin_facet_too()
    {
        var now = DateTimeOffset.UtcNow;
        SetupConversations(
            PinnedConv("pinned", isPinned: true, updatedAt: now),
            PinnedConv("loose", isPinned: false, updatedAt: now));

        var cut = _ctx.Render<ActivityDashboard>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));

        cut.Find("[data-testid='activity-filter-pinned']").Change(nameof(ActivityPinFilter.Pinned));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='activity-row']")));

        cut.Find("[data-testid='activity-summary-conversations']").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='activity-row']").Count));
        Assert.Equal(nameof(ActivityPinFilter.All),
            cut.Find("[data-testid='activity-filter-pinned']").GetAttribute("value"));
    }
}
