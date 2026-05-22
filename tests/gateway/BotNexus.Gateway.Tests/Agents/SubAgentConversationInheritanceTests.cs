using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentConversationInheritanceTests
{
    [Fact]
    public async Task SpawnAsync_WithInheritedConversationId_PinsSessionConversationBeforePrompt()
    {
        // Arrange
        const string inheritedConversationId = "conv-parent-123";
        var capturedConversationId = (string?)null;

        var gatewaySession = new GatewaySession();

        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(gatewaySession);
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((sess, _) =>
                capturedConversationId = sess.Session.ConversationId?.Value)
            .Returns(Task.CompletedTask);

        var handle = BuildHandle();
        var manager = BuildManager(handle, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "do something",
            InheritedConversationId = inheritedConversationId
        };

        // Act
        await manager.SpawnAsync(request);
        await Task.Delay(600); // allow background Task to complete

        // Assert
        capturedConversationId.ShouldBe(inheritedConversationId);
        handle.Verify(h => h.PromptAsync("do something", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SpawnAsync_WithoutInheritedConversationId_DoesNotPinConversation()
    {
        // Arrange
        var sessionStore = new Mock<ISessionStore>();

        var handle = BuildHandle();
        var manager = BuildManager(handle, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "do something"
            // No InheritedConversationId
        };

        // Act
        await manager.SpawnAsync(request);
        await Task.Delay(600);

        // Assert — session store NOT touched for conversation pinning
        sessionStore.Verify(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessionStore.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync("do something", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IAgentHandle> BuildHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        return handle;
    }

    private static DefaultSubAgentManager BuildManager(
        Mock<IAgentHandle> handle,
        ISessionStore? sessionStore = null)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(CreateDescriptor());
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var dispatcher = new Mock<IChannelDispatcher>();

        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new GatewayOptions());

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activity.Object,
            dispatcher.Object,
            options.Object,
            NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: sessionStore);
    }

    private static AgentDescriptor CreateDescriptor() => new()
    {
        AgentId = AgentId.From("parent-agent"),
        DisplayName = "Parent",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        SystemPrompt = "You are a test agent."
    };
}
