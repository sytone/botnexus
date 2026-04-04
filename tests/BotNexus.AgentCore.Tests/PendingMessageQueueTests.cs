using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using FluentAssertions;

namespace BotNexus.AgentCore.Tests;

public class PendingMessageQueueTests
{
    [Fact]
    public void Drain_AllMode_ReturnsAllItems()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.Enqueue(new UserMessage("one"));
        queue.Enqueue(new UserMessage("two"));

        var drained = queue.Drain();

        drained.Should().HaveCount(2);
        queue.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Drain_OneAtATimeMode_ReturnsOneItemPerDrain()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(new UserMessage("one"));
        queue.Enqueue(new UserMessage("two"));

        var first = queue.Drain();
        var second = queue.Drain();
        var third = queue.Drain();

        first.Should().ContainSingle();
        second.Should().ContainSingle();
        third.Should().BeEmpty();
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.Enqueue(new UserMessage("one"));
        queue.Enqueue(new UserMessage("two"));

        queue.Clear();

        queue.Drain().Should().BeEmpty();
        queue.HasItems.Should().BeFalse();
    }

    [Fact]
    public void HasItems_ReflectsQueueState()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.HasItems.Should().BeFalse();

        queue.Enqueue(new UserMessage("one"));
        queue.HasItems.Should().BeTrue();

        queue.Drain();
        queue.HasItems.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentEnqueueAndDrain_IsThreadSafe()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        const int totalMessages = 800;
        const int producerCount = 8;
        var perProducer = totalMessages / producerCount;
        var produced = 0;
        var consumed = 0;

        var producers = Enumerable.Range(0, producerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    queue.Enqueue(new UserMessage($"item-{Interlocked.Increment(ref produced)}"));
                }
            }))
            .ToArray();
        var producersTask = Task.WhenAll(producers);

        var consumer = Task.Run(async () =>
        {
            while (!producersTask.IsCompleted || queue.HasItems)
            {
                var drained = queue.Drain();
                Interlocked.Add(ref consumed, drained.Count);
                await Task.Yield();
            }
        });

        await producersTask;
        await consumer;

        consumed.Should().Be(totalMessages);
        queue.HasItems.Should().BeFalse();
    }
}
