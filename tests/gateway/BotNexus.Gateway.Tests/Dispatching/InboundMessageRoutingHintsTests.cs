using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the strongly-typed routing-hint contract on
/// <see cref="InboundMessageRoutingHints"/> introduced by F-10 sub-PR 6.2 (#582)
/// and promoted to a first-class property on
/// <see cref="InboundMessage.RoutingHints"/> by sub-PR 6.3 (#586).
/// Locks the lift behaviour so future writer-site changes that wire raw
/// strings through <see cref="InboundMessageRoutingHints.LiftFromStrings"/>
/// continue to produce the expected null / typed projections.
/// </summary>
public sealed class InboundMessageRoutingHintsTests
{
    [Fact]
    public void FromMessage_WithAllThreeFieldsSet_LiftsAllToTypedHints()
    {
        var message = CreateMessage(
            targetAgentId: "agent-a",
            sessionId: "session-42",
            conversationId: "c_abc123");

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedAgentId.ShouldBe(AgentId.From("agent-a"));
        hints.RequestedSessionId.ShouldBe(SessionId.From("session-42"));
        hints.RequestedConversationId.ShouldBe(ConversationId.From("c_abc123"));
    }

    [Fact]
    public void FromMessage_WithAllFieldsNull_ReturnsAllNullHints()
    {
        var message = CreateMessage(targetAgentId: null, sessionId: null, conversationId: null);

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedAgentId.ShouldBeNull();
        hints.RequestedSessionId.ShouldBeNull();
        hints.RequestedConversationId.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void FromMessage_WithEmptyOrWhitespaceFields_LiftsToNull_DoesNotThrow(string blank)
    {
        // Adapter contract: channel adapters have historically populated the legacy
        // string fields with empty strings on "no override" instead of null. The
        // Vogen From() factories reject whitespace — this helper MUST absorb the
        // mismatch silently to keep the inbound pipeline from crashing.
        var message = CreateMessage(targetAgentId: blank, sessionId: blank, conversationId: blank);

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedAgentId.ShouldBeNull();
        hints.RequestedSessionId.ShouldBeNull();
        hints.RequestedConversationId.ShouldBeNull();
    }

    [Fact]
    public void FromMessage_NullMessage_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => InboundMessageRoutingHints.FromMessage(null!));
    }

    [Fact]
    public void FromMessage_WithOnlyTargetAgentIdSet_LiftsOnlyAgentId_LeavesOthersNull()
    {
        // Independence pin: catches a regression where TryLift* is rewired to read
        // the wrong source field. The all-set / all-null pins above wouldn't catch
        // it; only an "only-X-set" assertion does.
        var message = CreateMessage(targetAgentId: "only-agent");

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedAgentId.ShouldBe(AgentId.From("only-agent"));
        hints.RequestedSessionId.ShouldBeNull();
        hints.RequestedConversationId.ShouldBeNull();
    }

    [Fact]
    public void FromMessage_WithOnlySessionIdSet_LiftsOnlySessionId_LeavesOthersNull()
    {
        var message = CreateMessage(sessionId: "only-session");

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedSessionId.ShouldBe(SessionId.From("only-session"));
        hints.RequestedAgentId.ShouldBeNull();
        hints.RequestedConversationId.ShouldBeNull();
    }

    [Fact]
    public void FromMessage_WithOnlyConversationIdSet_LiftsOnlyConversationId_LeavesOthersNull()
    {
        var message = CreateMessage(conversationId: "only-conv");

        var hints = InboundMessageRoutingHints.FromMessage(message);

        hints.RequestedConversationId.ShouldBe(ConversationId.From("only-conv"));
        hints.RequestedAgentId.ShouldBeNull();
        hints.RequestedSessionId.ShouldBeNull();
    }

    [Fact]
    public void Empty_ReturnsSingletonAllNullInstance()
    {
        var first = InboundMessageRoutingHints.Empty;
        var second = InboundMessageRoutingHints.Empty;

        first.RequestedAgentId.ShouldBeNull();
        first.RequestedSessionId.ShouldBeNull();
        first.RequestedConversationId.ShouldBeNull();
        ReferenceEquals(first, second).ShouldBeTrue(
            "Empty must be a cached singleton — code paths that fall back to it should not " +
            "allocate per inbound message.");
    }

    [Fact]
    public void DirectConstructor_AcceptsTypedFields_AndPreservesThem()
    {
        var hints = new InboundMessageRoutingHints(
            RequestedAgentId: AgentId.From("agent-x"),
            RequestedSessionId: SessionId.From("s_x"),
            RequestedConversationId: ConversationId.From("c_x"));

        hints.RequestedAgentId.ShouldBe(AgentId.From("agent-x"));
        hints.RequestedSessionId.ShouldBe(SessionId.From("s_x"));
        hints.RequestedConversationId.ShouldBe(ConversationId.From("c_x"));
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
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(targetAgentId, sessionId, conversationId)
        };
}
