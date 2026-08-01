using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the post-terminal-close reconnect loop that fixes the desktop portal never recovering
/// from a gateway restart (#2624).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No wall-clock assertions.</strong> The loop takes a delay seam, so every timing assertion
/// here reads the <em>durations requested</em> from a recorder rather than measuring elapsed time.
/// Nothing in this file sleeps for a backoff interval, so nothing here can flake on a slow machine.
/// </para>
/// <para>
/// The recorder also acts as the loop's pacer: because the loop awaits the delay seam before each
/// dial, a recorder that completes each delay on demand lets a test drive an arbitrary number of
/// simulated attempts instantly, including the multi-hour-outage case.
/// </para>
/// </remarks>
public sealed class DesktopReconnectLoopTests
{
    /// <summary>
    /// The framework default <c>WithAutomaticReconnect()</c> budget is ~5 retries x 3s. Any attempt
    /// past this index is one the stock policy would never have made -- it had already given up and
    /// raised <c>Closed</c>, which is precisely the state this loop starts from.
    /// </summary>
    private const int DefaultPolicyAttemptBudget = 5;

    /// <summary>
    /// THE core regression test. The old behaviour was terminal: <c>Closed</c> fired, nothing
    /// re-dialled, and the page stayed dead until a manual reload. This asserts that dial attempts
    /// keep happening well past the point the default policy would have surrendered.
    /// </summary>
    [Fact]
    public async Task Loop_KeepsDialling_AfterDefaultPolicyBudgetWouldHaveGivenUp()
    {
        var dials = 0;
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ =>
            {
                Interlocked.Increment(ref dials);
                return Task.FromException(new IOException("gateway still restarting"));
            },
            delayAsync: pacer.DelayAsync);

        loop.Start();

        // Drive 20 attempts: four times the default budget, all after it would have expired.
        await pacer.AdvanceAsync(20);

