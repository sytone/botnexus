using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit coverage for the /platform route. Deliberately thin: the counters and the loop list are
/// already covered by <see cref="PlatformStatsPanelTests"/>, so these tests only pin what the PAGE
/// contributes, which nothing else can see.
///
/// Both assertions exist because the panel's own suite cannot fail when the page stops wiring it
/// up. <c>PlatformStatsPanel</c> defaults <c>LoopsInitiallyExpanded</c> to false for Home, so
/// dropping the attribute here would leave all 40 panel and Landing tests green while /platform
/// quietly went back to opening on a collapsed disclosure over an otherwise empty viewport - the
/// "green tests, dead wiring" shape this repo has been bitten by before.
/// </summary>
public sealed class PlatformPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    // Nested inside PlatformStatsPanelTests, and deliberately reused rather than duplicated. The
    // unqualified name binds to an unrelated namespace-level MockHttpMessageHandler in
    // GatewayRestClientTests.cs, which has no SetupResponse.
    private readonly PlatformStatsPanelTests.MockHttpMessageHandler _httpHandler = new();

    public PlatformPageTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static string StatsJsonWithOneLoop() =>
        JsonSerializer.Serialize(new
        {
            activeAgentLoops = 1,
            peakAgentLoops = 1,
            totalCompletedLoops = 0L,
            activeSubAgents = 0,
            activeLoopDetails = new[]
            {
                new
                {
                    loopId = "L0",
                    agentId = "farnsworth",
                    conversationId = "c_abc",
                    sessionId = "s_1",
                    startedAtUtc = DateTimeOffset.UtcNow
                }
            }
        });

    /// <summary>The panel carries no chrome of its own, so the page must supply the bar.</summary>
    [Fact]
    public void Wraps_the_stats_panel_in_the_shared_bar()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithOneLoop());

        var cut = _ctx.Render<Platform>();

        var bar = cut.Find("[data-testid='platform-stats-bar']");
        bar.ClassList.ShouldContain("stats-bar");
        bar.QuerySelector("[data-testid='platform-stats-panel']").ShouldNotBeNull();
    }

    /// <summary>
    /// The route exists to answer "what is running right now", so it opts the loop list open
    /// rather than hiding it behind a disclosure click.
    /// </summary>
    [Fact]
    public void Opens_the_active_loop_list_without_a_click()
    {
        _httpHandler.SetupResponse("/api/stats", StatsJsonWithOneLoop());

        var cut = _ctx.Render<Platform>();

        cut.Find("[data-testid='active-loops-toggle']").GetAttribute("aria-expanded").ShouldBe("true");
        cut.FindAll("[data-testid='active-loop-details']").Count.ShouldBe(1);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='active-loop-row']").Count.ShouldBe(1));
    }
}
