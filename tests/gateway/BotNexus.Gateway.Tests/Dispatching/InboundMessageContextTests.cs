using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the strongly-typed override contract on <see cref="InboundMessageContext"/>
/// added in F-10 sub-PR 6.1. Pre-PR these overrides lived as <c>string?</c> directly
/// on <see cref="InboundMessage"/>; <see cref="InboundMessageContext.FromInboundMessage"/>
/// is the only legitimate site that may still read those legacy fields — every other
/// consumer must read the typed properties on the context.
/// </summary>
public sealed class InboundMessageContextTests
{
    [Fact]
    public void FromInboundMessage_WithAllOverridesSet_LiftsAllThreeToTypedProperties()
    {
        var message = CreateMessage(
            targetAgentId: "agent-a",
            sessionId: "session-42",
            conversationId: "c_abc123");

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("agent-a"), message);

        context.RequestedAgentId.ShouldBe(AgentId.From("agent-a"));
        context.RequestedSessionId.ShouldBe(SessionId.From("session-42"));
        context.RequestedConversationId.ShouldBe(ConversationId.From("c_abc123"));
    }

    [Fact]
    public void FromInboundMessage_WithNullOverrides_LeavesAllTypedPropertiesNull()
    {
        var message = CreateMessage(
            targetAgentId: null,
            sessionId: null,
            conversationId: null);

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("agent-a"), message);

        context.RequestedAgentId.ShouldBeNull();
        context.RequestedSessionId.ShouldBeNull();
        context.RequestedConversationId.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FromInboundMessage_WithEmptyOrWhitespaceOverrides_LeavesTypedPropertiesNull_GracefullyDoesNotThrow(string blank)
    {
        var message = CreateMessage(
            targetAgentId: blank,
            sessionId: blank,
            conversationId: blank);

        // The shim must NOT throw on whitespace inputs — channel adapters pass empty strings
        // historically. The Vogen From(...) factories reject whitespace, so the shim must
        // gracefully degrade to null rather than crash the inbound pipeline.
        var context = InboundMessageContext.FromInboundMessage(AgentId.From("agent-a"), message);

        context.RequestedAgentId.ShouldBeNull();
        context.RequestedSessionId.ShouldBeNull();
        context.RequestedConversationId.ShouldBeNull();
    }

    [Fact]
    public void FromInboundMessage_NullMessage_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            InboundMessageContext.FromInboundMessage(AgentId.From("agent-a"), null!));
    }

    [Fact]
    public void DirectConstructor_AcceptsTypedOverrides_AndPreservesThem()
    {
        var message = CreateMessage();
        var source = new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId);

        var context = new InboundMessageContext(
            AgentId.From("agent-x"),
            message,
            source,
            RequestedConversationId: ConversationId.From("c_explicit"),
            RequestedAgentId: AgentId.From("agent-target"),
            RequestedSessionId: SessionId.From("s_target"));

        context.RequestedConversationId.ShouldBe(ConversationId.From("c_explicit"));
        context.RequestedAgentId.ShouldBe(AgentId.From("agent-target"));
        context.RequestedSessionId.ShouldBe(SessionId.From("s_target"));
    }

    [Fact]
    public void DirectConstructor_AcceptsAllNullOverrides_AsDefault()
    {
        var message = CreateMessage();
        var source = new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId);

        var context = new InboundMessageContext(AgentId.From("agent-x"), message, source);

        context.RequestedConversationId.ShouldBeNull();
        context.RequestedAgentId.ShouldBeNull();
        context.RequestedSessionId.ShouldBeNull();
    }

    [Fact]
    public void FromInboundMessage_PreservesChannelSourceDetails()
    {
        var message = CreateMessage(targetAgentId: "agent-a");

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("agent-a"), message);

        context.AgentId.ShouldBe(AgentId.From("agent-a"));
        context.Message.ShouldBe(message);
        context.Source.ChannelType.ShouldBe(message.ChannelType);
        context.Source.ChannelAddress.ShouldBe(message.ChannelAddress);
        context.Source.SenderId.ShouldBe(message.SenderId);
        context.Source.BindingId.ShouldBe(message.BindingId);
    }

    [Fact]
    public void FromInboundMessage_WithOnlyTargetAgentIdSet_LiftsOnlyAgentId_LeavesOthersNull()
    {
        // Pin-vs-impl critique fold-in: regression-shape coverage for "lift pulled from the
        // wrong source field". If TryLiftAgentId were ever rewired to read message.SessionId
        // (or vice-versa), the all-set / all-null facts wouldn't catch it; only an
        // independence pin does.
        var message = CreateMessage(targetAgentId: "only-agent");

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("router-agent"), message);

        context.RequestedAgentId.ShouldBe(AgentId.From("only-agent"));
        context.RequestedSessionId.ShouldBeNull("only TargetAgentId was set on the inbound message; SessionId must remain null");
        context.RequestedConversationId.ShouldBeNull("only TargetAgentId was set on the inbound message; ConversationId must remain null");
    }

    [Fact]
    public void FromInboundMessage_WithOnlySessionIdSet_LiftsOnlySessionId_LeavesOthersNull()
    {
        var message = CreateMessage(sessionId: "only-session");

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("router-agent"), message);

        context.RequestedSessionId.ShouldBe(SessionId.From("only-session"));
        context.RequestedAgentId.ShouldBeNull("only SessionId was set on the inbound message; TargetAgentId must remain null");
        context.RequestedConversationId.ShouldBeNull("only SessionId was set on the inbound message; ConversationId must remain null");
    }

    [Fact]
    public void FromInboundMessage_WithOnlyConversationIdSet_LiftsOnlyConversationId_LeavesOthersNull()
    {
        var message = CreateMessage(conversationId: "only-conv");

        var context = InboundMessageContext.FromInboundMessage(AgentId.From("router-agent"), message);

        context.RequestedConversationId.ShouldBe(ConversationId.From("only-conv"));
        context.RequestedAgentId.ShouldBeNull("only ConversationId was set on the inbound message; TargetAgentId must remain null");
        context.RequestedSessionId.ShouldBeNull("only ConversationId was set on the inbound message; SessionId must remain null");
    }

    private static InboundMessage CreateMessage(
        string? targetAgentId = null,
        string? sessionId = null,
        string? conversationId = null)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello",
            TargetAgentId = targetAgentId,
            SessionId = sessionId,
            ConversationId = conversationId
        };
}
