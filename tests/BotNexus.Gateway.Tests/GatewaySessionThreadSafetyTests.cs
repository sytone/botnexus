using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class GatewaySessionThreadSafetyTests
{
    [Fact]
    public async Task AddEntry_WithConcurrentWriters_DoesNotCorruptHistory()
    {
        var session = CreateSession();
        const int totalEntries = 500;

        var writers = Enumerable.Range(0, totalEntries)
            .Select(i => Task.Run(() => session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" })));
        await Task.WhenAll(writers);

        var snapshot = session.GetHistorySnapshot();
        snapshot.Should().HaveCount(totalEntries);
        snapshot.Select(e => e.Content).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetHistorySnapshot_DuringConcurrentMutation_ReturnsConsistentSnapshots()
    {
        var session = CreateSession();
        var cts = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 250; i++)
            {
                session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"m-{i}" });
                await Task.Yield();
            }

            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var snapshot = session.GetHistorySnapshot();
                    snapshot.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Role));
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task AddEntries_WhenObservedConcurrently_IsAtomicPerBatch()
    {
        var session = CreateSession();
        const int batchSize = 4;
        const int batchCount = 100;
        var cts = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();

        var writer = Task.Run(async () =>
        {
            for (var batch = 0; batch < batchCount; batch++)
            {
                var batchEntries = Enumerable.Range(0, batchSize)
                    .Select(i => new SessionEntry { Role = MessageRole.User, Content = $"batch-{batch}-entry-{i}" });
                session.AddEntries(batchEntries);
                await Task.Yield();
            }

            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = session.GetHistorySnapshot();
                var grouped = snapshot
                    .GroupBy(e =>
                    {
                        var markerIndex = e.Content.LastIndexOf("-entry-", StringComparison.Ordinal);
                        return markerIndex >= 0 ? e.Content[..markerIndex] : e.Content;
                    });

                foreach (var group in grouped)
                {
                    if (group.Count() != batchSize)
                    {
                        errors.Enqueue($"Batch '{group.Key}' had non-atomic visibility with {group.Count()} entries.");
                    }
                }
            }
        });

        await Task.WhenAll(writer, reader);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void GetHistorySnapshot_WithPagination_ReturnsRequestedSegment()
    {
        var session = CreateSession();
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" });

        var snapshot = session.GetHistorySnapshot(offset: 3, limit: 4);

        snapshot.Should().HaveCount(4);
        snapshot[0].Content.Should().Be("entry-3");
        snapshot[^1].Content.Should().Be("entry-6");
    }

    [Fact]
    public void AllocateSequenceId_WhenCalledSequentially_IncrementsMonotonically()
    {
        var session = CreateSession();

        var first = session.AllocateSequenceId();
        var second = session.AllocateSequenceId();
        var third = session.AllocateSequenceId();

        first.Should().Be(1);
        second.Should().Be(2);
        third.Should().Be(3);
    }

    [Fact]
    public void AddStreamEvent_WithReplayWindow_KeepsLatestBoundedEvents()
    {
        var session = CreateSession();

        session.AddStreamEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 2);
        session.AddStreamEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 2);
        session.AddStreamEvent(3, """{"type":"pong","sequenceId":3}""", replayWindowSize: 2);

        var replay = session.GetStreamEventSnapshot();
        replay.Should().HaveCount(2);
        replay.Select(evt => evt.SequenceId).Should().ContainInOrder(2, 3);
    }

    [Fact]
    public void GetStreamEventsAfter_WithNoMissedMessages_ReturnsEmpty()
    {
        var session = CreateSession();
        session.AddStreamEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 10);
        session.AddStreamEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 10);

        var replay = session.GetStreamEventsAfter(lastSequenceId: 2, maxReplayCount: 10);

        replay.Should().BeEmpty();
    }

    [Fact]
    public void ReplayBuffer_WhenUsedDirectly_RemainsCompatibleWithSessionReplayApis()
    {
        var session = CreateSession();

        session.ReplayBuffer.AddStreamEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 2);
        session.ReplayBuffer.AddStreamEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 2);
        session.ReplayBuffer.AddStreamEvent(3, """{"type":"pong","sequenceId":3}""", replayWindowSize: 2);

        session.GetStreamEventSnapshot().Select(evt => evt.SequenceId).Should().ContainInOrder(2, 3);
        session.GetStreamEventsAfter(lastSequenceId: 2, maxReplayCount: 10)
            .Select(evt => evt.SequenceId)
            .Should()
            .ContainSingle()
            .Which.Should().Be(3);
    }

    private static GatewaySession CreateSession()
        => new() { SessionId = $"session-{Guid.NewGuid():N}", AgentId = "agent-a" };
}

