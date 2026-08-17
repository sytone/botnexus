using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Rendering tests for the Activity page's conversation cost subsection (#2898): that the
/// subsection is reachable from the sub-navigation shell, that a not-measured value reaches the DOM
/// as such rather than as a zero, and that each row's link is keyed on its own conversation id.
/// </summary>
public sealed class ActivityCostViewComponentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _rest;

    public ActivityCostViewComponentTests()
    {
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);

        var store = Substitute.For<IClientStateStore>();
        store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        store.GetAgent(Arg.Any<string>()).Returns((AgentState?)null);

        _rest = Substitute.For<IGatewayRestClient>();

        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(_rest);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conv(
        string id,
        string agentId = "alpha",
        string title = "Chat",
        string source = "Channel") =>
        new(
            ConversationId: id,
            AgentId: agentId,
            Title: title,
            IsDefault: false,
            Status: "Active",
            ActiveSessionId: null,
            BindingCount: 0,
            CreatedAt: Now.AddMinutes(-5),
            UpdatedAt: Now,
            Source: source);

    private void Seed(
        IReadOnlyList<ConversationSummaryDto> conversations,
        IReadOnlyList<ConversationCostDto> costs)
    {
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(conversations));
        _rest.GetConversationCostsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(costs));
    }

    /// <summary>
    /// AC1 (routing half): the cost subsection is registered in the shell and
    /// <c>/activity/costs</c> renders it in place of the overview dashboard.
    /// </summary>
    [Fact]
    public void Costs_section_is_registered_and_renders_at_its_route()
    {
        Seed([], []);

        Activity.DefaultSections.Select(s => s.Key).ShouldContain("costs");

        var cut = _ctx.Render<Activity>(p => p.Add(c => c.Section, "costs"));

        cut.Find("[data-testid='activity-cost']");
        // A subsection REPLACES the default view - the overview table must not also render.
        cut.FindAll("[data-testid='activity-dashboard']").ShouldBeEmpty();
    }

    /// <summary>
    /// AC1 (default route unchanged): the parameterless route still renders the overview dashboard
    /// and not the cost subsection, so adding the subsection did not move the landing view.
    /// </summary>
    [Fact]
    public void Parameterless_route_still_renders_the_overview_not_the_cost_view()
    {
        Seed([], []);

        var cut = _ctx.Render<Activity>();

        cut.Find("[data-testid='activity-dashboard']");
        cut.FindAll("[data-testid='activity-cost']").ShouldBeEmpty();
    }

    /// <summary>
    /// AC1: rows render ranked by total, most-accumulated first.
    /// </summary>
    [Fact]
    public void Rows_render_ranked_by_total_descending()
    {
        Seed(
            [Conv("cheap", title: "Cheap"), Conv("dear", title: "Dear")],
            [new ConversationCostDto("cheap", 1, 10, 0), new ConversationCostDto("dear", 40, 90_000, 3)]);

        var cut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));

        var rows = cut.FindAll("[data-testid='activity-cost-row']");
        rows.Count.ShouldBe(2);
        rows[0].GetAttribute("data-conversation-id").ShouldBe("dear");
        rows[1].GetAttribute("data-conversation-id").ShouldBe("cheap");
    }

    /// <summary>
    /// AC3: a not-measured value reaches the DOM as the not-measured word, and a measured zero
    /// reaches it as "0". Asserted on the rendered text of both cells so a formatter that collapsed
    /// null to zero reddens here as well as in the projection tests.
    /// </summary>
    [Fact]
    public void Not_measured_renders_distinctly_from_a_measured_zero()
    {
        Seed(
            [Conv("unmeasured", title: "Unmeasured"), Conv("zeroed", title: "Zeroed")],
            [
                // Same message count so ordering is decided by the id tie-break, keeping this test
                // about rendering rather than ranking.
                new ConversationCostDto("unmeasured", 1, 10, null),
                new ConversationCostDto("zeroed", 1, 10, 0)
            ]);

        var cut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));

        var cells = cut.FindAll("[data-testid='activity-cost-row']")
            .ToDictionary(
                r => r.GetAttribute("data-conversation-id")!,
                r => r.QuerySelector("[data-testid='activity-cost-compactions']")!);

        cells["unmeasured"].TextContent.Trim().ShouldBe(ActivityCostProjection.NotMeasured);
        cells["unmeasured"].GetAttribute("data-measured").ShouldBe("false");

        cells["zeroed"].TextContent.Trim().ShouldBe("0");
        cells["zeroed"].GetAttribute("data-measured").ShouldBe("true");

        // Total tokens are unmeasured on both rows today, so both must read as such - never as 0.
        foreach (var row in cut.FindAll("[data-testid='activity-cost-row']"))
        {
            row.QuerySelector("[data-testid='activity-cost-total']")!
                .TextContent.Trim().ShouldBe(ActivityCostProjection.NotMeasured);
        }
    }

    /// <summary>
    /// AC2: the origin badge rendered in the cost table is the same badge the main activity table
    /// renders for the same conversation - one classifier, two surfaces.
    /// </summary>
    [Fact]
    public void Origin_badge_agrees_with_the_main_activity_table()
    {
        var conversations = new[] { Conv("sub", title: "Sub run", source: "Agent") with { Kind = "AgentSubAgent" } };
        Seed(conversations, [new ConversationCostDto("sub", 3, 30, 1)]);

        var costCut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));
        var mainCut = _ctx.Render<ActivityDashboard>();

        var costBadge = costCut.Find("[data-testid='activity-cost-origin-badge']");
        var mainBadge = mainCut.Find("[data-testid='activity-origin-badge']");

        costBadge.TextContent.Trim().ShouldBe(mainBadge.TextContent.Trim());
        costBadge.GetAttribute("data-origin").ShouldBe(mainBadge.GetAttribute("data-origin"));

        // Non-vacuity: a badge really was rendered, not two empty strings compared.
        costBadge.TextContent.Trim().ShouldBe("Sub-agent");
    }

    /// <summary>
    /// AC5: each rendered row carries its OWN conversation id, so a click target is derivable from
    /// the row rather than from its display position.
    /// </summary>
    [Fact]
    public void Each_row_carries_its_own_conversation_id()
    {
        Seed(
            [Conv("alpha-conv", title: "A"), Conv("beta-conv", title: "B"), Conv("gamma-conv", title: "C")],
            [
                new ConversationCostDto("alpha-conv", 1, 300, 0),
                new ConversationCostDto("beta-conv", 1, 200, 0),
                new ConversationCostDto("gamma-conv", 1, 100, 0)
            ]);

        var cut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));

        cut.FindAll("[data-testid='activity-cost-row']")
            .Select(r => r.GetAttribute("data-conversation-id"))
            .ShouldBe(["alpha-conv", "beta-conv", "gamma-conv"]);
    }

    /// <summary>
    /// Sad path: a failing rollup read surfaces an error state rather than an empty table that
    /// would read as "nothing costs anything".
    /// </summary>
    [Fact]
    public void Failed_load_renders_an_error_rather_than_an_empty_ranking()
    {
        _rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([Conv("c1")]));
        _rest.GetConversationCostsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ConversationCostDto>>>(_ => throw new HttpRequestException("boom"));

        var cut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));

        cut.Find("[data-testid='activity-cost-error']");
        cut.FindAll("[data-testid='activity-cost-row']").ShouldBeEmpty();
    }

    /// <summary>
    /// Sad path: no matching conversations renders the empty state, distinct from the error state.
    /// </summary>
    [Fact]
    public void No_matching_conversations_renders_the_empty_state()
    {
        Seed([], []);

        var cut = _ctx.Render<ActivityCostView>(p => p.Add(c => c.Now, Now));

        cut.Find("[data-testid='activity-cost-empty']");
        cut.FindAll("[data-testid='activity-cost-error']").ShouldBeEmpty();
    }
}
