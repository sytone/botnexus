using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the mobile reconnect retry loop that fixes "retries forever without reconnecting"
/// after a gateway restart (#2625).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No wall-clock assertions.</strong> The loop takes a delay seam, so every timing assertion
/// here reads the <em>durations requested</em> from a recorder rather than measuring elapsed time.
/// Nothing in this file sleeps for a backoff interval, so nothing here can flake on a slow machine.
/// There is no <c>Stopwatch</c>, no <c>Skip=</c>, and no conditional skip.
/// </para>
/// <para>
/// The recorder also paces the loop: because the loop awaits the delay seam before each attempt, a
/// recorder that releases each delay on demand drives an arbitrary number of simulated attempts
/// instantly, including the multi-hour-outage case.
/// </para>
/// </remarks>
public sealed class MobileReconnectLoopTests
{
    /// <summary>
    /// Attempts past this index are ones no in-budget policy would have made. The framework's stock
    /// <c>WithAutomaticReconnect()</c> budget is ~5 retries x 3s; more importantly, once a rebuild's
    /// <c>StartAsync</c> has failed there is no started connection running <em>any</em> automatic
    /// reconnect, so past this point the loop is the sole recovery mechanism.
    /// </summary>
    private const int DefaultPolicyAttemptBudget = 5;

    // ── The core regression ──────────────────────────────────────────────────

