using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Pins the explicit inbound isolation boundary introduced by #2123.
/// </summary>
/// <remarks>
/// Before #2123 the isolation unit was implicit and derived from
/// <c>RequestedSessionId ?? channelType:channelAddress</c>. For webhooks that
/// collapsed to <c>webhook:&lt;webhookId&gt;</c>, so two registrations pinned to
/// one conversation landed on two different queues and raced that conversation's
/// <c>active_session_id</c>. The declared policy is that the canonical
/// <b>conversation</b> is the unit of isolation, so it must win over both the
/// session hint and the channel composite.
/// </remarks>
public sealed class InboundIsolationKeyTests
{
    [Fact]
    public void ForMessage_WithConversationHint_IsolatesOnConversation()
    {
        var key = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: "conv-1"));

        key.Scope.ShouldBe(InboundIsolationScope.Conversation);
        key.Value.ShouldBe("conversation:conv-1");
    }

    [Fact]
    public void ForMessage_ConversationHintWinsOverSessionHint()
    {
        // The conversation owns active_session_id, history, pending ask_user, todo and
        // canvas state. Isolating on the session would let two sessions in the same
        // conversation run overlapping turns and stomp all of it.
        var key = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: "sess-1", conversationId: "conv-1"));

        key.Scope.ShouldBe(InboundIsolationScope.Conversation);
        key.Value.ShouldBe("conversation:conv-1");
    }

    [Fact]
    public void ForMessage_TwoRegistrationsPinnedToOneConversation_ShareOneKey()
    {
        // The #2123 regression in key form: differing channel addresses (two webhook
        // registration ids) must NOT produce two isolation units when both are pinned
        // to the same canonical conversation.
        var first = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: "conv-shared"));
        var second = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-b", sessionId: null, conversationId: "conv-shared"));

        second.ShouldBe(first);
    }

    [Fact]
    public void ForMessage_DifferentConversations_ProduceDifferentKeys()
    {
        var first = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: "conv-1"));
        var second = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: "conv-2"));

        second.ShouldNotBe(first);
    }

    [Fact]
    public void ForMessage_WithSessionHintOnly_IsolatesOnSession()
    {
        var key = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: "sess-1", conversationId: null));

        key.Scope.ShouldBe(InboundIsolationScope.Session);
        key.Value.ShouldBe("session:sess-1");
    }

    [Fact]
    public void ForMessage_WithNoHints_FallsBackToChannelComposite()
    {
        var key = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: null));

        key.Scope.ShouldBe(InboundIsolationScope.Channel);
        key.Value.ShouldBe("channel:webhook:hook-a");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ForMessage_WithBlankHints_FallsBackToChannelComposite(string blank)
    {
        // Adapters have historically supplied empty strings; LiftFromStrings normalises
        // those to "no hint" and the isolation key must not produce a blank unit.
        var message = CreateMessage(channelAddress: "hook-a", sessionId: blank, conversationId: blank);

        var key = InboundIsolationKey.ForMessage(message);

        key.Scope.ShouldBe(InboundIsolationScope.Channel);
        key.Value.ShouldBe("channel:webhook:hook-a");
    }

    [Fact]
    public void ForMessage_NullMessage_Throws()
        => Should.Throw<ArgumentNullException>(() => InboundIsolationKey.ForMessage(null!));

    [Fact]
    public void ForMessage_ConversationAndSessionWithSameRawId_DoNotCollide()
    {
        // Scope prefixes exist so a conversation id can never alias a session id.
        var conversation = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: null, conversationId: "same-id"));
        var session = InboundIsolationKey.ForMessage(
            CreateMessage(channelAddress: "hook-a", sessionId: "same-id", conversationId: null));

        session.ShouldNotBe(conversation);
    }

    private static InboundMessage CreateMessage(
        string channelAddress, string? sessionId, string? conversationId)
        => new()
        {
            ChannelType = ChannelKey.From("webhook"),
            ChannelAddress = ChannelAddress.From(channelAddress),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hi",
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, conversationId)
        };
}
