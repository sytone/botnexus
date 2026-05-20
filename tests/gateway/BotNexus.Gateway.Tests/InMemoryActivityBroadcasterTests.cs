using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Activity;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class InMemoryActivityBroadcasterTests
{
    [Fact]
    public async Task PublishAsync_WithoutSubscribers_DoesNotThrow()
    {
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);

        Func<Task> act = () => broadcaster.PublishAsync(CreateActivity()).AsTask();

        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesPublishedEvents()
    {
        using var readySignal = new SemaphoreSlim(0, 1);
        var broadcaster = new ReadySignalingBroadcaster(readySignal);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var subscription = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = subscription.MoveNextAsync().AsTask();

        // Wait until the subscriber has entered WaitToReadAsync before publishing.
        await readySignal.WaitAsync(cts.Token);

        await broadcaster.PublishAsync(CreateActivity(), cts.Token);

        (await moveNext).ShouldBeTrue();
    }

    [Fact]
    public async Task SubscribeAsync_WithMultipleSubscribers_DeliversToEachSubscriber()
    {
        using var sharedSignal1 = new SemaphoreSlim(0, 1);
        using var sharedSignal2 = new SemaphoreSlim(0, 1);
        var shared = new TwoSubscriberReadyBroadcaster(sharedSignal1, sharedSignal2);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var first = shared.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var second = shared.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMoveNext = first.MoveNextAsync().AsTask();
        var secondMoveNext = second.MoveNextAsync().AsTask();

        // Wait until both subscribers are ready before publishing.
        await Task.WhenAll(sharedSignal1.WaitAsync(cts.Token), sharedSignal2.WaitAsync(cts.Token));

        await shared.PublishAsync(CreateActivity(), cts.Token);

        new[] { await firstMoveNext, await secondMoveNext }.ShouldAllBe(x => x);
    }

    [Fact]
    public async Task SubscribeAsync_WhenCancelled_EndsSubscription()
    {
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);
        using var cts = new CancellationTokenSource();
        await using var subscription = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = subscription.MoveNextAsync().AsTask();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    private static GatewayActivity CreateActivity()
        => new()
        {
            Type = GatewayActivityType.System,
            Message = "activity"
        };

    // ---------------------------------------------------------------------------
    // Test helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Wraps <see cref="InMemoryActivityBroadcaster"/> and signals a <see cref="SemaphoreSlim"/>
    /// immediately before the async iterator blocks on <c>MoveNextAsync</c>. This eliminates the
    /// <c>Task.Delay(20)</c> subscriber-ready race (issue #374).
    /// </summary>
    private sealed class ReadySignalingBroadcaster(SemaphoreSlim readySignal)
    {
        private readonly InMemoryActivityBroadcaster _inner =
            new(NullLogger<InMemoryActivityBroadcaster>.Instance);

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken ct = default)
            => _inner.PublishAsync(activity, ct);

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await using var enumerator = _inner.SubscribeAsync(ct).GetAsyncEnumerator(ct);

            // Signal that this subscriber is about to call MoveNextAsync (which internally
            // enters WaitToReadAsync). The caller awaits this before publishing.
            readySignal.Release();

            while (await enumerator.MoveNextAsync())
                yield return enumerator.Current;
        }
    }

    /// <summary>
    /// Like <see cref="ReadySignalingBroadcaster"/> but supports two independent subscribers,
    /// each signalling its own <see cref="SemaphoreSlim"/> when ready.
    /// </summary>
    private sealed class TwoSubscriberReadyBroadcaster(SemaphoreSlim signal1, SemaphoreSlim signal2)
    {
        private readonly InMemoryActivityBroadcaster _inner =
            new(NullLogger<InMemoryActivityBroadcaster>.Instance);
        private int _subscribeCount;

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken ct = default)
            => _inner.PublishAsync(activity, ct);

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Pick which signal to use based on subscribe order (1st vs 2nd caller).
            var index = Interlocked.Increment(ref _subscribeCount);
            var signal = index == 1 ? signal1 : signal2;

            await using var enumerator = _inner.SubscribeAsync(ct).GetAsyncEnumerator(ct);

            signal.Release();

            while (await enumerator.MoveNextAsync())
                yield return enumerator.Current;
        }
    }
}
