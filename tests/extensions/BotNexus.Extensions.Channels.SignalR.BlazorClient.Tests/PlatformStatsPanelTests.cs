using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Component tests for <see cref="PlatformStatsPanel"/> (issue #1692). The panel is a small,
/// self-contained read-only section that polls the platform stats endpoint (<c>/api/stats</c>)
/// and surfaces the live active agent-loop and active sub-agent counts. These tests pin the
/// mandatory bUnit coverage (AGENTS.md rule 9): default/loading state, rendering with fetched
/// data, and the error/unavailable path.
/// </summary>
public sealed class PlatformStatsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly MockHttpMessageHandler _httpHandler = new();

    public PlatformStatsPanelTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static string StatsJson(int activeLoops, int peakLoops, long totalCompleted, int activeSubAgents) =>
        JsonSerializer.Serialize(new
        {
            activeAgentLoops = activeLoops,
            peakAgentLoops = peakLoops,
            totalCompletedLoops = totalCompleted,
            activeSubAgents,
            activeLoopDetails = Array.Empty<object>()
        });

    /// <summary>Builds a stats payload whose headline count matches the supplied detail rows.</summary>
    private static string StatsJsonWithLoops(params (string? AgentId, string? ConversationId, string? SessionId, DateTimeOffset StartedAtUtc)[] loops) =>
        JsonSerializer.Serialize(new
        {
            activeAgentLoops = loops.Length,
            peakAgentLoops = loops.Length,
            totalCompletedLoops = 0L,
            activeSubAgents = 0,
            activeLoopDetails = loops.Select((l, i) => new
            {
                loopId = $"L{i}",
                agentId = l.AgentId,
                conversationId = l.ConversationId,
                sessionId = l.SessionId,
                startedAtUtc = l.StartedAtUtc
            }).ToArray()
        });

    [Fact]
    public void Renders_panel_container_immediately()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJson(0, 0, 0, 0));

        var cut = _ctx.Render<PlatformStatsPanel>();

        cut.Find("[data-testid='platform-stats-panel']");
    }

    [Fact]
    public void Renders_fetched_active_loop_and_subagent_counts()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJson(activeLoops: 4, peakLoops: 9, totalCompleted: 123, activeSubAgents: 2));

        var cut = _ctx.Render<PlatformStatsPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("4");
            cut.Find("[data-testid='stat-active-subagents']").TextContent.ShouldContain("2");
        });
    }

    [Fact]
    public void Renders_zero_counts_when_platform_is_idle()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJson(0, 0, 0, 0));

        var cut = _ctx.Render<PlatformStatsPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("0");
            cut.Find("[data-testid='stat-active-subagents']").TextContent.ShouldContain("0");
        });
    }

    [Fact]
    public void Shows_error_state_when_fetch_fails()
    {
        _httpHandler.SetFailure("/api/stats");

        var cut = _ctx.Render<PlatformStatsPanel>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Unable to load platform stats"));
    }

    // ---- #2794: active agent-loop disclosure --------------------------------------------------

    /// <summary>AC3: the stat is an accessible, collapsed-by-default disclosure control.</summary>
    [Fact]
    public void Active_loops_stat_renders_as_collapsed_accessible_disclosure()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJson(0, 0, 0, 0));

        var cut = _ctx.Render<PlatformStatsPanel>();

        var toggle = cut.Find("[data-testid='active-loops-toggle']");
        toggle.GetAttribute("aria-expanded").ShouldBe("false");
        toggle.GetAttribute("aria-controls").ShouldBe("active-loop-details");
        cut.FindAll("[data-testid='active-loop-details']").ShouldBeEmpty();
    }

    /// <summary>
    /// AC3 + AC4 + AC6, and the component half of AC7: emptying the returned detail collection
    /// makes this test fail by name because no loop rows can be found.
    /// </summary>
    [Fact]
    public void Expanding_active_loops_renders_a_row_per_active_agent_and_conversation()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(
            ("farnsworth", "c_abc", "s_1", started),
            ("nova", "c_def", "s_2", started.AddMinutes(1))));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("2"));

        cut.Find("[data-testid='active-loops-toggle']").Click();

        cut.Find("[data-testid='active-loops-toggle']").GetAttribute("aria-expanded").ShouldBe("true");
        var rows = cut.FindAll("[data-testid='active-loop-row']");
        rows.Count.ShouldBe(2);
        rows[0].TextContent.ShouldContain("farnsworth");
        rows[0].TextContent.ShouldContain("c_abc");
        rows[1].TextContent.ShouldContain("nova");
        rows[1].TextContent.ShouldContain("c_def");

        // AC4: run age is rendered for each row.
        cut.FindAll("[data-testid='active-loop-age']").Count.ShouldBe(2);
    }

    /// <summary>AC3: the disclosure collapses again on a second activation.</summary>
    [Fact]
    public void Active_loops_disclosure_collapses_on_second_activation()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(("farnsworth", "c_abc", "s_1", DateTimeOffset.UtcNow)));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("1"));

        cut.Find("[data-testid='active-loops-toggle']").Click();
        cut.FindAll("[data-testid='active-loop-details']").Count.ShouldBe(1);

        cut.Find("[data-testid='active-loops-toggle']").Click();
        cut.FindAll("[data-testid='active-loop-details']").ShouldBeEmpty();
        cut.Find("[data-testid='active-loops-toggle']").GetAttribute("aria-expanded").ShouldBe("false");
    }

    /// <summary>AC4: a row with a conversation navigates to /chat/{agentId}/{conversationId}.</summary>
    [Fact]
    public void Clicking_an_active_loop_navigates_to_its_conversation()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(("farns worth", "c_abc", "s_1", DateTimeOffset.UtcNow)));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("1"));
        cut.Find("[data-testid='active-loops-toggle']").Click();

        cut.Find("[data-testid='active-loop-link']").Click();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.Uri.ShouldEndWith("/chat/farns%20worth/c_abc");
    }

    /// <summary>AC4 sad path: a loop with no conversation is listed but is not a navigation link.</summary>
    [Fact]
    public void Active_loop_without_a_conversation_is_listed_but_not_navigable()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(("farnsworth", null, "s_1", DateTimeOffset.UtcNow)));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("1"));
        cut.Find("[data-testid='active-loops-toggle']").Click();

        cut.FindAll("[data-testid='active-loop-row']").Count.ShouldBe(1);
        cut.Find("[data-testid='active-loop-row']").TextContent.ShouldContain("farnsworth");
        cut.FindAll("[data-testid='active-loop-link']").ShouldBeEmpty();
    }

    /// <summary>AC5: an idle platform shows an explicit empty state, not a blank region.</summary>
    [Fact]
    public void Expanded_view_shows_idle_state_when_no_loops_are_active()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJson(0, 0, 0, 0));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("0"));

        cut.Find("[data-testid='active-loops-toggle']").Click();

        cut.Find("[data-testid='active-loop-empty']").TextContent.ShouldContain("No agent loops are currently running");
    }

    /// <summary>
    /// AC5: the disclosure state is component-owned, so a later poll updates the rows without
    /// collapsing the operator's chosen view.
    /// </summary>
    [Fact]
    public void Refreshing_updates_rows_without_collapsing_the_disclosure()
    {
        var started = DateTimeOffset.UtcNow;
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(("farnsworth", "c_abc", "s_1", started)));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("1"));
        cut.Find("[data-testid='active-loops-toggle']").Click();
        cut.FindAll("[data-testid='active-loop-row']").Count.ShouldBe(1);

        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(
            ("farnsworth", "c_abc", "s_1", started),
            ("nova", "c_def", "s_2", started)));

        cut.WaitForAssertion(
            () =>
            {
                cut.Find("[data-testid='active-loops-toggle']").GetAttribute("aria-expanded").ShouldBe("true");
                cut.FindAll("[data-testid='active-loop-row']").Count.ShouldBe(2);
            },
            TimeSpan.FromSeconds(15));
    }

    /// <summary>AC5 sad path: a failed refresh keeps the known rows and flags them as stale.</summary>
    [Fact]
    public void Failed_refresh_keeps_known_rows_and_surfaces_a_refresh_error()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithLoops(("farnsworth", "c_abc", "s_1", DateTimeOffset.UtcNow)));

        var cut = _ctx.Render<PlatformStatsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='stat-active-loops']").TextContent.ShouldContain("1"));
        cut.Find("[data-testid='active-loops-toggle']").Click();

        _httpHandler.SetFailure("/api/stats");

        cut.WaitForAssertion(
            () =>
            {
                cut.Find("[data-testid='active-loop-refresh-error']").TextContent.ShouldContain("Refresh failed");
                cut.FindAll("[data-testid='active-loop-row']").Count.ShouldBe(1);
                cut.Markup.ShouldNotContain("Unable to load platform stats");
            },
            TimeSpan.FromSeconds(15));
    }

    /// <summary>Minimal canned-response HTTP handler mirroring the AgentDetailPanelTests pattern.</summary>
    internal sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string pathSuffix, string jsonContent)
        {
            _responses[pathSuffix] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
        }

        public void SetFailure(string pathSuffix) => _failures.Add(pathSuffix);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            foreach (var failure in _failures)
            {
                if (path.Contains(failure, StringComparison.OrdinalIgnoreCase))
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"));
            }

            foreach (var (key, response) in _responses)
            {
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
