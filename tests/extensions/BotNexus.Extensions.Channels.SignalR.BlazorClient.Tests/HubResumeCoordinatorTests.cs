using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the liveness-verified hub-reset algorithm used on mobile app resume (#1838).
/// The coordinator probes the connection first and only tears down + rebuilds when the
/// probe fails (iOS zombie socket), while guarding against concurrent rebuilds from
/// rapid visibility toggles.
/// </summary>
public sealed class HubResumeCoordinatorTests
{
    /// <summary>
    /// A healthy connection (probe succeeds) must keep the existing connection: the
    /// rebuild path is never taken and the refresh path still runs.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_ProbeSucceeds_KeepsConnectionAndRefreshes()
    {
        var coordinator = new HubResumeCoordinator { ProbeTimeout = TimeSpan.FromMilliseconds(50) };
        var rebuilt = false;
        var refreshed = false;

        var outcome = await coordinator.ResumeAsync(
            probe: _ => Task.FromResult(true),
            rebuild: () => { rebuilt = true; return Task.CompletedTask; },
            refresh: () => { refreshed = true; return Task.CompletedTask; });

        outcome.ShouldBe(HubResumeOutcome.Alive);
        rebuilt.ShouldBeFalse();
        refreshed.ShouldBeTrue();
    }

    /// <summary>
    /// A zombie connection where the probe times out / fails must trigger teardown +
    /// rebuild before the refresh path runs.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_ProbeFails_TearsDownAndRebuilds()
    {
        var coordinator = new HubResumeCoordinator { ProbeTimeout = TimeSpan.FromMilliseconds(50) };
        var rebuilt = false;
        var refreshed = false;

        var outcome = await coordinator.ResumeAsync(
            probe: _ => Task.FromResult(false),
            rebuild: () => { rebuilt = true; return Task.CompletedTask; },
            refresh: () => { refreshed = true; return Task.CompletedTask; });

        outcome.ShouldBe(HubResumeOutcome.Rebuilt);
        rebuilt.ShouldBeTrue();
        refreshed.ShouldBeTrue();
    }

    /// <summary>
    /// A probe that throws (e.g. invocation cancelled by the short timeout) is treated as
    /// a failed liveness check and triggers a rebuild rather than propagating.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_ProbeThrows_TreatedAsFailureAndRebuilds()
    {
        var coordinator = new HubResumeCoordinator { ProbeTimeout = TimeSpan.FromMilliseconds(50) };
        var rebuilt = false;

        var outcome = await coordinator.ResumeAsync(
            probe: _ => throw new OperationCanceledException(),
            rebuild: () => { rebuilt = true; return Task.CompletedTask; },
            refresh: () => Task.CompletedTask);

        outcome.ShouldBe(HubResumeOutcome.Rebuilt);
        rebuilt.ShouldBeTrue();
    }

    /// <summary>
    /// Rapid visibility toggling must not stack rebuilds: while one resume is in flight,
    /// a concurrent resume returns <see cref="HubResumeOutcome.Skipped"/> and does not run
    /// the probe/rebuild/refresh delegates.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_Reentrant_SecondCallSkipsWhileFirstInFlight()
    {
        var coordinator = new HubResumeCoordinator { ProbeTimeout = TimeSpan.FromSeconds(5) };
        var gate = new TaskCompletionSource();
        var probeEntered = new TaskCompletionSource();
        var probeCount = 0;

        var first = coordinator.ResumeAsync(
            probe: async _ =>
            {
                Interlocked.Increment(ref probeCount);
                probeEntered.TrySetResult();
                await gate.Task;
                return true;
            },
            rebuild: () => Task.CompletedTask,
            refresh: () => Task.CompletedTask);

        await probeEntered.Task;

        // Second call arrives while the first is still holding the guard.
        var secondOutcome = await coordinator.ResumeAsync(
            probe: _ => { Interlocked.Increment(ref probeCount); return Task.FromResult(true); },
            rebuild: () => Task.CompletedTask,
            refresh: () => Task.CompletedTask);

        secondOutcome.ShouldBe(HubResumeOutcome.Skipped);

        gate.SetResult();
        var firstOutcome = await first;
        firstOutcome.ShouldBe(HubResumeOutcome.Alive);

        // Only the first call's probe ran; the second was skipped by the reentrancy guard.
        probeCount.ShouldBe(1);
    }

    /// <summary>
    /// After a resume completes, the guard is released so a subsequent resume runs normally.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_GuardReleasedAfterCompletion_AllowsSubsequentResume()
    {
        var coordinator = new HubResumeCoordinator { ProbeTimeout = TimeSpan.FromMilliseconds(50) };

        var firstOutcome = await coordinator.ResumeAsync(
            probe: _ => Task.FromResult(true),
            rebuild: () => Task.CompletedTask,
            refresh: () => Task.CompletedTask);
        firstOutcome.ShouldBe(HubResumeOutcome.Alive);

        var secondRebuilt = false;
        var secondOutcome = await coordinator.ResumeAsync(
            probe: _ => Task.FromResult(false),
            rebuild: () => { secondRebuilt = true; return Task.CompletedTask; },
            refresh: () => Task.CompletedTask);

        secondOutcome.ShouldBe(HubResumeOutcome.Rebuilt);
        secondRebuilt.ShouldBeTrue();
    }
}
