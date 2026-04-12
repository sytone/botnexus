using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class GatewaySessionBehaviorSnapshotTests
{
    [Fact]
    public void AddEntry_UpdatesHistoryAndUpdatedAt()
    {
        var session = CreateSession();
        var initialUpdatedAt = session.UpdatedAt;

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        session.History.Should().ContainSingle(e => e.Content == "hello" && e.Role == MessageRole.User);
        session.UpdatedAt.Should().BeOnOrAfter(initialUpdatedAt);
        session.MessageCount.Should().Be(1);
    }

    [Fact]
    public void ReplaceHistory_ReplacesEntriesAndUpdatesTimestamp()
    {
        var session = CreateSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "old-1" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "old-2" });
        var updatedAtBeforeReplace = session.UpdatedAt;

        session.ReplaceHistory(
        [
            new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "new-tail" }
        ]);

        session.GetHistorySnapshot().Select(e => e.Content).Should().ContainInOrder("summary", "new-tail");
        session.UpdatedAt.Should().BeOnOrAfter(updatedAtBeforeReplace);
        session.MessageCount.Should().Be(2);
    }

    [Fact]
    public void StreamReplayState_PreservesExpectedOrderingAndSequence()
    {
        var session = CreateSession();

        session.SetStreamReplayState(10,
        [
            new GatewaySessionStreamEvent(5, """{"type":"delta","sequenceId":5}""", DateTimeOffset.UtcNow.AddSeconds(-2)),
            new GatewaySessionStreamEvent(3, """{"type":"delta","sequenceId":3}""", DateTimeOffset.UtcNow.AddSeconds(-3)),
            new GatewaySessionStreamEvent(4, """{"type":"delta","sequenceId":4}""", DateTimeOffset.UtcNow.AddSeconds(-1))
        ]);

        session.NextSequenceId.Should().Be(10);
        session.GetStreamEventSnapshot().Select(e => e.SequenceId).Should().ContainInOrder(3, 4, 5);
        session.GetStreamEventsAfter(lastSequenceId: 3, maxReplayCount: 10)
            .Select(e => e.SequenceId)
            .Should()
            .ContainInOrder(4, 5);
    }

    [Fact]
    public void StreamEventLog_ReturnsCopyOfReplaySnapshot()
    {
        var session = CreateSession();
        session.AddStreamEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 10);
        session.AddStreamEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 10);

        var snapshot = session.StreamEventLog;
        snapshot.RemoveAt(0);

        session.GetStreamEventSnapshot().Select(evt => evt.SequenceId).Should().ContainInOrder(1, 2);
    }

    [Fact]
    public void GatewaySession_Composition_UsesSharedDomainSessionState()
    {
        var domainSession = new Session
        {
            SessionId = SessionId.From("domain-session"),
            AgentId = AgentId.From("agent-snapshot")
        };
        var gatewaySession = GatewaySession.FromSession(domainSession);

        gatewaySession.Runtime.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "from-runtime" });
        gatewaySession.Session.History.Should().ContainSingle(entry => entry.Content == "from-runtime");
        gatewaySession.MessageCount.Should().Be(1);
    }

    private static GatewaySession CreateSession()
        => new()
        {
            SessionId = SessionId.From($"session-{Guid.NewGuid():N}"),
            AgentId = AgentId.From("agent-snapshot")
        };
}
