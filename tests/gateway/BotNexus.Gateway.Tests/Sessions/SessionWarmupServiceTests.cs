using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
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

        store.Verify(value => value.ListAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        sessions.Where(summary => summary.SessionId == "startup-1").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WarmupService_FiltersToActiveAndRecent()
    {
        var now = DateTimeOffset.UtcNow;
        var activeRecent = CreateSession("active-recent", "agent-a", SessionStatus.Active, now.AddHours(-2));
        var sealedRecent = CreateSession("sealed-recent", "agent-a", SessionStatus.Sealed, now.AddHours(-1));
        var activeOld = CreateSession("active-old", "agent-a", SessionStatus.Active, now.AddDays(-2));

        var store = CreateSessionStore(activeRecent, sealedRecent, activeOld);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions
        {
            Enabled = true,
            RetentionWindowHours = 24
        });

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync(CancellationToken.None);
        var ids = sessions.Select(summary => summary.SessionId).ToList();

        ids.ShouldContain("active-recent");
        ids.ShouldContain("sealed-recent");
        ids.ShouldNotContain("active-old");
    }

    [Fact]
    public async Task WarmupService_OnlyReturnsUserAgentSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var userAgent = CreateSession("user-agent", "agent-a", SessionStatus.Active, now, BotNexus.Domain.Primitives.SessionType.UserAgent);
        var soul = CreateSession("soul", "agent-a", SessionStatus.Active, now.AddMinutes(-1), BotNexus.Domain.Primitives.SessionType.Soul);
        var cron = CreateSession("cron", "agent-a", SessionStatus.Active, now.AddMinutes(-2), BotNexus.Domain.Primitives.SessionType.Cron);
        var subAgent = CreateSession("sub-agent", "agent-a", SessionStatus.Active, now.AddMinutes(-3), BotNexus.Domain.Primitives.SessionType.AgentSubAgent);
        var agentSelf = CreateSession("agent-self", "agent-a", SessionStatus.Active, now.AddMinutes(-4), BotNexus.Domain.Primitives.SessionType.AgentSelf);
        var agentAgent = CreateSession("agent-agent", "agent-a", SessionStatus.Active, now.AddMinutes(-5), BotNexus.Domain.Primitives.SessionType.AgentAgent);

        var store = CreateSessionStore(userAgent, soul, cron, subAgent, agentSelf, agentAgent);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None);

        sessions.Select(summary => summary.SessionId).ShouldHaveSingleItem().ShouldBe("user-agent");
    }

    [Fact]
    public async Task WarmupService_HidesSoulCronAndSubAgentSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var userAgent = CreateSession("user-agent", "agent-a", SessionStatus.Active, now, BotNexus.Domain.Primitives.SessionType.UserAgent);
        var soul = CreateSession("soul", "agent-a", SessionStatus.Active, now.AddMinutes(-1), BotNexus.Domain.Primitives.SessionType.Soul);
        var cron = CreateSession("cron", "agent-a", SessionStatus.Active, now.AddMinutes(-2), BotNexus.Domain.Primitives.SessionType.Cron);
        var subAgent = CreateSession("sub-agent", "agent-a", SessionStatus.Active, now.AddMinutes(-3), BotNexus.Domain.Primitives.SessionType.AgentSubAgent);

        var store = CreateSessionStore(userAgent, soul, cron, subAgent);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var visibleIds = (await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None))
            .Select(summary => summary.SessionId)
            .ToList();

        visibleIds.ShouldContain("user-agent");
        visibleIds.ShouldNotContain("soul");
        visibleIds.ShouldNotContain("cron");
        visibleIds.ShouldNotContain("sub-agent");
    }

    [Fact]
    public async Task WarmupService_HidesCronChannelSessionsEvenWhenSessionTypeIsUserAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var userAgent = CreateSession("user-agent", "agent-a", SessionStatus.Active, now, channelType: BotNexus.Domain.Primitives.ChannelKey.From("signalr"));
        var leakedCron = CreateSession("cron-leak", "agent-a", SessionStatus.Active, now.AddMinutes(-1), BotNexus.Domain.Primitives.SessionType.UserAgent, BotNexus.Domain.Primitives.ChannelKey.From("cron"));

        var store = CreateSessionStore(userAgent, leakedCron);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var visibleIds = (await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None))
            .Select(summary => summary.SessionId)
            .ToList();

        visibleIds.ShouldContain("user-agent");
        visibleIds.ShouldNotContain("cron-leak");
    }

    [Fact]
    public async Task WarmupService_HidesSealedChannelSessionWhenNewerActiveSiblingExists()
    {
        var now = DateTimeOffset.UtcNow;
        var oldSealed = CreateSession("sealed-old", "agent-a", SessionStatus.Sealed, now.AddMinutes(-10), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));
        var newActive = CreateSession("active-new", "agent-a", SessionStatus.Active, now.AddMinutes(-1), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));

        var store = CreateSessionStore(oldSealed, newActive);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None);

        sessions.Select(summary => summary.SessionId).ShouldHaveSingleItem().ShouldBe("active-new");
    }

    [Fact]
    public async Task WarmupService_ShowsMostRecentSealedWhenNoActiveSessionForChannel()
    {
        var now = DateTimeOffset.UtcNow;
        var sealedOld = CreateSession("sealed-old", "agent-a", SessionStatus.Sealed, now.AddMinutes(-15), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));
        var sealedNewest = CreateSession("sealed-newest", "agent-a", SessionStatus.Sealed, now.AddMinutes(-5), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));

        var store = CreateSessionStore(sealedOld, sealedNewest);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions());

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None);

        sessions.Select(summary => summary.SessionId).ShouldHaveSingleItem().ShouldBe("sealed-newest");
    }

    [Fact]
    public async Task WarmupService_CapsPerAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = Enumerable.Range(1, 6)
            .Select(index => CreateSession($"agent-a-{index}", "agent-a", SessionStatus.Active, now.AddMinutes(-index), channelType: BotNexus.Domain.Primitives.ChannelKey.From($"chan-a-{index}")))
            .Concat(Enumerable.Range(1, 6)
                .Select(index => CreateSession($"agent-b-{index}", "agent-b", SessionStatus.Active, now.AddMinutes(-index), channelType: BotNexus.Domain.Primitives.ChannelKey.From($"chan-b-{index}"))))
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

        available.Count(summary => summary.AgentId == "agent-a").ShouldBeLessThanOrEqualTo(3);
        available.Count(summary => summary.AgentId == "agent-b").ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task WarmupService_WhenConfigured_ReturnsAllVisibleSessionsWithoutChannelCollapse()
    {
        var now = DateTimeOffset.UtcNow;
        var older = CreateSession("telegram-older", "agent-a", SessionStatus.Active, now.AddMinutes(-5), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));
        var newest = CreateSession("telegram-newer", "agent-a", SessionStatus.Active, now.AddMinutes(-1), channelType: BotNexus.Domain.Primitives.ChannelKey.From("telegram"));

        var store = CreateSessionStore(older, newest);
        var service = CreateService(store.Object, CreateRegistry("agent-a"), new SessionWarmupOptions
        {
            CollapseChannelContinuations = false
        });

        await service.StartAsync(CancellationToken.None);
        var sessions = await service.GetAvailableSessionsAsync("agent-a", CancellationToken.None);

        sessions.Select(summary => summary.SessionId).ShouldContain("telegram-older");
        sessions.Select(summary => summary.SessionId).ShouldContain("telegram-newer");
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

        available.Select(summary => summary.SessionId).ShouldContain("session-a");
        available.Select(summary => summary.SessionId).ShouldContain("session-b");
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

        available.Where(summary => summary.SessionId == "agent-a-session").ShouldHaveSingleItem();
        available.ShouldAllBe(summary => summary.AgentId == "agent-a");
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

        available.ShouldBeEmpty();
        store.Verify(value => value.ListAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        store.Setup(value => value.ListAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotNexus.Domain.Primitives.AgentId? agentId, CancellationToken _) =>
                sessions.Where(session => !agentId.HasValue || session.AgentId == agentId.Value).ToList());
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

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        BotNexus.Gateway.Abstractions.Models.SessionStatus status,
        DateTimeOffset updatedAt,
        BotNexus.Domain.Primitives.SessionType? sessionType = null,
        BotNexus.Domain.Primitives.ChannelKey? channelType = null)
        => new()
        {
            SessionId = sessionId,
            AgentId = agentId,
            Status = status,
            UpdatedAt = updatedAt,
            SessionType = sessionType ?? BotNexus.Domain.Primitives.SessionType.UserAgent,
            ChannelType = channelType
        };
}
