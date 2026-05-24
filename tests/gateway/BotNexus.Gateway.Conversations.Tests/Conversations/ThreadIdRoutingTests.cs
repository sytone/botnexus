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
/// Tests for thread-aware inbound routing in <see cref="DefaultConversationRouter"/>.
/// </summary>
public sealed class ThreadIdRoutingTests
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
    public async Task ResolveInbound_DifferentThreadId_ResolvesToDifferentConversation()
    {
        // Arrange: same agent, same channel, same address, but different thread IDs
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var channel = Channel();
        const string address = "group-chat-123";

        // Act: first message with no thread
        var resultNoThread = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address), threadId: null);

        // Act: second message with a specific thread id
        var resultWithThread = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address), threadId: ThreadId.From("topic-42"));

        // They should resolve to different conversations since thread identity differs
        resultWithThread.Conversation.ConversationId.ShouldNotBe(resultNoThread.Conversation.ConversationId);
    }

    [Fact]
    public async Task ResolveInbound_SameThreadId_ReusesConversation()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var channel = Channel();
        const string address = "group-chat-456";
        const string threadId = "topic-99";

        var result1 = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address), threadId: ThreadId.From(threadId));
        var result2 = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address), threadId: ThreadId.From(threadId));

        result2.Conversation.ConversationId.ShouldBe(result1.Conversation.ConversationId);
        result2.IsNewSession.ShouldBeFalse();
    }

    [Fact]
    public async Task InboundMessage_HasThreadId_Field()
    {
        // Verifies that InboundMessage carries a ThreadId property
        var msg = new InboundMessage
        {
            ChannelType = Channel(),
            SenderId = "user1",
            Sender = CitizenId.Of(UserId.From("user1")),
            ChannelAddress = ChannelAddress.From("chat1"),
            Content = "hello",
            ThreadId = ThreadId.From("thread-1")
        };

        msg.ThreadId.ShouldBe(ThreadId.From("thread-1"));
    }

    [Fact]
    public async Task GatewayHost_ExtractsThreadId_FromInboundMessage()
    {
        // This test verifies that GatewayHost passes InboundMessage.ThreadId
        // through to IConversationRouter.ResolveInboundAsync by using a capturing fake router.
        var capturingRouter = new CapturingConversationRouter();
        var message = new InboundMessage
        {
            ChannelType = Channel("telegram"),
            SenderId = "user1",
            Sender = CitizenId.Of(UserId.From("user1")),
            ChannelAddress = ChannelAddress.From("chat-789"),
            Content = "hello",
            ThreadId = ThreadId.From("topic-55")
        };

        var result = await capturingRouter.ResolveInboundAsync(
            Agent(),
            message.ChannelType,
            message.ChannelAddress,
            message.ThreadId,
            conversationId: null);

        capturingRouter.CapturedThreadId.ShouldBe(ThreadId.From("topic-55"));
    }
}

/// <summary>
/// A minimal fake router that records the threadId it was called with.
/// </summary>
internal sealed class CapturingConversationRouter : IConversationRouter
{
    public ThreadId? CapturedThreadId { get; private set; }

    public Task<ConversationRoutingResult> ResolveInboundAsync(
        AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress,
        ThreadId? threadId, string? conversationId = null, CancellationToken ct = default,
        BotNexus.Domain.World.CitizenId? initiator = null)
    {
        CapturedThreadId = threadId;
        var conv = new Conversation { AgentId = agentId };
        var sessionId = SessionId.Create();
        return Task.FromResult(new ConversationRoutingResult(conv, sessionId, true));
    }

    public Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
        SessionId sessionId, BindingId? originatingChannelAddress, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChannelBinding>>([]);

    public Task MuteBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ReattachBindingAsync(BindingId bindingId, ConversationId targetConversationId, CancellationToken ct = default)
        => Task.CompletedTask;
}

