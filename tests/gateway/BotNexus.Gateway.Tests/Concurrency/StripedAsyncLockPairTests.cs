using BotNexus.Gateway.Abstractions.Concurrency;

namespace BotNexus.Gateway.Tests.Concurrency;

public sealed class StripedAsyncLockPairTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task AcquirePairAsync_HoldsDistinctStripesOnce_AndDisposesIdempotently(int first, int second)
    {
        var locks = new StripedAsyncLock(2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pair = await locks.AcquirePairAsync(first, second, deadline.Token);
        var waiter = locks.AcquireAsync(first, deadline.Token);
        waiter.IsCompleted.ShouldBeFalse();
        pair.Dispose();
        pair.Dispose();
        using var acquired = await waiter;
        var another = locks.AcquireAsync(first, deadline.Token);
        another.IsCompleted.ShouldBeFalse("double disposal must not release a held stripe twice");
        acquired.Dispose();
        (await another).Dispose();
        (await locks.AcquireAsync(second, deadline.Token)).Dispose();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task AcquirePairAsync_OrdersPhysicalStripes_AndReleasesPartialAcquisitionOnCancellation(int first, int second)
    {
        var locks = new StripedAsyncLock(2);
        using var heldHighStripe = await locks.AcquireAsync(1);
        using var cancellation = new CancellationTokenSource();
        var pair = locks.AcquirePairAsync(first, second, cancellation.Token);
        pair.IsCompleted.ShouldBeFalse();
        // WaitAsync consumes an available semaphore synchronously before the pair awaits
        // the held high stripe, so this proves ordering without sleeps or racing tasks.
        var lowWaiter = locks.AcquireAsync(0);
        lowWaiter.IsCompleted.ShouldBeFalse("both input orders must hold the lower stripe first");
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => { (await pair).Dispose(); });
        using var releasedLow = await lowWaiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquirePairAsync_PreCanceled_DoesNotStrandAnyStripe()
    {
        var locks = new StripedAsyncLock(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => { (await locks.AcquirePairAsync(0, 1, cancellation.Token)).Dispose(); });
        (await locks.AcquireAsync(0)).Dispose();
        (await locks.AcquireAsync(1)).Dispose();
    }
}
