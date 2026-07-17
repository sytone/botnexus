using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class InterruptedTurnNotificationServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        bool withSentinel = false,
        string? callerId = null,
        ChannelKey? channelType = null)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            Status = SessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionType = SessionType.UserAgent,
            ChannelType = channelType,
            CallerId = callerId
        };

        if (withSentinel)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress — gateway restarted if visible]",
                IsCrashSentinel = true
            });
        }

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

    private static InterruptedTurnNotificationService CreateService(
        ISessionStore store,
        IAgentRegistry registry,
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
            NullLogger<InterruptedTurnNotificationService>.Instance);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_SessionWithSentinel_AddsNotificationEntryAndRemovesSentinels()
    {
        var session = CreateSession("sess-1", "agent-a", withSentinel: true);
        var store = CreateStore(session);
        var service = CreateService(store.Object, CreateRegistry("agent-a"));

        await service.StartedAsync(CancellationToken.None);

        // Sentinel should be gone
        session.History.ShouldNotContain(e => e.IsCrashSentinel);

        // A notification entry should have been appended
        session.History.ShouldContain(e => e.Role == MessageRole.Notification);
        session.History.ShouldContain(e =>
            e.Role == MessageRole.Notification &&
            e.Content.Contains("gateway was restarted"));

        // Session should have been persisted
        store.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_SessionWithoutSentinel_IsNotTouched()
    {
        var session = CreateSession("sess-clean", "agent-a", withSentinel: false);
        var store = CreateStore(session);
        var service = CreateService(store.Object, CreateRegistry("agent-a"));

        await service.StartedAsync(CancellationToken.None);

        session.History.ShouldNotContain(e => e.Role == MessageRole.Notification);
        store.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_BroadcastsActivity_ForInterruptedSession()
    {
        var session = CreateSession("sess-2", "agent-b", withSentinel: true);
        var store = CreateStore(session);
        var broadcaster = new Mock<IActivityBroadcaster>();
        broadcaster.Setup(b => b.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var service = CreateService(store.Object, CreateRegistry("agent-b"), broadcaster: broadcaster.Object);

        await service.StartedAsync(CancellationToken.None);

        broadcaster.Verify(
            b => b.PublishAsync(
                It.Is<GatewayActivity>(a => a.AgentId == "agent-b" && a.SessionId == "sess-2"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_MultipleAgents_OnlyInterruptedSessionsNotified()
    {
        var interrupted = CreateSession("sess-int", "agent-x", withSentinel: true);
        var clean = CreateSession("sess-clean", "agent-x", withSentinel: false);
        var store = CreateStore(interrupted, clean);
        var service = CreateService(store.Object, CreateRegistry("agent-x"));

        await service.StartedAsync(CancellationToken.None);

        store.Verify(s => s.SaveAsync(interrupted, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.SaveAsync(clean, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var store = CreateStore();
        var service = CreateService(store.Object, CreateRegistry());
        var ex = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));
        ex.ShouldBeNull();
    }
}
