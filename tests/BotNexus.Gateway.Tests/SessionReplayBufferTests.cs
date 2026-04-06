using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class SessionReplayBufferTests
{
    [Fact]
    public void AddStreamEvent_WithNonPositiveWindow_UsesDefaultReplayWindowSize()
    {
        var buffer = new SessionReplayBuffer();

        for (var sequenceId = 1; sequenceId <= SessionReplayBuffer.DefaultReplayWindowSize + 5; sequenceId++)
            buffer.AddStreamEvent(sequenceId, $$"""{"sequenceId":{{sequenceId}}}""", replayWindowSize: 0);

        var snapshot = buffer.GetStreamEventSnapshot();
        snapshot.Should().HaveCount(SessionReplayBuffer.DefaultReplayWindowSize);
        snapshot[0].SequenceId.Should().Be(6);
        snapshot[^1].SequenceId.Should().Be(SessionReplayBuffer.DefaultReplayWindowSize + 5);
    }

    [Fact]
    public void SetState_WithUnorderedEvents_SortsAndNormalizesNextSequenceId()
    {
        var buffer = new SessionReplayBuffer();
        var replay = new[]
        {
            new GatewaySessionStreamEvent(3, """{"type":"pong","sequenceId":3}""", DateTimeOffset.UtcNow),
            new GatewaySessionStreamEvent(1, """{"type":"connected","sequenceId":1}""", DateTimeOffset.UtcNow),
            new GatewaySessionStreamEvent(2, """{"type":"pong","sequenceId":2}""", DateTimeOffset.UtcNow)
        };

        buffer.SetState(nextSequenceId: 0, replay);

        buffer.NextSequenceId.Should().Be(1);
        buffer.GetStreamEventSnapshot().Select(evt => evt.SequenceId).Should().ContainInOrder(1, 2, 3);
    }
}
