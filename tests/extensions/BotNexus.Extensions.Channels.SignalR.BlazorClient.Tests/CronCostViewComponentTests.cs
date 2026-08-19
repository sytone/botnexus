using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Rendering tests for the Activity page's cron cost subsection (#3289): that it is reachable from
/// the sub-navigation shell, that the derived tool-calls-per-turn column reaches the DOM (and is
/// absent rather than zero when undefined), that an unmeasured job is visually distinct from a
/// cheap one, that the retention notice appears only when the response sets the flag, and that the
/// existing default view is untouched.
/// </summary>
public sealed class CronCostViewComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly CronCostMockHandler _handler = new();

    public CronCostViewComponentTests()
    {
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);

        var store = Substitute.For<IClientStateStore>();
        store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        store.GetAgent(Arg.Any<string>()).Returns((AgentState?)null);

        var rest = Substitute.For<IGatewayRestClient>();
        rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([]));
        rest.GetConversationCostsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationCostDto>>([]));

        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };

        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(rest);
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddScoped<CronApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private void SeedCosts(params object[] costs)
    {
        _handler.SetupResponse("/api/cron/costs", JsonSerializer.Serialize(costs));
        _handler.SetupResponse("/api/cron", "[]");
    }

    private static object Rollup(
        string jobId,
        int runCount,
        int measuredRunCount,
        long? totalTokens = null,
        long? totalToolCalls = null,
        long? totalTurns = null,
        long? totalDurationMs = null,
        int windowDays = 7,
        bool truncated = false) =>
        new
        {
            jobId,
            runCount,
            measuredRunCount,
            totalTokens,
            totalToolCalls,
            totalTurns,
            totalDurationMs,
            windowStart = "2026-08-11T00:00:00+00:00",
            windowDays,
            windowTruncatedByRetention = truncated
        };

    private IRenderedComponent<CronCostView> RenderView()
    {
        var cut = _ctx.Render<CronCostView>();
        cut.WaitForState(() =>
            cut.FindAll("[data-testid='cron-cost-table']").Count > 0 ||
            cut.FindAll("[data-testid='cron-cost-empty']").Count > 0);
        return cut;
    }

    // ── AC1: routing ───────────────────────────────────────────────────────

    /// <summary>
    /// AC1: <c>cron</c> is registered in the shell and direct navigation to <c>/activity/cron</c>
    /// selects it, rendering the cron cost subsection in place of the overview dashboard.
    /// </summary>
    [Fact]
    public void Cron_section_is_registered_and_renders_at_its_route()
    {
        SeedCosts();

        Activity.DefaultSections.Select(s => s.Key).ShouldContain("cron");

        var cut = _ctx.Render<Activity>(p => p.Add(c => c.Section, "cron"));

        cut.Find("[data-testid='cron-cost']");
        // A subsection REPLACES the default view.
        cut.FindAll("[data-testid='activity-dashboard']").ShouldBeEmpty();
        cut.FindAll("[data-testid='activity-cost']").ShouldBeEmpty();
    }

    // ── AC9: the existing default view is unchanged ────────────────────────

    /// <summary>
    /// AC9: appending a section did not move the landing view. The parameterless route still renders
    /// the overview dashboard and neither cost subsection, and <c>/activity/overview</c> and
    /// <c>/activity/costs</c> behave exactly as before.
    /// </summary>
    [Fact]
    public void Appending_the_cron_section_does_not_change_the_default_or_existing_views()
    {
        SeedCosts();

        var bare = _ctx.Render<Activity>();
        bare.Find("[data-testid='activity-dashboard']");
        bare.FindAll("[data-testid='cron-cost']").ShouldBeEmpty();

        var overview = _ctx.Render<Activity>(p => p.Add(c => c.Section, "overview"));
        overview.Find("[data-testid='activity-dashboard']");
        overview.FindAll("[data-testid='cron-cost']").ShouldBeEmpty();

        var costs = _ctx.Render<Activity>(p => p.Add(c => c.Section, "costs"));
        costs.Find("[data-testid='activity-cost']");
        costs.FindAll("[data-testid='cron-cost']").ShouldBeEmpty();

        // The pre-existing sections keep their keys and their order; cron is APPENDED.
        Activity.DefaultSections.Select(s => s.Key).ShouldBe(["overview", "costs", "cron"]);
    }

    // ── AC3: total ranking reaches the DOM ─────────────────────────────────

    /// <summary>
    /// AC3 (render half): the rendered row order follows TOTAL spend, so the frequent-but-cheaper
    /// job is listed above the rare-but-expensive one.
    /// </summary>
    [Fact]
    public void Rows_render_in_total_descending_order()
    {
        SeedCosts(
            Rollup("rare", 8, 8, totalTokens: 800_000, totalToolCalls: 80, totalTurns: 40),
            Rollup("frequent", 192, 192, totalTokens: 4_800_000, totalToolCalls: 3_840, totalTurns: 1_920));

        var cut = RenderView();

        cut.FindAll("[data-testid='cron-cost-row']")
            .Select(r => r.GetAttribute("data-job-id"))
            .ShouldBe(["frequent", "rare"]);
    }

    // ── AC4: the derived column in the DOM ─────────────────────────────────

    /// <summary>
    /// AC4 (render half): the derived tool-calls-per-turn value reaches the DOM for a measured job,
    /// and renders as the not-measured word - never <c>0</c>, never <c>NaN</c> - for a job with no
    /// turns.
    /// </summary>
    [Fact]
    public void Tool_calls_per_turn_column_renders_the_ratio_or_the_not_measured_word()
    {
        SeedCosts(
            Rollup("measured", 192, 192, totalTokens: 4_800_000, totalToolCalls: 3_840, totalTurns: 1_920),
            Rollup("command-job", 40, 0));

        var cut = RenderView();
        var cells = cut.FindAll("[data-testid='cron-cost-tool-calls-per-turn']");

        cells.Count.ShouldBe(2);
        cells[0].TextContent.Trim().ShouldBe("2.00");
        cells[0].GetAttribute("data-measured").ShouldBe("true");

        var unmeasured = cells[1].TextContent.Trim();
        unmeasured.ShouldBe(CronCostProjection.NotMeasured);
        unmeasured.ShouldNotBe("0");
        unmeasured.ShouldNotContain("NaN");
        unmeasured.ShouldNotContain("Infinity");
        cells[1].GetAttribute("data-measured").ShouldBe("false");
    }

    // ── AC5 / AC6: unmeasured is visually distinct ─────────────────────────

    /// <summary>
    /// AC5/AC6: a job with runs but zero measured runs renders as unmeasured at every cell, and both
    /// counts are shown, so it is distinguishable from a genuinely cheap job whose measured totals
    /// really are zero.
    /// </summary>
    [Fact]
    public void Job_with_runs_but_no_measurements_renders_as_unmeasured_not_zero()
    {
        SeedCosts(
            Rollup("cheap-but-measured", 3, 3, totalTokens: 0, totalToolCalls: 0, totalTurns: 0, totalDurationMs: 0),
            Rollup("ran-but-unmeasured", 120, 0));

        var cut = RenderView();
        var rows = cut.FindAll("[data-testid='cron-cost-row']");
        var byJob = rows.ToDictionary(r => r.GetAttribute("data-job-id")!, r => r, StringComparer.Ordinal);

        var unmeasured = byJob["ran-but-unmeasured"];
        unmeasured.GetAttribute("data-measured").ShouldBe("false");
        unmeasured.QuerySelector("[data-testid='cron-cost-runs']")!.TextContent.Trim().ShouldBe("120");
        unmeasured.QuerySelector("[data-testid='cron-cost-measured-runs']")!.TextContent.Trim().ShouldBe("0");
        var unmeasuredTotal = unmeasured.QuerySelector("[data-testid='cron-cost-total-tokens']")!;
        unmeasuredTotal.TextContent.Trim().ShouldBe(CronCostProjection.NotMeasured);
        unmeasuredTotal.TextContent.Trim().ShouldNotBe("0");
        unmeasuredTotal.GetAttribute("data-measured").ShouldBe("false");

        var cheap = byJob["cheap-but-measured"];
        cheap.GetAttribute("data-measured").ShouldBe("true");
        var cheapTotal = cheap.QuerySelector("[data-testid='cron-cost-total-tokens']")!;
        cheapTotal.TextContent.Trim().ShouldBe("0");
        cheapTotal.GetAttribute("data-measured").ShouldBe("true");

        // The whole clause: the two must be DISTINGUISHABLE in the DOM.
        cheapTotal.TextContent.Trim().ShouldNotBe(unmeasuredTotal.TextContent.Trim());
    }

    // ── AC7: retention notice ──────────────────────────────────────────────

    /// <summary>
    /// AC7: the clamped-window notice appears only when the response sets
    /// <c>windowTruncatedByRetention</c>.
    /// </summary>
    [Fact]
    public void Retention_notice_appears_only_when_the_window_was_truncated()
    {
        SeedCosts(Rollup("a", 5, 5, totalTokens: 100, windowDays: 7, truncated: false));
        RenderView().FindAll("[data-testid='cron-cost-truncated']").ShouldBeEmpty();

        SeedCosts(Rollup("a", 5, 5, totalTokens: 100, windowDays: 3, truncated: true));
        var truncated = RenderView();
        truncated.Find("[data-testid='cron-cost-truncated']").TextContent.ShouldContain("3");
    }

    // ── AC8: navigation keyed on the row's own job id ──────────────────────

    /// <summary>
    /// AC8: every rendered row carries its OWN job id, and the navigation target derived from it
    /// matches - never a display index.
    /// </summary>
    [Fact]
    public void Each_row_is_keyed_on_its_own_job_id()
    {
        SeedCosts(
            Rollup("zulu", 10, 10, totalTokens: 900),
            Rollup("alpha", 10, 10, totalTokens: 500),
            Rollup("mike", 10, 10, totalTokens: 700));

        var cut = RenderView();
        var ids = cut.FindAll("[data-testid='cron-cost-row']")
            .Select(r => r.GetAttribute("data-job-id")!)
            .ToList();

        ids.ShouldBe(["zulu", "mike", "alpha"]);
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
    }

    /// <summary>An empty rollup renders the empty state rather than a bare table.</summary>
    [Fact]
    public void Empty_response_renders_the_empty_state()
    {
        SeedCosts();
        RenderView().Find("[data-testid='cron-cost-empty']");
    }

    /// <summary>
    /// A handler that returns a FRESH response per request, because an
    /// <see cref="HttpResponseMessage"/> body can only be read once and this view issues two calls.
    /// </summary>
    private sealed class CronCostMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string pathPrefix, string json) => _bodies[pathPrefix] = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            // Longest prefix wins so /api/cron/costs is never served by the /api/cron entry.
            foreach (var (prefix, body) in _bodies.OrderByDescending(kv => kv.Key.Length))
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json(body));
            }
            return Task.FromResult(Json("[]"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
