using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests;

public sealed class GatewaySessionBehaviorSnapshotTests
{
    [Fact]
    public void AddEntry_UpdatesHistoryAndUpdatedAt()
    {
        var session = CreateSession();
        var initialUpdatedAt = session.UpdatedAt;

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        session.History.Where(e => e.Content == "hello" && e.Role == MessageRole.User).ShouldHaveSingleItem();
        session.UpdatedAt.ShouldBeGreaterThanOrEqualTo(initialUpdatedAt);
        session.MessageCount.ShouldBe(1);
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

        session.GetHistorySnapshot().Select(e => e.Content).ToList().ShouldBe(new[] { "summary", "new-tail" });
        session.UpdatedAt.ShouldBeGreaterThanOrEqualTo(updatedAtBeforeReplace);
        session.MessageCount.ShouldBe(2);
    }

    [Fact]
    public void StreamReplayState_PreservesExpectedOrderingAndSequence()
    {
        var session = CreateSession();

        session.StreamReplay.SetState(10,
        [
            new GatewaySessionStreamEvent(5, """{"type":"delta","sequenceId":5}""", DateTimeOffset.UtcNow.AddSeconds(-2)),
            new GatewaySessionStreamEvent(3, """{"type":"delta","sequenceId":3}""", DateTimeOffset.UtcNow.AddSeconds(-3)),
            new GatewaySessionStreamEvent(4, """{"type":"delta","sequenceId":4}""", DateTimeOffset.UtcNow.AddSeconds(-1))
        ]);

        session.StreamReplay.NextSequenceId.ShouldBe(10);
        session.StreamReplay.GetEventSnapshot().Select(e => e.SequenceId).ToList().ShouldBe(new long[] { 3, 4, 5 });
        session.StreamReplay.GetEventsAfter(lastSequenceId: 3, maxReplayCount: 10)
            .Select(e => e.SequenceId)
            .ShouldBe(new long[] { 4, 5 }, ignoreOrder: false);
    }

    [Fact]
    public void GetEventSnapshot_ReturnsDefensiveCopy_NotAliasOfInternalState()
    {
        var session = CreateSession();
        session.StreamReplay.AddEvent(1, """{"type":"connected","sequenceId":1}""", replayWindowSize: 10);
        session.StreamReplay.AddEvent(2, """{"type":"pong","sequenceId":2}""", replayWindowSize: 10);

        var snapshotBeforeAdd = session.StreamReplay.GetEventSnapshot();
        session.StreamReplay.AddEvent(3, """{"type":"pong","sequenceId":3}""", replayWindowSize: 10);

        // The previously-returned snapshot is a defensive copy and does NOT
        // observe the post-snapshot addition; a freshly fetched snapshot does.
        snapshotBeforeAdd.Select(evt => evt.SequenceId).ToList().ShouldBe(new long[] { 1, 2 });
        session.StreamReplay.GetEventSnapshot().Select(evt => evt.SequenceId).ToList().ShouldBe(new long[] { 1, 2, 3 });
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
        gatewaySession.Session.History.Where(entry => entry.Content == "from-runtime").ShouldHaveSingleItem();
        gatewaySession.MessageCount.ShouldBe(1);
    }

    [Fact]
    public void ConversationId_RoundTripsThroughProxy_ReadingInnerRecord()
    {
        var session = CreateSession();
        var conversationId = ConversationId.From("conv-abc");

        session.ConversationId = conversationId;

        session.ConversationId.ShouldBe(conversationId);
        session.Session.ConversationId.ShouldBe(conversationId,
            "F-9 / Phase 7: the ConversationId proxy setter must mutate the inner " +
            "Session record so persistence sees the value. If this fails, the proxy " +
            "is storing the value in a private field instead of delegating.");
    }

    [Fact]
    public void ConversationId_ProxyGetter_ReflectsInnerRecordChanges()
    {
        var session = CreateSession();
        session.Session.ConversationId = ConversationId.From("conv-direct");

        session.ConversationId.ShouldBe(ConversationId.From("conv-direct"),
            "F-9 / Phase 7: the ConversationId proxy getter must read directly from " +
            "the inner Session record. If this fails, the proxy is caching a stale " +
            "value and reads after persistence-layer writes would be wrong.");
    }

    [Fact]
    public void ConversationId_ProxyAcceptsNull_ForOrphanSessions()
    {
        var session = CreateSession();
        session.ConversationId = ConversationId.From("conv-set");
        session.ConversationId = null;

        session.ConversationId.ShouldBeNull(
            "F-9 / Phase 7: the ConversationId proxy must accept null assignment for " +
            "orphan / legacy ungrouped sessions. If this fails, the proxy lost null-" +
            "tolerance and callers cannot clear the binding.");
        session.Session.ConversationId.ShouldBeNull();
    }

    private static GatewaySession CreateSession()
        => new()
        {
            SessionId = SessionId.From($"session-{Guid.NewGuid():N}"),
            AgentId = AgentId.From("agent-snapshot")
        };
}
