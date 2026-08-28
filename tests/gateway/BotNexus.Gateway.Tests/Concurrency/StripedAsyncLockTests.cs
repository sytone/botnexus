using BotNexus.Gateway.Abstractions.Concurrency;

namespace BotNexus.Gateway.Tests.Concurrency;

public sealed class StripedAsyncLockTests
{
    [Fact]
    public void StripeCount_IsFixed_AndDoesNotGrowWithDistinctKeys()
    {
        var locks = new StripedAsyncLock(stripeCount: 16);
        locks.StripeCount.ShouldBe(16);

        // Touching thousands of distinct keys must not change the stripe count:
        // the whole point is a bounded, fixed pool (no per-key leak).
        for (var i = 0; i < 10_000; i++)
        {
            _ = locks.GetStripe(i);
        }

        locks.StripeCount.ShouldBe(16);
    }

    [Fact]
    public void GetStripe_IsStableForSameKey()
    {
        var locks = new StripedAsyncLock(stripeCount: 64);

        var a = locks.GetStripe("session-1");
        var b = locks.GetStripe("session-1");

        a.ShouldBeSameAs(b);
    }

    [Fact]
    public async Task AcquireAsync_SerializesCallersOnTheSameStripe()
    {
        var locks = new StripedAsyncLock(stripeCount: 1);
        using var first = await locks.AcquireAsync("first-key");

        var waiters = Enumerable.Range(0, 10)
            .Select(index => locks.AcquireAsync($"key-{index}"))
            .ToList();

        waiters.ShouldAllBe(waiter => !waiter.IsCompleted);

        first.Dispose();
        foreach (var waiter in waiters)
        {
            using var acquired = await waiter;
        }
    }

    [Fact]
    public async Task AcquireAsync_AllowsConcurrency_AcrossDifferentStripes()
    {
        var locks = new StripedAsyncLock(stripeCount: 256);

        // int.GetHashCode() is the value itself, so non-negative key i maps to stripe
        // (i % StripeCount). Keys 0 and 1 are therefore on distinct stripes.
        using var first = await locks.AcquireAsync(0);

        var acquireSecond = locks.AcquireAsync(1);
        acquireSecond.IsCompleted.ShouldBeTrue("a different stripe must not block");
        (await acquireSecond).Dispose();
    }

    [Fact]
    public async Task ReleasingHandle_FreesTheStripe_ForTheNextCaller()
    {
        var locks = new StripedAsyncLock(stripeCount: 1);

        var handle = await locks.AcquireAsync("k");
        // A second acquire cannot complete while the first is held.
        var second = locks.AcquireAsync("k");
        second.IsCompleted.ShouldBeFalse();

        handle.Dispose(); // release

        var secondHandle = await second.WaitAsync(TimeSpan.FromSeconds(2));
        secondHandle.Dispose();
    }

