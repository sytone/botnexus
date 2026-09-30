using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for #3034: legacy user-message edges must carry
/// explicit delivery intent and addressed ids through the unified inbound seam.
/// </summary>
public sealed class UnifiedInboundEntryPointTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("session-1");
    private static readonly ConversationId Conversation = ConversationId.From("conversation-1");

    [Fact]
    public async Task ChatController_Steer_PostsAddressedSteerToInboundOrchestrator()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns(new AgentInstance
        {
            InstanceId = "instance-1",
            AgentId = Agent,
            SessionId = Session,
            IsolationStrategy = "in-process"
        });
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = Session, AgentId = Agent, ConversationId = Conversation });
        var orchestrator = new CapturingInboundMessageOrchestrator
        {
            AdmissionStatus = InboundDispatchStatus.Steered
        };
        var controller = new ChatController(supervisor.Object, sessions.Object, orchestrator: orchestrator);

        var result = await controller.Steer(
            new AgentControlRequest(Agent.Value, Session.Value, "adjust"),
            CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        var message = orchestrator.Captured.ShouldHaveSingleItem();
        message.Content.ShouldBe("adjust");
        message.RoutingHints.ShouldNotBeNull();
        message.RoutingHints!.RequestedAgentId.ShouldBe(Agent);
        message.RoutingHints.RequestedSessionId.ShouldBe(Session);
        message.RoutingHints.RequestedConversationId.ShouldBe(Conversation);
        message.RoutingHints.DeliveryMode.ShouldBe(InboundDeliveryMode.Steer);
        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GatewayHub_SteerWithMedia_PostsAddressedSteerToInboundOrchestrator()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator { AdmissionStatus = InboundDispatchStatus.Steered };
        var sessions = new InMemorySessionStore();
        await sessions.SaveAsync(new GatewaySession { SessionId = Session, AgentId = Agent, ConversationId = Conversation });
        var hub = SignalRHubTests.CreateHub(orchestrator: orchestrator, sessions: sessions);

        await hub.SteerWithMedia(Agent, Session, "look",
            [new MediaContentPartDto { MimeType = "text/plain", Text = "attachment" }],
            Conversation.Value);

        var message = orchestrator.Captured.ShouldHaveSingleItem();
        message.Content.ShouldBe("look");
        message.ContentParts.ShouldNotBeNull();
        message.ContentParts!.Count.ShouldBe(1);
        message.RoutingHints.ShouldNotBeNull();
        message.RoutingHints!.RequestedAgentId.ShouldBe(Agent);
        message.RoutingHints.RequestedSessionId.ShouldBe(Session);
        message.RoutingHints.RequestedConversationId.ShouldBe(Conversation);
        message.RoutingHints.DeliveryMode.ShouldBe(InboundDeliveryMode.Steer);
    }

    [Fact]
    public async Task GatewayHub_InterruptAndSteer_PostsAddressedInterruptToInboundOrchestrator()
    {
        var orchestrator = new CapturingInboundMessageOrchestrator { AdmissionStatus = InboundDispatchStatus.Steered };
        var sessions = new InMemorySessionStore();
        await sessions.SaveAsync(new GatewaySession { SessionId = Session, AgentId = Agent, ConversationId = Conversation });
        var hub = SignalRHubTests.CreateHub(orchestrator: orchestrator, sessions: sessions);

        var accepted = await hub.InterruptAndSteer(Agent, Session, "stop, do this");

        accepted.ShouldBeTrue();
        var message = orchestrator.Captured.ShouldHaveSingleItem();
        message.RoutingHints.ShouldNotBeNull();
        message.RoutingHints!.RequestedAgentId.ShouldBe(Agent);
        message.RoutingHints.RequestedSessionId.ShouldBe(Session);
        message.RoutingHints.RequestedConversationId.ShouldBe(Conversation);
        message.RoutingHints.DeliveryMode.ShouldBe(InboundDeliveryMode.Interrupt);
    }
}
