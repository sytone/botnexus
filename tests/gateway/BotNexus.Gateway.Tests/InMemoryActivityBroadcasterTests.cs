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
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = subscription.MoveNextAsync().AsTask();
        await Task.Delay(20, cts.Token);

        await broadcaster.PublishAsync(CreateActivity(), cts.Token);

        (await moveNext).ShouldBeTrue();
    }

    [Fact]
    public async Task SubscribeAsync_WithMultipleSubscribers_DeliversToEachSubscriber()
    {
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var first = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var second = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMoveNext = first.MoveNextAsync().AsTask();
        var secondMoveNext = second.MoveNextAsync().AsTask();
        await Task.Delay(20, cts.Token);

        await broadcaster.PublishAsync(CreateActivity(), cts.Token);

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
}
