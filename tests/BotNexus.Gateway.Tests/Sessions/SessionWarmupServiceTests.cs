using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SessionWarmupServiceTests
{
    [Fact]
    public async Task WarmupService_LoadsSessions_OnStartup()
    {
        var now = DateTimeOffset.UtcNow;
        var store = CreateSessionStore(
            CreateSession("startup-1", "agent-a", SessionStatus.Active, now.AddMinutes(-2)));
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync(CancellationToken.None);

        store.Verify(value => value.ListAsync("agent-a", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        sessions.Should().ContainSingle(summary => summary.SessionId == "startup-1");
    }

    [Fact]
    public async Task WarmupService_FiltersToActiveAndRecent()
    {
        var now = DateTimeOffset.UtcNow;
        var activeRecent = CreateSession("active-recent", "agent-a", SessionStatus.Active, now.AddHours(-2));
        var expiredRecent = CreateSession("expired-recent", "agent-a", SessionStatus.Expired, now.AddHours(-1));
        var activeOld = CreateSession("active-old", "agent-a", SessionStatus.Active, now.AddDays(-2));

        var store = CreateSessionStore(activeRecent, expiredRecent, activeOld);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions
        {
            Enabled = true,
            RetentionWindowHours = 24
        });

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync(CancellationToken.None);
        var ids = sessions.Select(summary => summary.SessionId).ToList();

        ids.Should().Contain(["active-recent", "expired-recent"]);
        ids.Should().NotContain("active-old");
    }

    [Fact]
    public async Task WarmupService_CapsPerAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = Enumerable.Range(1, 6)
            .Select(index => CreateSession($"agent-a-{index}", "agent-a", SessionStatus.Active, now.AddMinutes(-index)))
            .Concat(Enumerable.Range(1, 6)
                .Select(index => CreateSession($"agent-b-{index}", "agent-b", SessionStatus.Active, now.AddMinutes(-index))))
            .ToArray();

        var store = CreateSessionStore(sessions);
        var service = CreateService(store.Object, CreateRegistry("agent-a", "agent-b"), new SessionWarmupOptions
        {
            Enabled = true,
            MaxSessionsPerAgent = 3,
            RetentionWindowHours = 24
        });

        await service.StartAsync(CancellationToken.None);
        var available = await service.GetAvailableSessionsAsync(CancellationToken.None);

        available.Count(summary => summary.AgentId == "agent-a").Should().BeLessThanOrEqualTo(3);
        available.Count(summary => summary.AgentId == "agent-b").Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task WarmupService_GetAvailableSessions_ReturnsAll()
    {
        var now = DateTimeOffset.UtcNow;
        var store = CreateSessionStore(
            CreateSession("session-a", "agent-a", SessionStatus.Active, now.AddMinutes(-1)),
            CreateSession("session-b", "agent-b", SessionStatus.Active, now.AddMinutes(-1)));
        var service = CreateService(store.Object, CreateRegistry("agent-a", "agent-b"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var available = await service.GetAvailableSessionsAsync(CancellationToken.None);

        available.Select(summary => summary.SessionId).Should().Contain(["session-a", "session-b"]);
    }

    [Fact]
    public async Task WarmupService_GetAvailableSessions_FiltersByAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var store = CreateSessionStore(
            CreateSession("agent-a-session", "agent-a", SessionStatus.Active, now.AddMinutes(-1)),
            CreateSession("agent-b-session", "agent-b", SessionStatus.Active, now.AddMinutes(-1)));
        var service = CreateService(store.Object, CreateRegistry("agent-a", "agent-b"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var available = await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None);

        available.Should().ContainSingle(summary => summary.SessionId == "agent-a-session");
        available.Should().OnlyContain(summary => summary.AgentId == "agent-a");
    }

    [Fact]
    public async Task WarmupService_WhenDisabled_ReturnsEmpty()
    {
        var store = CreateSessionStore(CreateSession("disabled-1", "agent-a", SessionStatus.Active, DateTimeOffset.UtcNow));
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions
        {
            Enabled = false
        });

        await service.StartAsync(CancellationToken.None);
        var available = await service.GetAvailableSessionsAsync(CancellationToken.None);

        available.Should().BeEmpty();
        store.Verify(value => value.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SessionWarmupService CreateService(
        ISessionStore sessionStore,
        IAgentRegistry registry,
        SessionWarmupOptions options)
        => new(
            sessionStore,
            registry,
            Options.Create(options),
            NullLogger<SessionWarmupService>.Instance);

    private static Mock<ISessionStore> CreateSessionStore(params GatewaySession[] sessions)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(value => value.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? agentId, CancellationToken _) =>
                sessions.Where(session => agentId is null || session.AgentId == agentId).ToList());
        return store;
    }

    private static IAgentRegistry CreateRegistry(params string[] agentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.GetAll())
            .Returns(agentIds.Select(agentId => new AgentDescriptor
            {
                AgentId = agentId,
                DisplayName = agentId,
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }).ToList());
        return registry.Object;
    }

    private static GatewaySession CreateSession(string sessionId, string agentId, BotNexus.Gateway.Abstractions.Models.SessionStatus status, DateTimeOffset updatedAt)
        => new()
        {
            SessionId = sessionId,
            AgentId = agentId,
            Status = status,
            UpdatedAt = updatedAt,
            ExpiresAt = status == BotNexus.Gateway.Abstractions.Models.SessionStatus.Expired ? updatedAt : null
        };
}
