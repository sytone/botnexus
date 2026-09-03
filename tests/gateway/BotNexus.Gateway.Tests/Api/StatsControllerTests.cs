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

    /// <summary>
    /// AC2 + AC7. The controller must project the tracker's detail rows, and the headline count must
    /// equal the projected list size. Replacing the returned detail collection with an empty list
    /// makes this test fail by name.
    /// </summary>
    [Fact]
    public void GetOverview_ReturnsActiveLoopDetails_FromTheSameSnapshotAsTheHeadlineCount()
    {
        var started = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero);
        var tracker = new FakeActiveLoopTracker
        {
            Snapshot = new ActiveLoopSnapshot
            {
                ActiveCount = 2,
                PeakCount = 6,
                TotalCompleted = 11,
                ActiveLoops =
                [
                    new ActiveLoopDetail { LoopId = "L1", AgentId = "farnsworth", ConversationId = "c_abc", SessionId = "s_1", StartedAtUtc = started },
                    new ActiveLoopDetail { LoopId = "L2", AgentId = "nova", ConversationId = null, SessionId = "s_2", StartedAtUtc = started.AddMinutes(1) }
                ]
            }
        };

        var controller = new StatsController(tracker, subAgentManager: null);

        var dto = controller.GetOverview().ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<PlatformStatsDto>();

        dto.ActiveLoopDetails.Count.ShouldBe(2);
        dto.ActiveAgentLoops.ShouldBe(dto.ActiveLoopDetails.Count);

        dto.ActiveLoopDetails[0].LoopId.ShouldBe("L1");
        dto.ActiveLoopDetails[0].AgentId.ShouldBe("farnsworth");
        dto.ActiveLoopDetails[0].ConversationId.ShouldBe("c_abc");
        dto.ActiveLoopDetails[0].SessionId.ShouldBe("s_1");
        dto.ActiveLoopDetails[0].StartedAtUtc.ShouldBe(started);

        dto.ActiveLoopDetails[1].AgentId.ShouldBe("nova");
        dto.ActiveLoopDetails[1].ConversationId.ShouldBeNull();

        // AC2: the endpoint must take exactly ONE snapshot; two reads could straddle a start/end.
        tracker.SnapshotCalls.ShouldBe(1);
    }

    /// <summary>Sad path: an idle platform returns an empty detail list, not null.</summary>
    [Fact]
    public void GetOverview_WhenNoLoopsAreActive_ReturnsEmptyDetailList()
    {
        var tracker = new FakeActiveLoopTracker
        {
            Snapshot = new ActiveLoopSnapshot { ActiveCount = 0, PeakCount = 3, TotalCompleted = 8, ActiveLoops = [] }
        };

        var dto = new StatsController(tracker, null).GetOverview()
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<PlatformStatsDto>();

        dto.ActiveAgentLoops.ShouldBe(0);
        dto.ActiveLoopDetails.ShouldNotBeNull();
        dto.ActiveLoopDetails.ShouldBeEmpty();
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
        public ActiveLoopSnapshot? Snapshot { get; init; }
        public int SnapshotCalls { get; private set; }

        public int ActiveCount { get; init; }
        public int PeakCount { get; init; }
        public long TotalCompleted { get; init; }

        public ActiveLoopRegistration TrackStart(string? agentId = null, string? conversationId = null, string? sessionId = null)
            => ActiveLoopRegistration.None;

        public void TrackEnd(ActiveLoopRegistration registration) { }

        public ActiveLoopSnapshot GetSnapshot()
        {
            SnapshotCalls++;
            return Snapshot ?? new ActiveLoopSnapshot
            {
                ActiveCount = ActiveCount,
                PeakCount = PeakCount,
                TotalCompleted = TotalCompleted,
                ActiveLoops = []
            };
        }
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

        public Task OnCompletedAsync(
            string subAgentId,
            string resultSummary,
            SubAgentRunOutcome? outcome = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
