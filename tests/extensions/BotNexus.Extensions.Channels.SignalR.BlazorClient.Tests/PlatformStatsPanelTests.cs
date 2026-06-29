using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
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
            activeSubAgents
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
