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
    public async Task AllocateSequenceId_StampsSessionUpdatedAt()
    {
        // Parity pin (#575 — rubber-duck HIGH): pre-extract, the runtime forwarding
        // method `AllocateSequenceId` stamped `Session.UpdatedAt = DateTimeOffset.UtcNow`
        // after delegating to the buffer. After extracting to `SessionStreamReplay`,
        // the new facade must preserve this behaviour exactly — anything less would
        // silently change the UpdatedAt cadence on the soul/heartbeat replay path.
        var session = CreateSession();
        var initialUpdatedAt = session.UpdatedAt;

        // Use a real sleep rather than re-reading the wall clock because UpdatedAt is
        // stamped via DateTimeOffset.UtcNow inside SessionStreamReplay — without a real
        // gap the assertion is timing-sensitive on fast CI machines.
        await Task.Delay(5);

        var allocated = session.StreamReplay.AllocateSequenceId();

        allocated.ShouldBe(1);
        session.UpdatedAt.ShouldBeGreaterThan(initialUpdatedAt,
            "F-9 / Phase 7 (#575) parity pin: AllocateSequenceId must stamp " +
            "Session.UpdatedAt. If this fails, the extract dropped the UpdatedAt-stamping " +
            "side-effect from the runtime's forwarding method and the soul-write " +
            "freshness gate would observe stale timestamps on heartbeat sequencing.");
    }

    [Fact]
    public async Task AddEvent_StampsSessionUpdatedAt()
    {
        // Parity pin (#575 — rubber-duck HIGH): pre-extract, the runtime forwarding
        // method `AddStreamEvent` stamped `Session.UpdatedAt = DateTimeOffset.UtcNow`
        // after delegating to the buffer. The new facade must preserve this exactly —
        // otherwise streaming-only activity (no AddEntry, no AllocateSequenceId) would
        // not bump the timestamp and idle-session detection would seal active streams.
        var session = CreateSession();
        var initialUpdatedAt = session.UpdatedAt;

        await Task.Delay(5);

        session.StreamReplay.AddEvent(1, """{"type":"delta","sequenceId":1}""", replayWindowSize: 10);

        session.UpdatedAt.ShouldBeGreaterThan(initialUpdatedAt,
            "F-9 / Phase 7 (#575) parity pin: AddEvent must stamp Session.UpdatedAt. " +
            "If this fails, the extract dropped the UpdatedAt-stamping side-effect; " +
            "streaming-only sessions would never refresh their freshness gate and the " +
            "session-cleanup service would seal active streams as idle.");
    }

    [Fact]
    public void GatewaySession_Composition_UsesSharedDomainSessionState()
    {
        var domainSession = new Session
        {
            SessionId = SessionId.From("domain-session")
        };
        var gatewaySession = GatewaySession.FromSession(domainSession);
        gatewaySession.HydrateAgentId(AgentId.From("agent-snapshot"));

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
    public void ConversationId_DefaultsToUninitialized_OnNewSession()
    {
        // Phase 9 / P9-B-2 (#627): Session.ConversationId is non-nullable. A brand-new
        // session has an UNINITIALIZED ConversationId (the unset sentinel) until either
        // the legacy resolver fires on save or the conversation router stamps it.
        // IsInitialized() is the typed predicate; we never compare to `default(ConversationId)`
        // literally (banned by Vogen analyzer VOG009).
        var session = CreateSession();

        session.ConversationId.IsInitialized().ShouldBeFalse(
            "F-9 / Phase 9: a freshly constructed GatewaySession must expose an " +
            "uninitialized ConversationId so the storage layer can detect it and " +
            "backfill the legacy conversation. If this fails, the unset sentinel " +
            "has been lost.");
        session.Session.ConversationId.IsInitialized().ShouldBeFalse();
    }

    [Fact]
    public void ConversationId_AssignedValue_IsInitialized()
    {
        // Phase 9 / P9-B-2 (#627): when a ConversationId has been stamped on the session,
        // IsInitialized() returns true and Value.Value is the assigned string.
        var session = CreateSession();
        var convId = ConversationId.From("conv-pinned");

        session.ConversationId = convId;

        session.ConversationId.IsInitialized().ShouldBeTrue();
        session.ConversationId.ShouldBe(convId);
        session.Session.ConversationId.ShouldBe(convId);
    }

    private static GatewaySession CreateSession()
        => new()
        {
            SessionId = SessionId.From($"session-{Guid.NewGuid():N}"),
            AgentId = AgentId.From("agent-snapshot")
        };
}
