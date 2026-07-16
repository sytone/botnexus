using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class AutoReplayInterruptedTurnsTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        bool withSentinel = false,
        string? callerId = null,
        ChannelKey? channelType = null,
        SessionType? sessionType = null,
        string? lastUserContent = null,
        int existingReplayCount = 0)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            Status = SessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionType = sessionType ?? SessionType.UserAgent,
            ChannelType = channelType,
            CallerId = callerId
        };

        if (lastUserContent is not null)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = lastUserContent,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        }

        if (withSentinel)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress — gateway restarted if visible]",
                IsCrashSentinel = true
            });
        }

        if (existingReplayCount > 0)
            session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount] = existingReplayCount;

        return session;
    }

    private static IAgentRegistry CreateRegistry(params string[] agentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll())
            .Returns(agentIds.Select(id => new AgentDescriptor
            {
                AgentId = AgentId.From(id),
                DisplayName = id,
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            }).ToList());
        return registry.Object;
    }

    private static Mock<ISessionStore> CreateStore(params GatewaySession[] sessions)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentId? agentId, CancellationToken _) =>
                sessions.Where(s => !agentId.HasValue || s.AgentId == agentId.Value).ToList());
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static Mock<IInboundMessageOrchestrator> CreateOrchestrator(bool postAccepted = true)
    {
        var orch = new Mock<IInboundMessageOrchestrator>();
        orch.Setup(o => o.Post(It.IsAny<InboundMessage>())).Returns(postAccepted);
        return orch;
    }

    private static InterruptedTurnNotificationService CreateService(
        ISessionStore store,
        IAgentRegistry registry,
        GatewayOptions? options = null,
        IInboundMessageOrchestrator? orchestrator = null,
        IActivityBroadcaster? broadcaster = null,
        IChannelManager? channelManager = null)
    {
        broadcaster ??= Mock.Of<IActivityBroadcaster>();
        channelManager ??= Mock.Of<IChannelManager>();
        return new InterruptedTurnNotificationService(
            store,
            registry,
            broadcaster,
            channelManager,
            NullLogger<InterruptedTurnNotificationService>.Instance,
            orchestrator,
            options is not null ? Options.Create(options) : null);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoReplay_WhenEnabled_PostsLastUserMessage()
    {
        var session = CreateSession("sess-1", "agent-a", withSentinel: true,
            channelType: ChannelKey.From("signalr"), lastUserContent: "hello agent");
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = true, MaxAutoReplayAttempts = 2 };

        var service = CreateService(store.Object, CreateRegistry("agent-a"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        orchestrator.Verify(o => o.Post(It.Is<InboundMessage>(m =>
            m.Content == "hello agent" &&
            (bool)m.Metadata["isReplay"]! == true)), Times.Once);
    }

    [Fact]
    public async Task AutoReplay_WhenDisabled_DoesNotCallPost()
    {
        var session = CreateSession("sess-2", "agent-b", withSentinel: true,
            channelType: ChannelKey.From("signalr"), lastUserContent: "hello");
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = false };

        var service = CreateService(store.Object, CreateRegistry("agent-b"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
    }

    [Fact]
    public async Task AutoReplay_WhenMaxAttemptsReached_FallsBackToNotificationOnly()
    {
        var session = CreateSession("sess-3", "agent-c", withSentinel: true,
            channelType: ChannelKey.From("signalr"), lastUserContent: "retry this",
            existingReplayCount: 2);
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = true, MaxAutoReplayAttempts = 2 };

        var service = CreateService(store.Object, CreateRegistry("agent-c"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        // Should NOT replay (at max)
        orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);

        // But SHOULD still append a notification entry
        session.History.ShouldContain(e => e.Role == MessageRole.Notification);
    }

    [Fact]
    public async Task AutoReplay_CronSession_IsSkipped()
    {
        // Cron sessions have ChannelType="cron" — IsInteractive returns false
        var session = CreateSession("sess-4", "agent-d", withSentinel: true,
            channelType: ChannelKey.From("cron"), lastUserContent: "cron message");
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = true, MaxAutoReplayAttempts = 2 };

        var service = CreateService(store.Object, CreateRegistry("agent-d"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        // Cron session is not interactive — replay must not fire
        orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
    }

    [Fact]
    public async Task AutoReplay_IncrementsReplayCountInMetadata()
    {
        var session = CreateSession("sess-5", "agent-e", withSentinel: true,
            channelType: ChannelKey.From("signalr"), lastUserContent: "count this");
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = true, MaxAutoReplayAttempts = 3 };

        var service = CreateService(store.Object, CreateRegistry("agent-e"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount].ShouldBe(1);
    }

    [Fact]
    public async Task AutoReplay_WhenNoUserMessageExists_DoesNotPost()
    {
        // Session with sentinel but no prior user message
        var session = CreateSession("sess-6", "agent-f", withSentinel: true,
            channelType: ChannelKey.From("signalr"), lastUserContent: null);
        var store = CreateStore(session);
        var orchestrator = CreateOrchestrator();
        var options = new GatewayOptions { AutoReplayInterruptedTurns = true, MaxAutoReplayAttempts = 2 };

        var service = CreateService(store.Object, CreateRegistry("agent-f"), options, orchestrator.Object);
        await service.StartedAsync(CancellationToken.None);

        orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
    }
}