    [Fact]
    public async Task Stripe_IsReleased_EvenWhenBodyThrows()
    {
        var locks = new StripedAsyncLock(stripeCount: 1);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using (await locks.AcquireAsync("k"))
            {
                throw new InvalidOperationException("boom");
            }
        });

        // If the stripe had been stranded, this acquire would deadlock.
        var reacquire = locks.AcquireAsync("k");
    reacquire.IsCompleted.ShouldBeTrue("an exception in the body must still release the stripe");
        (await reacquire).Dispose();
    }

    [Fact]
    public async Task DoubleDispose_DoesNotOverRelease()
    {
        var locks = new StripedAsyncLock(stripeCount: 1);

        var handle = await locks.AcquireAsync("k");
        handle.Dispose();
        handle.Dispose(); // second dispose must be a no-op (no extra Release)

        // The stripe should hold at most one permit: a fresh acquire succeeds, but a
        // SECOND concurrent acquire must still block (proving the count was not
        // corrupted to 2 by a double release).
        using var a = await locks.AcquireAsync("k");
        var b = locks.AcquireAsync("k");
        b.IsCompleted.ShouldBeFalse();
        a.Dispose();
        (await b).Dispose();
    }

    [Fact]
    public void Constructor_RejectsNonPositiveStripeCount()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new StripedAsyncLock(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new StripedAsyncLock(-4));
    }

    // ── #3517: the bounded acquire ──────────────────────────────────────────────────────

    [Fact]
    public async Task BoundedAcquire_OnAFreeStripe_SucceedsImmediately()
    {
        // Non-vacuity for everything below: the bound must not be the thing that fails an
        // uncontended acquire.
        var locks = new StripedAsyncLock(stripeCount: 1);

        var acquire = locks.AcquireAsync("k", TimeSpan.FromSeconds(30));

        acquire.IsCompleted.ShouldBeTrue("an uncontended stripe is available synchronously");
        (await acquire).Dispose();
    }

    [Fact]
    public async Task BoundedAcquire_OnAHeldStripe_ThrowsStripeLockTimeout_NamingTheKeyAndTheBound()
    {
        // #3517's core: the wait must END, and it must end with something an operator can read.
        // The production signature was a bare TaskCanceledException raised from inside
        // SemaphoreSlim.WaitAsync with CancellationToken.None - a stack that says "cancelled" when
        // nothing was cancelled, which is what made 154 identical errors undiagnosable.
        //
        // The bound is a real (small) one rather than zero so this exercises the timed WaitAsync
        // path, not a fast-path special case. It is deterministic: the stripe is held for the whole
        // test and is never released, so the timeout is the only possible outcome.
        var locks = new StripedAsyncLock(stripeCount: 1);
        using var held = await locks.AcquireAsync("conv-wedged");

        var ex = await Should.ThrowAsync<StripeLockTimeoutException>(
            async () => await locks.AcquireAsync("conv-wedged", TimeSpan.FromMilliseconds(50)));

        ex.Key.ShouldBe("conv-wedged", "the message has to name WHICH key was contended");
        ex.Timeout.ShouldBe(TimeSpan.FromMilliseconds(50));
        ex.Message.ShouldContain("conv-wedged");
        ex.ShouldBeAssignableTo<TimeoutException>(
            "contention is a timeout, not a cancellation - a caller catching OperationCanceledException must not swallow it");
        ex.ShouldNotBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task BoundedAcquire_ThatTimesOut_DoesNotConsumeThePermit()
    {
        // A timed-out waiter that had actually taken the semaphore would leave the stripe
        // permanently over-held - converting a transient contention into the exact permanent wedge
        // the fix exists to prevent.
        var locks = new StripedAsyncLock(stripeCount: 1);
        var held = await locks.AcquireAsync("k");

        await Should.ThrowAsync<StripeLockTimeoutException>(
            async () => await locks.AcquireAsync("k", TimeSpan.FromMilliseconds(50)));

        held.Dispose();

        var reacquire = locks.AcquireAsync("k", TimeSpan.FromSeconds(30));
        reacquire.IsCompleted.ShouldBeTrue("the timed-out waiter must not have taken the permit");
        (await reacquire).Dispose();
    }

    [Fact]
    public async Task BoundedAcquire_StillHonoursCancellation_AsCancellationNotAsTimeout()
    {
        // The two outcomes are deliberately distinct. "My caller went away" is routine and
        // retryable; "somebody is holding this and will not let go" is a stuck peer an operator has
        // to hear about. Collapsing them would put the fix back where it started.
        var locks = new StripedAsyncLock(stripeCount: 1);
        using var held = await locks.AcquireAsync("k");
        using var cts = new CancellationTokenSource();

        var acquire = locks.AcquireAsync("k", Timeout.InfiniteTimeSpan, cts.Token);
        acquire.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await acquire);
    }

    [Fact]
    public async Task BoundedAcquire_ReleasesTheStripe_WhenTheHolderLetsGoInTime()
    {
        // The bound must not turn a NORMAL hand-off into a failure.
        var locks = new StripedAsyncLock(stripeCount: 1);
        var held = await locks.AcquireAsync("k");

        var waiter = locks.AcquireAsync("k", TimeSpan.FromSeconds(30));
        waiter.IsCompleted.ShouldBeFalse();

        held.Dispose();

        (await waiter.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task BoundedAcquire_RejectsANonPositiveTimeout()
    {
        // Zero or negative would silently mean "never wait", which is not a bound - it is a
        // different operation, and one no caller here wants by accident. InfiniteTimeSpan stays
        // legal as the explicit opt-out.
        var locks = new StripedAsyncLock(stripeCount: 1);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await locks.AcquireAsync("k", TimeSpan.Zero));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await locks.AcquireAsync("k", TimeSpan.FromSeconds(-1)));

        (await locks.AcquireAsync("k", Timeout.InfiniteTimeSpan)).Dispose();
    }
}
