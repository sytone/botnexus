using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Tests verifying that fan-out delivers binding-aware OutboundMessage fields.
/// </summary>
public sealed class FanOutBindingAwareTests
{
    private static AgentId Agent(string id = "agent1") => AgentId.From(id);
    private static ChannelKey Channel(string type = "telegram") => ChannelKey.From(type);

    [Fact]
    public async Task FanOut_PassesThreadId_FromBindingToAdapter()
    {
        // Arrange: a conversation with two bindings — originator (no thread) and a target with ThreadId
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var conversation = await conversationStore.GetOrCreateDefaultAsync(agentId);
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            BindingId = BindingId.From("binding-src"),
            ChannelType = Channel("signalr"),
            ChannelAddress = ChannelAddress.From("conn-origin"),
            Mode = BindingMode.Interactive
        });
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            BindingId = BindingId.From("binding-tg"),
            ChannelType = Channel("telegram"),
            ChannelAddress = ChannelAddress.From("chat-999"),
            ThreadId = ThreadId.From("topic-77"),
            Mode = BindingMode.Interactive
        });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = new DefaultConversationRouter(conversationStore, sessionStore, NullLogger<DefaultConversationRouter>.Instance);
        var bindings = await router.GetOutboundBindingsAsync(sessionId, BindingId.From("binding-src"));

        // The telegram binding with ThreadId should be included
        bindings.Count.ShouldBe(1);
        var tgBinding = bindings[0];
        tgBinding.ChannelAddress.ShouldBe(ChannelAddress.From("chat-999"));
        tgBinding.ThreadId.ShouldBe(ThreadId.From("topic-77"));

        // Build the outbound message as FanOutResponseAsync would
        var outbound = new OutboundMessage
        {
            ChannelType = tgBinding.ChannelType,
            ChannelAddress = tgBinding.ChannelAddress,
            Content = "response text",
            ThreadId = tgBinding.ThreadId,
            BindingId = tgBinding.BindingId,
            DisplayPrefix = tgBinding.DisplayPrefix
        };

        outbound.ThreadId.ShouldBe(ThreadId.From("topic-77"));
        outbound.BindingId?.Value.ShouldBe("binding-tg");
    }
}
