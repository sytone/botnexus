using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for the explicit-<c>conversationId</c> routing path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> <see cref="VirtualWorld.WhenSendsAsync"/>
/// accepts an explicit <c>conversationId</c> override that maps to
/// <c>InboundMessage.ConversationId</c>. This drives the gateway down a
/// <i>different routing branch</i> than the implicit
/// <c>(channelType, channelAddress)</c> lookup — specifically the
/// <c>DefaultConversationRouter</c> "explicit conversation id" fast path
/// (see <c>DefaultConversationRouter.cs:46-104</c>). The implicit and explicit
/// paths share zero code beneath the entry point, so they can silently diverge
/// in behaviour (e.g. explicit-id binding registration, fan-out targeting, or
/// session creation under same-address-different-conversation contention).
/// </para>
/// <para>
/// <b>Documented contract</b> (verified via <c>DefaultConversationRouter.cs:46-55</c>):
/// the explicit <c>conversationId</c> on inbound is treated as "look up an existing
/// conversation". A non-existent id <b>falls through</b> to address-based routing;
/// clients <i>cannot</i> create a new conversation with a chosen id by passing it on
/// inbound. The test below pins this contract and probes the
/// "bind-on-first-use" side effect described at <c>:67-86</c>.
/// </para>
/// </remarks>
public sealed class ExplicitConversationIdScenario
{
    [Fact]
    public async Task ExplicitConversationId_OnExistingConversation_RoutesToThatConversation_AndBindsChannelAddressIfNew()
    {
        // Arrange — create a conversation by sending a first inbound (the natural
        // creation path). Then capture the generated id and use it as the explicit
        // ConversationId for a SECOND inbound that arrives on a different channel
        // address. The router's bind-on-first-use path (router :67-86) should bind
        // the new channel address to the existing conversation, NOT create a second
        // conversation. This is the user-visible "open same conversation from a
        // second device" flow.
        await using var world = await VirtualWorld.StartAsync();
        _ = await world.GivenAgentAsync("explicit-id-agent");

        await world.WhenSendsAsync(
            fromUser: "ivy",
            toAgent: "explicit-id-agent",
            channelAddress: "ivy-tablet",
            content: "first message from tablet");
        _ = await world.WaitForReplyAsync(channelAddress: "ivy-tablet");

        var initialConversation = (await world.ListConversationsForAgentAsync("explicit-id-agent")).Single();
        var sharedConversationId = initialConversation.ConversationId;

        // Act — send a second inbound from a DIFFERENT channel address but with the
        // explicit conversation id of the first conversation. Must route to that
        // conversation (not create a second one).
        await world.WhenSendsAsync(
            fromUser: "ivy",
            toAgent: "explicit-id-agent",
            channelAddress: "ivy-phone",
            content: "follow-up from phone, same conversation",
            conversationId: sharedConversationId);
        _ = await world.WaitForReplyAsync(channelAddress: "ivy-phone");

        // Assert — exactly one conversation still exists.
        var conversations = await world.ListConversationsForAgentAsync("explicit-id-agent");
        conversations.Count.ShouldBe(
            1,
            $"explicit ConversationId on a second inbound from a different channel address should NOT " +
            $"create a second conversation; observed {conversations.Count} conversations — " +
            $"router fell back to address-based path despite the explicit id being valid");

        var conversation = conversations[0];
        conversation.ConversationId.ShouldBe(sharedConversationId);

        // And the conversation now has TWO channel-address bindings — proving the
        // bind-on-first-use side effect at router lines 67-86 fired.
        conversation.ChannelBindings.Count.ShouldBe(
            2,
            $"the router should have added a binding for the new channel address; observed " +
            $"{conversation.ChannelBindings.Count} bindings — bind-on-first-use regression");

        var addresses = conversation.ChannelBindings.Select(b => b.ChannelAddress).ToHashSet();
        addresses.ShouldContain("ivy-tablet", "the original address must still be bound");
        addresses.ShouldContain("ivy-phone", "the new address must be bound via bind-on-first-use");

        // And both inbound messages reached the LLM.
        world.Provider.TurnCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExplicitConversationId_OnNonExistentId_FallsBackToAddressRouting_AndPersistsNewConversation()
    {
        // Bug-probing companion: pins the "non-existent explicit id falls through to
        // binding lookup" contract documented at DefaultConversationRouter.cs:50-55.
        // A regression that throws on a non-existent id (or silently drops the
        // inbound) would surface here.
        await using var world = await VirtualWorld.StartAsync();
        _ = await world.GivenAgentAsync("fallback-agent");

        var bogusId = "non-existent-" + Guid.NewGuid().ToString("N");

        // Act — inbound carries an explicit conversationId that doesn't exist.
        await world.WhenSendsAsync(
            fromUser: "jack",
            toAgent: "fallback-agent",
            content: "hello",
            conversationId: bogusId);
        _ = await world.WaitForReplyAsync(channelAddress: "jack");

        // Assert — the inbound was processed (provider invoked), one conversation
        // exists for the agent (via the fallback address-based path), and its id
        // is NOT the bogus id we provided.
        world.Provider.TurnCount.ShouldBe(1);
        var conversations = await world.ListConversationsForAgentAsync("fallback-agent");
        conversations.Count.ShouldBe(
            1,
            "the fallback address-based path should create exactly one conversation; " +
            $"observed {conversations.Count}");
        conversations[0].ConversationId.ShouldNotBe(
            bogusId,
            "the gateway must NOT persist a client-supplied conversation id (security boundary)");
        conversations[0].ChannelBindings.Single().ChannelAddress.ShouldBe("jack");
    }
}

