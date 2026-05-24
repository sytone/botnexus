using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

/// <summary>
/// Tests that the router treats <see cref="ChannelAddress"/> opaquely — different composite
/// addresses (e.g. <c>chatId/topic:N</c>) resolve to distinct conversations even though they
/// share the same underlying chat. Native sub-addresses are encoded by the originating adapter
/// before the router ever sees them.
/// </summary>
public sealed class CompositeAddressRoutingTests
{
    private static AgentId Agent(string id = "agent1") => AgentId.From(id);
    private static ChannelKey Channel(string type = "telegram") => ChannelKey.From(type);

    private static DefaultConversationRouter CreateRouter(
        IConversationStore? conversationStore = null,
        ISessionStore? sessionStore = null)
        => new(
            conversationStore ?? new InMemoryConversationStore(),
            sessionStore ?? new InMemorySessionStore(),
            NullLogger<DefaultConversationRouter>.Instance);

    [Fact]
    public async Task ResolveInbound_DifferentCompositeAddresses_ResolveToDifferentConversations()
    {
        // Arrange: same agent, same channel — addresses differ only by composite topic suffix
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var channel = Channel();

        // Act: root-chat message has no topic suffix
        var resultRoot = await router.ResolveInboundAsync(
            agentId, channel, ChannelAddress.From("group-chat-123"));

        // Act: topic-specific message has topic suffix encoded by the adapter
        var resultTopic = await router.ResolveInboundAsync(
            agentId, channel, ChannelAddress.From("group-chat-123/topic:42"));

        // They resolve to different conversations because the addresses differ
        resultTopic.Conversation.ConversationId.ShouldNotBe(resultRoot.Conversation.ConversationId);
    }

    [Fact]
    public async Task ResolveInbound_SameCompositeAddress_ReusesConversation()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var channel = Channel();
        const string address = "group-chat-456/topic:99";

        var result1 = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address));
        var result2 = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address));

        result2.Conversation.ConversationId.ShouldBe(result1.Conversation.ConversationId);
        result2.IsNewSession.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveInbound_TwoTopicsInSameChat_ResolveToDistinctConversations()
    {
        // Regression for the historic "thread-binding hack" defect: two topics inside the
        // same chat resolved into the same conversation when threadId was null on either side.
        // Composite address encoding makes the addresses themselves distinct so the bug is
        // structurally impossible.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var channel = Channel();

        var result42 = await router.ResolveInboundAsync(
            agentId, channel, ChannelAddress.From("chat-555/topic:42"));
        var result99 = await router.ResolveInboundAsync(
            agentId, channel, ChannelAddress.From("chat-555/topic:99"));

        result42.Conversation.ConversationId.ShouldNotBe(result99.Conversation.ConversationId);

        // Both conversations got bindings with their respective composite addresses
        result42.OriginatingBinding.ShouldNotBeNull();
        result42.OriginatingBinding!.ChannelAddress.ShouldBe(ChannelAddress.From("chat-555/topic:42"));
        result99.OriginatingBinding.ShouldNotBeNull();
        result99.OriginatingBinding!.ChannelAddress.ShouldBe(ChannelAddress.From("chat-555/topic:99"));
    }
}

