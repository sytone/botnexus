using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the issue #2149 contract at the GatewayHost seam: a turn initiated by a
/// <see cref="MessageKind.SubAgentCompletion"/> inbound message stamps the inbound completion
/// entry as <c>subagent-completion</c> and the parent agent's response entry (and the outbound
/// delivery) as <c>subagent-response</c>, while an ordinary human turn stays <c>message</c>.
/// The kind is orthogonal to <see cref="MessageRole"/>, which stays user/assistant.
/// </summary>
public sealed partial class GatewayHostTests
{
    [Fact]
    public async Task DispatchAsync_WhenSubAgentCompletion_StampsCompletionAndResponseKinds()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["parent-agent"]);
        var handle = CreatePromptHandle("parent-agent", "parent-session", "parent reply");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("parent-agent"), SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = SessionId.From("parent-session"), AgentId = AgentId.From("parent-agent") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(SessionId.From("parent-session"), AgentId.From("parent-agent"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("internal", supportsStreaming: false);
        OutboundMessage? outbound = null;
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => outbound = m)
            .Returns(Task.CompletedTask);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateSubAgentCompletionMessage("parent-session"));

        var completion = session.History.Single(e => e.Content == "[completion]");
        completion.ResolveKind().ShouldBe(MessageKind.SubAgentCompletion);

        var reply = session.History.Single(e => e.Content == "parent reply");
        reply.Role.ShouldBe(MessageRole.Assistant, "role stays the LLM role, not the kind");
        reply.ResolveKind().ShouldBe(MessageKind.SubAgentResponse);

        outbound.ShouldNotBeNull();
        outbound!.ResolveKind().ShouldBe(MessageKind.SubAgentResponse,
            "the outbound delivery must expose the typed kind to channel adapters (#2149)");
    }

    [Fact]
    public async Task DispatchAsync_WhenOrdinaryUserTurn_StampsMessageKindOnly()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = SessionId.From("session-1"), AgentId = AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        OutboundMessage? outbound = null;
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => outbound = m)
            .Returns(Task.CompletedTask);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        session.History.ShouldAllBe(e => e.Kind == null);
        session.History.ShouldAllBe(e => e.ResolveKind() == MessageKind.Message);
        outbound.ShouldNotBeNull();
        outbound!.Kind.ShouldBeNull("an ordinary turn must not stamp a non-default kind");
        outbound.ResolveKind().ShouldBe(MessageKind.Message);
    }

    private static InboundMessage CreateSubAgentCompletionMessage(string sessionId) => new()
    {
        ChannelType = ChannelKey.From("internal"),
        SenderId = "subagent:child-1",
        Sender = CitizenId.Of(AgentId.From("child-agent")),
        ChannelAddress = ChannelAddress.From(sessionId),
        Content = "[completion]",
        RoutingHints = new InboundMessageRoutingHints(
            RequestedAgentId: AgentId.From("parent-agent"),
            RequestedSessionId: SessionId.From(sessionId),
            RequestedConversationId: null),
        Kind = MessageKind.SubAgentCompletion,
        Metadata = new Dictionary<string, object?>
        {
            ["messageType"] = "subagent-completion",
            ["subAgentId"] = "child-1"
        }
    };
}
