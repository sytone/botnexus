using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class InMemorySessionStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithUnknownSession_CreatesSession()
    {
        var store = new InMemorySessionStore();

        var session = await store.GetOrCreateAsync("s1", "agent-a");

        session.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithExistingSession_ReturnsExistingSession()
    {
        var store = new InMemorySessionStore();
        var created = await store.GetOrCreateAsync("s1", "agent-a");

        var loaded = await store.GetOrCreateAsync("s1", "agent-b");

        loaded.Should().BeSameAs(created);
    }

    [Fact]
    public async Task SaveAsync_PersistsSessionChanges()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-1";

        await store.SaveAsync(session);
        var loaded = await store.GetAsync("s1");

        loaded!.CallerId.Should().Be("caller-1");
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");

        await store.DeleteAsync("s1");

        (await store.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task ArchiveAsync_RemovesFromStore()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");

        await store.ArchiveAsync("s1");

        (await store.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_WithoutFilter_ReturnsAllSessions()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        await store.GetOrCreateAsync("s2", "agent-b");

        var sessions = await store.ListAsync();

        sessions.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_WithAgentFilter_ReturnsMatchingSessionsOnly()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        await store.GetOrCreateAsync("s2", "agent-a");
        await store.GetOrCreateAsync("s3", "agent-b");

        var sessions = await store.ListAsync("agent-a");

        sessions.Should().OnlyContain(s => s.AgentId == "agent-a");
    }

    [Fact]
    public async Task ListByChannelAsync_FiltersByAgentAndNormalizedChannel_OrderedByCreatedAtDesc()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-old",
            AgentId = "agent-a",
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-new",
            AgentId = "agent-a",
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-other-agent",
            AgentId = "agent-b",
            ChannelType = ChannelKey.From("web chat")
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-null-channel",
            AgentId = "agent-a"
        });

        var sessions = await store.ListByChannelAsync("agent-a", ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId).Should().Equal("s-new", "s-old");
    }

    [Fact]
    public async Task GetAsync_WithUnknownSession_ReturnsNull()
    {
        var store = new InMemorySessionStore();

        var session = await store.GetAsync("unknown");

        session.Should().BeNull();
    }
}



