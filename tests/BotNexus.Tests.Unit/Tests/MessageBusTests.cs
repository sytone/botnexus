using BotNexus.Core.Bus;
using BotNexus.Core.Models;
using FluentAssertions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class MessageBusTests
{
    private static InboundMessage CreateMessage(string content = "test") =>
        new("telegram", "user1", "chat1", content, DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

    [Fact]
    public async Task PublishAndRead_RoundTrips_Message()
    {
        var bus = new MessageBus();
        var message = CreateMessage("hello world");

        await bus.PublishAsync(message);
        var received = await bus.ReadAsync();

        received.Should().Be(message);
    }

    [Fact]
    public async Task PublishMultiple_ReadAllAsync_ReturnsAllMessages()
    {
        var bus = new MessageBus();
        var messages = Enumerable.Range(1, 5)
            .Select(i => CreateMessage($"msg{i}"))
            .ToList();

        foreach (var msg in messages)
            await bus.PublishAsync(msg);

        bus.Complete();

        var received = new List<InboundMessage>();
        await foreach (var msg in bus.ReadAllAsync())
            received.Add(msg);

        received.Should().HaveCount(5);
        received.Select(m => m.Content).Should().Equal(messages.Select(m => m.Content));
    }

    [Fact]
    public async Task ReadAsync_CancellationToken_ThrowsOperationCanceled()
    {
        var bus = new MessageBus();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await bus.ReadAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublishAsync_Concurrent_AllMessagesDelivered()
    {
        var bus = new MessageBus(capacity: 100);
        var count = 50;

        var publishTasks = Enumerable.Range(0, count)
            .Select(i => bus.PublishAsync(CreateMessage($"msg{i}")).AsTask());
        await Task.WhenAll(publishTasks);

        var received = new List<InboundMessage>();
        for (int i = 0; i < count; i++)
            received.Add(await bus.ReadAsync());

        received.Should().HaveCount(count);
    }
}
