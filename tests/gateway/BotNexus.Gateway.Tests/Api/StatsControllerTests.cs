using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Covers the platform-wide stats overview endpoint (issue #1692). The controller is a thin
/// read-only aggregator over the already-existing <see cref="IActiveLoopTracker"/> (active agent
/// loops) and <see cref="ISubAgentManager.ActiveSubAgentCount"/> (platform-wide active sub-agents),
/// so these tests pin (a) that the headline counts are surfaced from the injected signals and
/// (b) the defensive null-service path returns zeros rather than throwing - mirroring how
/// <c>DiagnosticsController</c> treats its optional diagnostics services.
/// </summary>
public sealed class StatsControllerTests
{
    [Fact]
    public void GetOverview_SurfacesActiveLoopAndActiveSubAgentCounts_FromInjectedSignals()
    {
        var tracker = new FakeActiveLoopTracker
        {
            ActiveCount = 3,
            PeakCount = 7,
            TotalCompleted = 42
        };
        var subAgents = new FakeSubAgentManager { ActiveSubAgentCount = 5 };

        var controller = new StatsController(tracker, subAgents);

        var result = controller.GetOverview();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<PlatformStatsDto>();
        dto.ActiveAgentLoops.ShouldBe(3);
        dto.PeakAgentLoops.ShouldBe(7);
        dto.TotalCompletedLoops.ShouldBe(42);
        dto.ActiveSubAgents.ShouldBe(5);
    }

    [Fact]
    public void GetOverview_WhenServicesAreNull_ReturnsZeros()
    {
        var controller = new StatsController(activeLoopTracker: null, subAgentManager: null);

        var result = controller.GetOverview();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = ok.Value.ShouldBeOfType<PlatformStatsDto>();
        dto.ActiveAgentLoops.ShouldBe(0);
        dto.PeakAgentLoops.ShouldBe(0);
        dto.TotalCompletedLoops.ShouldBe(0);
        dto.ActiveSubAgents.ShouldBe(0);
    }

    [Fact]
    public void GetOverview_WhenOnlyLoopTrackerPresent_ReportsLoopsAndZeroSubAgents()
    {
        var tracker = new FakeActiveLoopTracker { ActiveCount = 2, PeakCount = 4, TotalCompleted = 9 };

        var controller = new StatsController(tracker, subAgentManager: null);

        var result = controller.GetOverview();

        var dto = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<PlatformStatsDto>();
        dto.ActiveAgentLoops.ShouldBe(2);
        dto.ActiveSubAgents.ShouldBe(0);
    }

    private sealed class FakeActiveLoopTracker : IActiveLoopTracker
    {
        public int ActiveCount { get; init; }
        public int PeakCount { get; init; }
        public long TotalCompleted { get; init; }

        public void TrackStart() { }
        public void TrackEnd() { }
    }

    // Minimal hand-rolled fake: only ActiveSubAgentCount matters for these tests; the remaining
    // members are never exercised by GetOverview, so they throw to make any accidental use loud.
    private sealed class FakeSubAgentManager : ISubAgentManager
    {
        public int ActiveSubAgentCount { get; init; }

        public Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
