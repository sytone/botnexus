using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Dispatching.Tests;

/// <summary>
/// Unit coverage for <see cref="InboundMessageContext.FromInboundMessage"/> - the projection
/// that normalizes a raw transport <see cref="InboundMessage"/> (plus its typed routing hints)
/// into the immutable dispatch context the orchestrator consumes. Covers the happy path
/// (hints lifted into typed properties), the no-hints path (all requested-* stay null), and
/// the null-guard sad path.
/// </summary>
public sealed class InboundMessageContextTests
{
    private static InboundMessage NewMessage(InboundMessageRoutingHints? hints = null) => new()
    {
        ChannelType = ChannelKey.From("signalr"),
        SenderId = "wire-123",
        Sender = CitizenId.Of(UserId.From("wire-123")),
        ChannelAddress = ChannelAddress.From("chat-1"),
        Content = "hello",
        RoutingHints = hints,
    };

    [Fact]
    public void FromInboundMessage_CopiesSourceIdentityFromMessage()
    {
        var agentId = AgentId.From("farnsworth");
        var message = NewMessage();

        var context = InboundMessageContext.FromInboundMessage(agentId, message);

        Assert.Equal(agentId, context.AgentId);
        Assert.Same(message, context.Message);
        Assert.Equal(message.ChannelType, context.Source.ChannelType);
        Assert.Equal(message.ChannelAddress, context.Source.ChannelAddress);
        Assert.Equal(message.SenderId, context.Source.SenderId);
    }

    [Fact]
    public void FromInboundMessage_NoRoutingHints_LeavesRequestedTargetsNull()
    {
        var context = InboundMessageContext.FromInboundMessage(AgentId.From("a"), NewMessage());

        Assert.Null(context.RequestedConversationId);
        Assert.Null(context.RequestedAgentId);
        Assert.Null(context.RequestedSessionId);
    }

    [Fact]
    public void FromInboundMessage_WithRoutingHints_LiftsTypedRequestedTargets()
    {
        var wantedConversation = ConversationId.Create();
        var wantedSession = SessionId.Create();
        var wantedAgent = AgentId.From("target-agent");
        var hints = new InboundMessageRoutingHints(wantedAgent, wantedSession, wantedConversation);

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("a"), NewMessage(hints));

        Assert.Equal(wantedConversation, context.RequestedConversationId);
        Assert.Equal(wantedSession, context.RequestedSessionId);
        Assert.Equal(wantedAgent, context.RequestedAgentId);
    }

    [Fact]
    public void FromInboundMessage_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => InboundMessageContext.FromInboundMessage(AgentId.From("a"), null!));
    }
}
