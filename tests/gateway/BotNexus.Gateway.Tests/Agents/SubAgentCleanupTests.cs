using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for DefaultSubAgentManager.CleanupChildSessionsAsync -- prevents orphaned sub-agent
/// sessions when a parent session is reset.
/// </summary>
public sealed class SubAgentCleanupTests
{
    [Fact]
    public async Task CleanupChildSessionsAsync_ReturnsZero_WhenNoChildrenExist()
    {
        var manager = CreateManager();

        var count = await manager.CleanupChildSessionsAsync(SessionId.From("orphan-session"));

        count.ShouldBe(0);
    }

    [Fact]
    public async Task CleanupChildSessionsAsync_ArchivesChildSession_WhenChildExists()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var childHandle = CreateInstantHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var parentSessionId = SessionId.From("parent-session");
        await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionId,
            Task = "do something",
            TimeoutSeconds = 600
        });

        // Wait briefly for the background task to start
        await Task.Delay(50);

        var count = await manager.CleanupChildSessionsAsync(parentSessionId);

        count.ShouldBe(1);
        sessionStore.Verify(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CleanupChildSessionsAsync_IsNonFatal_WhenSessionArchiveFails()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("session store unavailable"));

        var childHandle = CreateInstantHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var parentSessionId = SessionId.From("parent-session");
        await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionId,
            Task = "do something",
            TimeoutSeconds = 600
        });

        await Task.Delay(50);

        // Should not throw even if archive fails
        var count = await manager.CleanupChildSessionsAsync(parentSessionId);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task CleanupChildSessionsAsync_DoesNotCleanOtherParentSessions()
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var childHandle = CreateInstantHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var parentSessionA = SessionId.From("parent-session-a");
        var parentSessionB = SessionId.From("parent-session-b");

        await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionA,
            Task = "task A",
            TimeoutSeconds = 600
        });
        await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = parentSessionB,
            Task = "task B",
            TimeoutSeconds = 600
        });

        await Task.Delay(50);

        // Only clean up session A
        var count = await manager.CleanupChildSessionsAsync(parentSessionA);

        count.ShouldBe(1);

        // Session B's child should still be trackable
        var remaining = await manager.ListAsync(parentSessionB);
        remaining.ShouldNotBeEmpty();
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor? supervisor = null,
        ISessionStore? sessionStore = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot"
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()));
        registry.Setup(r => r.Unregister(It.IsAny<AgentId>()));

        var usedSupervisor = supervisor ?? Mock.Of<IAgentSupervisor>();
        var activity = Mock.Of<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>();
        var dispatcher = Mock.Of<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>();

        return new DefaultSubAgentManager(
            usedSupervisor,
            registry.Object,
            activity,
            dispatcher,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: sessionStore);
    }

    private static Mock<IAgentHandle> CreateInstantHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        return handle;
    }
}
