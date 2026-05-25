using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests;

public sealed class InMemorySessionStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithUnknownSession_CreatesSession()
    {
        var store = new InMemorySessionStore();

        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        session.SessionId.Value.ShouldBe("s1");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithExistingSession_ReturnsExistingSession()
    {
        var store = new InMemorySessionStore();
        var created = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        var loaded = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-b"));

        loaded.ShouldBeSameAs(created);
    }

    [Fact]
    public async Task SaveAsync_PersistsSessionChanges()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-1";

        await store.SaveAsync(session);
        var loaded = await store.GetAsync(SessionId.From("s1"));

        loaded!.CallerId.ShouldBe("caller-1");
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        await store.DeleteAsync(SessionId.From("s1"));

        (await store.GetAsync(SessionId.From("s1"))).ShouldBeNull();
    }

    [Fact]
    public async Task ArchiveAsync_RemovesFromStore()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        await store.ArchiveAsync(SessionId.From("s1"));

        (await store.GetAsync(SessionId.From("s1"))).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_WithoutFilter_ReturnsAllSessions()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.GetOrCreateAsync(SessionId.From("s2"), AgentId.From("agent-b"));

        var sessions = await store.ListAsync();

        sessions.Count().ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_WithAgentFilter_ReturnsMatchingSessionsOnly()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.GetOrCreateAsync(SessionId.From("s2"), AgentId.From("agent-a"));
        await store.GetOrCreateAsync(SessionId.From("s3"), AgentId.From("agent-b"));

        var sessions = await store.ListAsync(AgentId.From("agent-a"));

        sessions.ShouldAllBe(s => s.AgentId == "agent-a");
    }

    [Fact]
    public async Task ListByChannelAsync_FiltersByAgentAndNormalizedChannel_OrderedByCreatedAtDesc()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-old"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-new"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-other-agent"),
            AgentId = AgentId.From("agent-b"),
            ChannelType = ChannelKey.From("web chat")
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-null-channel"),
            AgentId = AgentId.From("agent-a")
        });

        var sessions = await store.ListByChannelAsync(AgentId.From("agent-a"), ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "s-new", "s-old" }, ignoreOrder: false);
    }

    [Fact]
    public async Task GetAsync_WithUnknownSession_ReturnsNull()
    {
        var store = new InMemorySessionStore();

        var session = await store.GetAsync(SessionId.From("unknown"));

        session.ShouldBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_SubAgentPatternSessionId_InfersSubAgentSessionType()
    {
        var store = new InMemorySessionStore();
        var subSessionId = SessionId.ForSubAgent("parent-session", "child-1");

        var session = await store.GetOrCreateAsync(subSessionId, AgentId.From("agent-a"));

        session.SessionType.ShouldBe(SessionType.AgentSubAgent);
        session.SessionId.Value.ShouldContain("::subagent::");
    }

    [Fact]
    public async Task GetExistenceAsync_ReturnsOwnedAndParticipantSessions_WithFiltersApplied()
    {
        var store = new InMemorySessionStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("owned"),
            AgentId = AgentId.From("agent-a"),
            SessionType = SessionType.UserAgent,
            CreatedAt = now.AddDays(-2)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("participant"),
            AgentId = AgentId.From("agent-b"),
            SessionType = SessionType.Cron,
            Participants =
            [
                new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-a")) }
            ],
            CreatedAt = now.AddDays(-1)
        });

        var sessions = await store.GetExistenceAsync(
            AgentId.From("agent-a"),
            new ExistenceQuery
            {
                TypeFilter = SessionType.Cron,
                From = now.AddDays(-1.5),
                Limit = 10
            });

        sessions.Select(session => session.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    // --- ListByConversationAsync: F-7 contract pins ---
    //
    // The 5 invariants (rubber-duck review):
    //   1. Returns ALL sessions (Active + Sealed) for the conversation — not just active.
    //   2. Excludes sessions for other conversations AND sessions with null ConversationId.
    //   3. Stable chronological order — CreatedAt ascending with SessionId as tie-breaker.
    //   4. Returns empty list (NEVER null) for unknown conversation id.
    //   5. Optional AgentId filter narrows by owner.
    //
    // These pins protect against the regression that issue F-7 documents: callers
    // doing `ListAsync(...).Where(s => s.ConversationId == ...)` silently miss
    // sessions on FileSessionStore-backed deployments (pre-this-PR) and load the
    // entire session table on SQLite-backed ones.

    private static async Task SeedConversationFixtureAsync(InMemorySessionStore store, DateTimeOffset baseTime)
    {
        var convA = ConversationId.From("conv-a");
        var convB = ConversationId.From("conv-b");

        // Active session in convA (newest)
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-active"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(10),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        // Sealed session in convA (older)
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-sealed"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime,
            Status = SessionStatus.Sealed,
            Session = { ConversationId = convA }
        });
        // Session in convA owned by a DIFFERENT agent (for agent filter test)
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-other-agent"),
            AgentId = AgentId.From("agent-y"),
            CreatedAt = baseTime.AddMinutes(5),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        // Session in DIFFERENT conversation (must not leak)
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-b"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(20),
            Status = SessionStatus.Active,
            Session = { ConversationId = convB }
        });
        // ORPHAN session with NULL ConversationId (must not leak into any query)
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-orphan"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(15),
            Status = SessionStatus.Active
        });
    }

    [Fact]
    public async Task ListByConversationAsync_ReturnsActiveAndSealedSessions_ForConversation()
    {
        // Invariant 1: includes both Active and Sealed (history needs full timeline).
        var store = new InMemorySessionStore();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(store, baseTime);

        var sessions = await store.ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-other-agent", "s-a-active" }, ignoreOrder: false);
        // Belt-and-braces: both statuses are present.
        sessions.Select(s => s.Status).ShouldContain(SessionStatus.Sealed);
        sessions.Select(s => s.Status).ShouldContain(SessionStatus.Active);
    }

    [Fact]
    public async Task ListByConversationAsync_ExcludesOtherConversations_AndOrphanSessions()
    {
        // Invariant 2: must not leak sessions belonging to other conversations,
        // and must not leak orphan sessions (ConversationId == null).
        var store = new InMemorySessionStore();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(store, baseTime);

        var sessions = await store.ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-b");
        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-orphan");
    }

    [Fact]
    public async Task ListByConversationAsync_ReturnsCreatedAtAscending_WithSessionIdTieBreaker()
    {
        // Invariant 3: chronological order with deterministic tie-break.
        var store = new InMemorySessionStore();
        var same = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var convId = ConversationId.From("conv-tie");

        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-z"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same,
            Session = { ConversationId = convId }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same,
            Session = { ConversationId = convId }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-later"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = same.AddMinutes(1),
            Session = { ConversationId = convId }
        });

        var sessions = await store.ListByConversationAsync(convId);

        // Same CreatedAt: ordinal SessionId ascending. Then later session at the end.
        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a", "s-z", "s-later" }, ignoreOrder: false);
    }

    [Fact]
    public async Task ListByConversationAsync_ReturnsEmptyList_ForUnknownConversation()
    {
        // Invariant 4: empty list, never null, never throws.
        var store = new InMemorySessionStore();

        var sessions = await store.ListByConversationAsync(ConversationId.From("does-not-exist"));

        sessions.ShouldNotBeNull();
        sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListByConversationAsync_WithAgentFilter_NarrowsToOwner()
    {
        // Invariant 5: optional AgentId filter.
        var store = new InMemorySessionStore();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(store, baseTime);

        var sessions = await store.ListByConversationAsync(
            ConversationId.From("conv-a"),
            agentId: AgentId.From("agent-x"));

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-active" }, ignoreOrder: false);
        sessions.ShouldAllBe(s => s.AgentId == AgentId.From("agent-x"));
    }
}



