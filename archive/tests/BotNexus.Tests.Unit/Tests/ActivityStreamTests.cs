using BotNexus.Core.Bus;
using BotNexus.Core.Models;
using FluentAssertions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class ActivityStreamTests
{
    private static ActivityEvent CreateEvent(
        ActivityEventType type = ActivityEventType.MessageReceived,
        string content = "test") =>
        new(type, "websocket", "ws:chat1", "chat1", "user1", content, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Publish_SingleSubscriber_ReceivesEvent()
    {
        var stream = new ActivityStream();
        using var sub = stream.Subscribe();

        var evt = CreateEvent(content: "hello");
        await stream.PublishAsync(evt);

        var received = await sub.ReadAsync();
        received.Content.Should().Be("hello");
        received.EventType.Should().Be(ActivityEventType.MessageReceived);
    }

    [Fact]
    public async Task Publish_MultipleSubscribers_AllReceiveEvent()
    {
        var stream = new ActivityStream();
        using var sub1 = stream.Subscribe();
        using var sub2 = stream.Subscribe();

        var evt = CreateEvent(content: "broadcast");
        await stream.PublishAsync(evt);

        var r1 = await sub1.ReadAsync();
        var r2 = await sub2.ReadAsync();

        r1.Content.Should().Be("broadcast");
        r2.Content.Should().Be("broadcast");
    }

    [Fact]
    public async Task Subscribe_Dispose_StopsReceiving()
    {
        var stream = new ActivityStream();
        var sub = stream.Subscribe();
        sub.Dispose();

        // Publishing after dispose should not throw
        await stream.PublishAsync(CreateEvent());

        // The reader should be completed — ReadAllAsync should terminate
        var items = new List<ActivityEvent>();
        await foreach (var item in sub.ReadAllAsync())
            items.Add(item);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsStreamOfEvents()
    {
        var stream = new ActivityStream();
        using var sub = stream.Subscribe();

        for (int i = 0; i < 5; i++)
            await stream.PublishAsync(CreateEvent(content: $"msg{i}"));

        // Read 5 events
        var received = new List<ActivityEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var evt in sub.ReadAllAsync(cts.Token))
        {
            received.Add(evt);
            if (received.Count >= 5) break;
        }

        received.Should().HaveCount(5);
        received.Select(e => e.Content).Should().Equal("msg0", "msg1", "msg2", "msg3", "msg4");
    }

    [Fact]
    public async Task ReadAsync_Cancellation_ThrowsOperationCanceled()
    {
        var stream = new ActivityStream();
        using var sub = stream.Subscribe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sub.ReadAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Publish_NoSubscribers_DoesNotThrow()
    {
        var stream = new ActivityStream();
        var act = async () => await stream.PublishAsync(CreateEvent());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Publish_AfterSubscriberDisposed_DoesNotThrow()
    {
        var stream = new ActivityStream();
        var sub = stream.Subscribe();
        sub.Dispose();

        var act = async () => await stream.PublishAsync(CreateEvent());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Publish_EventProperties_ArePreserved()
    {
        var stream = new ActivityStream();
        using var sub = stream.Subscribe();

        var ts = DateTimeOffset.UtcNow;
        var evt = new ActivityEvent(
            ActivityEventType.ResponseSent,
            "telegram",
            "tg:chat42",
            "chat42",
            "bot1",
            "agent response",
            ts);

        await stream.PublishAsync(evt);
        var received = await sub.ReadAsync();

        received.EventType.Should().Be(ActivityEventType.ResponseSent);
        received.Channel.Should().Be("telegram");
        received.SessionKey.Should().Be("tg:chat42");
        received.ChatId.Should().Be("chat42");
        received.SenderId.Should().Be("bot1");
        received.Content.Should().Be("agent response");
        received.Timestamp.Should().Be(ts);
        received.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Concurrent_PublishAndSubscribe_Works()
    {
        var stream = new ActivityStream();
        using var sub = stream.Subscribe();
        var count = 50;

        var publishTask = Task.Run(async () =>
        {
            for (int i = 0; i < count; i++)
                await stream.PublishAsync(CreateEvent(content: $"p{i}"));
        });

        var received = new List<ActivityEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var evt in sub.ReadAllAsync(cts.Token))
            {
                received.Add(evt);
                if (received.Count >= count) break;
            }
        });

        await Task.WhenAll(publishTask, consumeTask);
        received.Should().HaveCount(count);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var stream = new ActivityStream();
        var sub = stream.Subscribe();
        var act = () =>
        {
            sub.Dispose();
            sub.Dispose();
        };
        act.Should().NotThrow();
    }
}