    /// <summary>
    /// THE regression test for #2625, and the one the issue explicitly says is not satisfied by
    /// asserting a retry was merely <em>attempted</em>. The old overlay treated "ResumeAsync did not
    /// throw" as the end of an attempt and checked connectivity only to decide whether to reschedule;
    /// a resume that completed while still disconnected therefore looked like progress. This asserts
    /// the full long-outage sequence: many failures, then the gateway returns and the very next
    /// attempt genuinely reconnects and ends the outage.
    /// </summary>
    [Fact]
    public async Task Loop_ReconnectsOnTheFirstAttemptAfterTheGatewayReturns()
    {
        const int failuresBeforeRecovery = 40;
        var attempts = 0;
        var connected = false;
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n > failuresBeforeRecovery)
                    connected = true;      // gateway is back: this resume genuinely restores the socket.
                return Task.FromException(new IOException("gateway restarting"));
            },
            isConnected: () => connected,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(failuresBeforeRecovery + 1);

        // It kept trying far past any in-budget policy, and then actually recovered.
        attempts.ShouldBeGreaterThan(DefaultPolicyAttemptBudget);
        attempts.ShouldBe(failuresBeforeRecovery + 1);
        connected.ShouldBeTrue();

        // The outage ended: the loop stopped retrying rather than continuing to churn.
        loop.IsRetrying.ShouldBeFalse();
        loop.AttemptCount.ShouldBe(0);

        // And it did not keep dialling over the now-live socket.
        pacer.ReleaseAll();
        await Task.Delay(50);
        attempts.ShouldBe(failuresBeforeRecovery + 1);
    }

    /// <summary>
    /// The precise defect shape: a resume that completes <em>without throwing</em> but leaves the
    /// client disconnected must NOT be treated as success. Before the fix the only signal available
    /// to the retry chain was the absence of an exception plus a reschedule decision; this pins that
    /// connectivity -- not the absence of a throw -- is what ends the outage.
    /// </summary>
    [Fact]
    public async Task Loop_KeepsRetrying_WhenResumeSucceedsButConnectionIsStillDown()
    {
        var attempts = 0;
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.CompletedTask;   // completes cleanly, restores nothing.
            },
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(12);

        attempts.ShouldBe(12);
        loop.IsRetrying.ShouldBeTrue();
    }

    /// <summary>
    /// The mirror case: a resume that <em>throws</em> but nonetheless left the connection up must be
    /// treated as success. Together with the test above this proves the success predicate is
    /// connectivity alone.
    /// </summary>
    [Fact]
    public async Task Loop_StopsRetrying_WhenResumeThrowsButConnectionCameUp()
    {
        var attempts = 0;
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("negotiate blew up after the socket landed");
            },
            isConnected: () => true,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(1);

        attempts.ShouldBe(1);
        loop.IsRetrying.ShouldBeFalse();
    }

    /// <summary>
    /// A throwing resume must not kill the loop. Exiting here would restore exactly the
    /// "no recovery mechanism left" state that #2625 is about.
    /// </summary>
    [Fact]
    public async Task Loop_SurvivesThrowingResume_AndContinues()
    {
        var attempts = 0;
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                var n = Interlocked.Increment(ref attempts);
                throw new InvalidOperationException($"resume {n} blew up");
            },
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(9);

        attempts.ShouldBe(9);
        loop.IsRetrying.ShouldBeTrue();
    }

    // ── Backoff schedule ─────────────────────────────────────────────────────

    /// <summary>
    /// The exact requested schedule: 2s, 4s, 8s, 16s, then held at the 30s cap. The cap is
    /// load-bearing in both directions -- uncapped exponential would mean a gateway returning after
    /// 45 minutes goes unnoticed for a further long interval, and no growth would be a tight loop
    /// against a down gateway.
    /// </summary>
    [Fact]
    public async Task Loop_RequestsGrowingDelays_CappedAtCeiling()
    {
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ => Task.FromException(new IOException("down")),
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(6);

        // Take(6): the loop parks on the NEXT delay after the 6th attempt, so the recorder
        // legitimately holds one extra pending request beyond the attempts released.
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
    /// Long outage: the delay reaches the cap, never exceeds it, and worst-case detection latency
    /// after the gateway returns is therefore one cap interval regardless of outage length.
    /// </summary>
    [Fact]
    public async Task Loop_HoldsAtCap_OverALongOutage()
    {
        const int attemptsToDrive = 200; // ~100 minutes of simulated outage at the cap.
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ => Task.FromException(new IOException("down")),
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(attemptsToDrive);

        pacer.Requested.ShouldContain(MobileReconnectBackoff.MaxDelay);
        pacer.Requested.ShouldAllBe(d => d <= MobileReconnectBackoff.MaxDelay);
        pacer.Requested[^1].ShouldBe(MobileReconnectBackoff.MaxDelay);
    }

    /// <summary>
    /// The #2564 shape: a counter reset mid-outage makes the ceiling inert and degenerates the loop
    /// into a fixed-rate poll. Asserts the requested delay never regresses once it has climbed.
    /// </summary>
    [Fact]
    public async Task Loop_NeverResetsBackoffMidOutage()
    {
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ => Task.FromException(new IOException("down")),
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(30);

        for (var i = 1; i < pacer.Requested.Count; i++)
            pacer.Requested[i].ShouldBeGreaterThanOrEqualTo(pacer.Requested[i - 1]);

        pacer.Requested.Skip(5).ShouldAllBe(d => d == MobileReconnectBackoff.MaxDelay);
        loop.AttemptCount.ShouldBe(30);
    }

    /// <summary>
    /// A user hammering "Retry now" must not hold the schedule at its base interval. The manual path
    /// runs an attempt out of band but deliberately leaves the attempt counter alone.
    /// </summary>
    [Fact]
    public async Task ManualRetry_DoesNotResetTheAttemptCounter()
    {
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ => Task.FromException(new IOException("down")),
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(6);
        var countBefore = loop.AttemptCount;
        countBefore.ShouldBe(6);

        await loop.RetryNowAsync();
        await loop.RetryNowAsync();

        loop.AttemptCount.ShouldBe(countBefore);
        MobileReconnectBackoff.GetDelay(loop.AttemptCount).ShouldBe(MobileReconnectBackoff.MaxDelay);
    }

    /// <summary>A manual retry that reconnects ends the outage and stops the loop.</summary>
    [Fact]
    public async Task ManualRetry_ThatReconnects_StopsTheLoop()
    {
        var connected = false;
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ => { connected = true; return Task.CompletedTask; },
            isConnected: () => connected,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        (await loop.RetryNowAsync()).ShouldBeTrue();

        loop.IsRetrying.ShouldBeFalse();
        loop.AttemptCount.ShouldBe(0);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Repeated disconnect notifications during one outage must not stack parallel loops -- that
    /// would multiply the request rate against a gateway that is already down.
    /// </summary>
    [Fact]
    public async Task Loop_StartIsIdempotent_DuringAnOutage()
    {
        var attempts = 0;
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException(new IOException("down"));
            },
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        loop.Start();
        loop.Start();

        await pacer.AdvanceAsync(4);

        attempts.ShouldBe(4);
    }

    [Fact]
    public async Task Loop_StopsAttempting_AfterStopAsync()
    {
        var attempts = 0;
        var pacer = new DelayPacer();
        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException(new IOException("down"));
            },
            isConnected: () => false,
            delayAsync: pacer.DelayAsync);

        loop.Start();
        await pacer.AdvanceAsync(3);
        attempts.ShouldBe(3);

        await loop.StopAsync();
        var atStop = attempts;

        pacer.ReleaseAll();
        await Task.Delay(50);

        attempts.ShouldBe(atStop);
        loop.IsRetrying.ShouldBeFalse();
    }

    /// <summary>The overlay needs a state signal to render "Reconnecting" and then dismiss.</summary>
    [Fact]
    public async Task Loop_SignalsRetryState_ToObservers()
    {
        var transitions = new List<bool>();
        var attempts = 0;
        var connected = false;
        var pacer = new DelayPacer();

        await using var loop = new MobileReconnectLoop(
            resumeAsync: _ =>
            {
                if (Interlocked.Increment(ref attempts) >= 3)
                    connected = true;
                return Task.CompletedTask;
            },
            isConnected: () => connected,
            delayAsync: pacer.DelayAsync);

        loop.OnRetryStateChanged += () => transitions.Add(loop.IsRetrying);

        loop.IsRetrying.ShouldBeFalse();
        loop.Start();
        transitions.ShouldContain(true);

        await pacer.AdvanceAsync(3);

        transitions.ShouldBe([true, false]);
        loop.IsRetrying.ShouldBeFalse();
    }

    [Fact]
    public void Constructor_RejectsNullDelegates()
    {
        Should.Throw<ArgumentNullException>(() => new MobileReconnectLoop(null!, () => true));
        Should.Throw<ArgumentNullException>(() => new MobileReconnectLoop(_ => Task.CompletedTask, null!));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Delay seam that records every requested duration and releases waits on demand, so tests assert
    /// the durations REQUESTED and never the time actually slept. Modelled on the DelayTool recorder
    /// seam (PR #2589) and the desktop loop's pacer (PR #2626).
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
        /// Releases <paramref name="count"/> waits, letting the loop perform that many attempts,
        /// waiting for each to complete before releasing the next so the sequence is deterministic.
        /// </summary>
        public async Task AdvanceAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!await ReleaseOneAsync())
                    return;   // loop-drain helper exit: the loop finished early (it reconnected).
            }
        }

        private async Task<bool> ReleaseOneAsync()
        {
            // Wait for the loop to park on a delay before releasing it. This is a handshake with a
            // generous scheduling allowance, not a timing assertion.
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

                    // Let the attempt run and the next delay be requested (or the loop finish).
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
