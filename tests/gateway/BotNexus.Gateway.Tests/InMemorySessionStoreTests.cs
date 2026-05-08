using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
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
                new SessionParticipant { Type = ParticipantType.Agent, Id = "agent-a" }
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
}



