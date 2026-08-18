using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Dumps the cron cost subsection's REAL rendered markup to <c>artifacts/ui-evidence/</c> so a PR's
/// UI evidence is a capture of what the component actually emits rather than a hand-transcribed
/// mock-up of it (#3289).
/// </summary>
/// <remarks>
/// This is an evidence-producing test, and it is also a genuine assertion: it fails if the markup it
/// captured does not contain the derived column, both measured states and the retention notice - so
/// it cannot silently emit an empty shell and call it evidence.
/// </remarks>
public sealed class CronCostViewMarkupCaptureTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public CronCostViewMarkupCaptureTests()
    {
        var handler = new CaptureHandler();
        var costs = JsonSerializer.Serialize(new object[]
        {
            Rollup("heartbeat", 1_375, 1_375, 48_120_400, 12_880, 6_440, 5_412_000, 3, true),
            Rollup("issue-sweeper", 175, 175, 8_004_500, 2_100, 300, 986_000, 3, true),
            Rollup("log-rotate", 612, 0, windowDays: 3, truncated: true)
        });
        handler.Setup("/api/cron/costs", costs);
        handler.Setup("/api/cron", JsonSerializer.Serialize(new object[]
        {
            new { id = "heartbeat", name = "Heartbeat sweep" },
            new { id = "issue-sweeper", name = "Issue sweeper" },
            new { id = "log-rotate", name = "Log rotate (command job)" }
        }));

        _ctx.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddScoped<CronApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static object Rollup(
        string jobId, int runCount, int measuredRunCount,
        long? totalTokens = null, long? totalToolCalls = null, long? totalTurns = null,
        long? totalDurationMs = null, int windowDays = 7, bool truncated = false) =>
        new
        {
            jobId,
            runCount,
            measuredRunCount,
            totalTokens,
            totalToolCalls,
            totalTurns,
            totalDurationMs,
            windowStart = "2026-08-15T00:00:00+00:00",
            windowDays,
            windowTruncatedByRetention = truncated
        };

    /// <summary>
    /// Renders the subsection and writes its markup to disk, asserting the capture is non-vacuous:
    /// it must carry the derived column, a measured AND an unmeasured cell, and the clamp notice.
    /// </summary>
    [Fact]
    public void Capture_rendered_markup_for_ui_evidence()
    {
        var cut = _ctx.Render<CronCostView>();
        cut.WaitForState(() => cut.FindAll("[data-testid='cron-cost-row']").Count == 3);

        var markup = cut.Markup;

        // Non-vacuity: an empty shell is not evidence.
        markup.ShouldContain("cron-cost-tool-calls-per-turn");
        markup.ShouldContain("data-measured=\"true\"");
        markup.ShouldContain("data-measured=\"false\"");
        markup.ShouldContain(CronCostProjection.NotMeasured);
        markup.ShouldContain("cron-cost-truncated");
        // 12,880 tool calls over 6,440 turns is exactly 2.00 per turn.
        markup.ShouldContain("2.00");

        var dir = Path.Combine(AppContext.BaseDirectory, "ui-evidence");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cron-cost-view.html"), markup, Encoding.UTF8);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);

        public void Setup(string prefix, string json) => _bodies[prefix] = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            foreach (var (prefix, body) in _bodies.OrderByDescending(kv => kv.Key.Length))
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }
}
