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

}