        dials.ShouldBeGreaterThan(DefaultPolicyAttemptBudget);
        dials.ShouldBe(20);
        loop.IsReconnecting.ShouldBeTrue();
    }

    /// <summary>
    /// The backoff must actually grow and then hold at the cap. A flat short interval would be a
    /// tight loop against a down gateway; an uncapped exponential would mean a gateway that returns
    /// after 45 minutes goes unnoticed for a further long interval.
    /// </summary>
    [Fact]
    public async Task Loop_RequestsGrowingDelays_CappedAtCeiling()
    {
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ => Task.FromException(new IOException("down")),
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(6);

        // 2s, 4s, 8s, 16s, then held at the 30s cap. Exact schedule, not a range.
        // Take(6): the loop parks on the NEXT delay after the 6th dial, so the recorder legitimately
        // holds one extra pending request beyond the attempts that were released.
        pacer.Requested.Take(6).ShouldBe(
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        ]);
    }

    /// <summary>
    /// AC8 / long-outage case: many failures, then success. Asserts the delay reached the cap, never
    /// exceeded it, and that recovery ran on the first successful attempt.
    /// </summary>
    [Fact]
    public async Task Loop_RecoversAfterLongOutage_WithDelayHeldAtCapAndNeverExceedingIt()
    {
        const int failuresBeforeRecovery = 200; // ~100 minutes of simulated outage at the cap.
        var dials = 0;
        var recovered = 0;
        var pacer = new DelayPacer();

        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ =>
            {
                var attempt = Interlocked.Increment(ref dials);
                if (attempt <= failuresBeforeRecovery)
                    return Task.FromException(new IOException("gateway down"));

                Interlocked.Increment(ref recovered);
                return Task.CompletedTask;
            },
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(failuresBeforeRecovery + 1);

        dials.ShouldBe(failuresBeforeRecovery + 1);

        // Recovery ran exactly once, on the first attempt that succeeded.
        recovered.ShouldBe(1);

        // The delay reached the cap and never exceeded it, no matter how long the outage ran.
        pacer.Requested.ShouldContain(DesktopReconnectBackoff.MaxDelay);
        pacer.Requested.ShouldAllBe(d => d <= DesktopReconnectBackoff.MaxDelay);
        pacer.Requested[^1].ShouldBe(DesktopReconnectBackoff.MaxDelay);

        // A successful dial ends the outage: the loop stops retrying and clears its state.
        loop.IsReconnecting.ShouldBeFalse();
        loop.AttemptCount.ShouldBe(0);
    }

    /// <summary>
    /// The #2564 failure mode: a counter that resets on each failure makes the ceiling inert and the
    /// loop degenerates into a fixed-rate poll. Asserts the delay never regresses to the base value
    /// once it has climbed.
    /// </summary>
    [Fact]
    public async Task Loop_NeverResetsBackoffMidOutage()
    {
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ => Task.FromException(new IOException("down")),
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(30);

        // Monotonic non-decreasing: every delay is >= its predecessor.
        for (var i = 1; i < pacer.Requested.Count; i++)
            pacer.Requested[i].ShouldBeGreaterThanOrEqualTo(pacer.Requested[i - 1]);

        // And it did not slide back to the short initial interval after climbing.
        pacer.Requested.Skip(5).ShouldAllBe(d => d == DesktopReconnectBackoff.MaxDelay);
    }

    /// <summary>
    /// The user must be able to see the difference between "gave up" and "still trying". The loop
    /// signals the reconnecting state on start and clears it on recovery.
    /// </summary>
    [Fact]
    public async Task Loop_SignalsReconnectingState_ToObservers()
    {
        var transitions = new List<bool>();
        var dials = 0;
        var pacer = new DelayPacer();

        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ => Interlocked.Increment(ref dials) < 3
                ? Task.FromException(new IOException("down"))
                : Task.CompletedTask,
            delayAsync: pacer.DelayAsync);

        loop.OnReconnectStateChanged += () => transitions.Add(loop.IsReconnecting);

        loop.IsReconnecting.ShouldBeFalse();
        loop.Start();
        transitions.ShouldContain(true);

        await pacer.AdvanceAsync(3);

        transitions.ShouldBe([true, false]);
        loop.IsReconnecting.ShouldBeFalse();
    }

    /// <summary>
    /// Repeated <c>Closed</c> notifications during one outage must not stack parallel dial loops --
    /// that would multiply the request rate against a gateway that is already down.
    /// </summary>
    [Fact]
    public async Task Loop_StartIsIdempotent_DuringAnOutage()
    {
        var dials = 0;
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ =>
            {
                Interlocked.Increment(ref dials);
                return Task.FromException(new IOException("down"));
            },
            delayAsync: pacer.DelayAsync);

        loop.Start();
        loop.Start();
        loop.Start();

        await pacer.AdvanceAsync(4);

        // One loop, one dial per released delay -- not three.
        dials.ShouldBe(4);
    }

    /// <summary>
    /// A dial that throws must not kill the loop. Swallowing-and-continuing is the whole point here:
    /// a failed dial against a restarting gateway is the expected case, and an exit would restore the
    /// exact terminal-close defect being fixed.
    /// </summary>
    [Fact]
    public async Task Loop_SurvivesThrowingDial_AndContinues()
    {
        var dials = 0;
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ =>
            {
                var n = Interlocked.Increment(ref dials);
                throw new InvalidOperationException($"dial {n} blew up");
            },
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(8);

        dials.ShouldBe(8);
        loop.IsReconnecting.ShouldBeTrue();
    }

    /// <summary>
    /// Stopping (page teardown, or SignalR's own reconnect winning the race) must halt the loop so no
    /// dial runs over a live socket.
    /// </summary>
    [Fact]
    public async Task Loop_StopsDialling_AfterStopAsync()
    {
        var dials = 0;
        var pacer = new DelayPacer();
        await using var loop = new DesktopReconnectLoop(
            dialAsync: _ =>
            {
                Interlocked.Increment(ref dials);
                return Task.FromException(new IOException("down"));
            },
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(3);
        dials.ShouldBe(3);

        await loop.StopAsync();
        var dialsAtStop = dials;

        // Release any further waits; nothing more should dial.
        pacer.ReleaseAll();
        await Task.Delay(50);

        dials.ShouldBe(dialsAtStop);
        loop.IsReconnecting.ShouldBeFalse();
    }

    // ── Backoff schedule ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(5, 30)]
    [InlineData(1000, 30)]
    [InlineData(int.MaxValue, 30)]
    public void Backoff_FollowsCappedExponentialSchedule(int attempt, int expectedSeconds)
        => DesktopReconnectBackoff.GetDelay(attempt).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    /// <summary>
    /// There is no attempt index at which the schedule signals "stop". Every value is a positive,
    /// finite delay bounded by the cap -- the property that makes the retry indefinite.
    /// </summary>
    [Fact]
    public void Backoff_NeverReturnsATerminalValue()
    {
        foreach (var attempt in new[] { 0, 1, 5, 50, 5_000, 1_000_000, int.MaxValue })
        {
            var delay = DesktopReconnectBackoff.GetDelay(attempt);
            delay.ShouldBeGreaterThan(TimeSpan.Zero);
            delay.ShouldBeLessThanOrEqualTo(DesktopReconnectBackoff.MaxDelay);
        }
    }

    [Fact]
    public void Backoff_RejectsNegativeAttempt()
        => Should.Throw<ArgumentOutOfRangeException>(() => DesktopReconnectBackoff.GetDelay(-1));

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Delay seam that records every requested duration and releases waits on demand, so tests assert
    /// the durations REQUESTED and never the time actually slept. Modelled on the DelayTool recorder
    /// seam (PR #2589).
    /// </summary>
    private sealed class DelayPacer
    {
        private readonly List<TimeSpan> _requested = [];
        private readonly object _sync = new();
        private TaskCompletionSource? _pending;
        private bool _releaseAll;

        /// <summary>Every duration the loop asked to wait, in order.</summary>
        public IReadOnlyList<TimeSpan> Requested
        {
            get { lock (_sync) return _requested.ToArray(); }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            TaskCompletionSource tcs;
            lock (_sync)
            {
                _requested.Add(delay);
                if (_releaseAll)
                    return Task.CompletedTask;

                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = tcs;
            }

            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        /// <summary>
        /// Releases <paramref name="count"/> waits, letting the loop perform that many dials. Waits
        /// for each dial to complete before releasing the next, so the sequence is deterministic.
        /// </summary>
        public async Task AdvanceAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var released = await ReleaseOneAsync();
                if (!released)
                    return;
            }
        }

        private async Task<bool> ReleaseOneAsync()
        {
            // Wait for the loop to park on a delay before releasing it. Bounded by a generous
            // scheduling allowance -- this is a handshake, not a timing assertion.
            for (var spins = 0; spins < 500; spins++)
            {
                TaskCompletionSource? tcs;
                lock (_sync)
                    tcs = _pending;

                if (tcs is not null)
                {
                    lock (_sync)
                        _pending = null;

                    var before = Requested.Count;
                    tcs.TrySetResult();

                    // Let the dial run and the next delay be requested (or the loop finish).
                    for (var wait = 0; wait < 500; wait++)
                    {
                        await Task.Yield();
                        await Task.Delay(1);
                        lock (_sync)
                        {
                            if (_pending is not null || Requested.Count > before)
                                return true;
                        }
                    }

                    return true;
                }

                await Task.Delay(1);
            }

            return false;
        }

        /// <summary>Makes all present and future waits complete immediately.</summary>
        public void ReleaseAll()
        {
            lock (_sync)
            {
                _releaseAll = true;
                _pending?.TrySetResult();
                _pending = null;
            }
        }
    }
}
