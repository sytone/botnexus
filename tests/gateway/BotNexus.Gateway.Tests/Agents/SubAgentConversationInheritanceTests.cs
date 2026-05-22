using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for sub-agent conversation ID inheritance (issue #468).
/// When a spawn request includes a ConversationId, the child session should be stamped
/// so its output routes into the parent conversation instead of creating a new one.
/// </summary>
public sealed class SubAgentConversationInheritanceTests
{
    [Fact]
    public async Task SpawnAsync_WithConversationId_StampsChildSession()
    {
        // Arrange
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var conversationId = ConversationId.From("parent-conv-123");
        GatewaySession? savedSession = null;
        var childSession = new GatewaySession
        {
            SessionId = SessionId.From("child-session"),
            AgentId = AgentId.From("parent-agent")
        };

        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childSession);
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            ConversationId = conversationId
        };

        // Act
        await manager.SpawnAsync(request);

        // Assert
        sessionStore.Verify(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Once);
        sessionStore.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Once);
        savedSession.ShouldNotBeNull();
        savedSession!.Session.ConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public async Task SpawnAsync_WithoutConversationId_DoesNotStampChildSession()
    {
        // Arrange
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var sessionStore = new Mock<ISessionStore>();

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do work",
            TimeoutSeconds = 600
        };

        // Act
        await manager.SpawnAsync(request);

        // Assert - session store should not be called for stamping when no ConversationId provided
        sessionStore.Verify(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessionStore.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_WithConversationId_WhenSessionStoreMissing_SpawnsSuccessfully()
    {
        // Arrange - no session store injected; spawning should still succeed (graceful degradation)
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var manager = CreateManager(supervisor.Object, sessionStore: null);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            ConversationId = ConversationId.From("any-conv")
        };

        // Act + Assert - should not throw
        var result = await manager.SpawnAsync(request);
        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task SpawnAsync_WithConversationId_WhenChildSessionNotFound_SpawnsSuccessfully()
    {
        // Arrange - session store returns null for the child session; should degrade gracefully
        var childHandle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);

        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);

        var manager = CreateManager(supervisor.Object, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do work",
            TimeoutSeconds = 600,
            ConversationId = ConversationId.From("any-conv")
        };

        // Act + Assert - should not throw
        var result = await manager.SpawnAsync(request);
        result.ShouldNotBeNull();
        result.Status.ShouldBe(SubAgentStatus.Running);
        sessionStore.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DefaultSubAgentManager CreateManager(
        IAgentSupervisor supervisor,
        ISessionStore? sessionStore = null)
    {
        var options = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions
        {
            SubAgents = new SubAgentOptions { MaxDepth = 3 }
        });

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent",
                ModelId = "gpt-4o",
                ApiProvider = "openai",
                SystemPrompt = "You are a parent agent."
            });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()));

        return new DefaultSubAgentManager(
            supervisor,
            registry.Object,
            new Mock<BotNexus.Gateway.Abstractions.Activity.IActivityBroadcaster>().Object,
            new Mock<BotNexus.Gateway.Abstractions.Channels.IChannelDispatcher>().Object,
            options,
            new Mock<Microsoft.Extensions.Logging.ILogger<DefaultSubAgentManager>>().Object,
            sessionStore: sessionStore);
    }

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns("session");
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(1, ct);
                return new AgentResponse { Content = "done" };
            });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }
}
