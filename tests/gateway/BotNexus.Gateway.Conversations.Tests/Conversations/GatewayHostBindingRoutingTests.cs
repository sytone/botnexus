using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

/// <summary>
/// Tests proving that the originating ChannelBinding fields (BindingId, DisplayPrefix) are
/// carried through to both streaming and non-streaming direct sends, and that the channel
/// address (which may include a composite suffix like /topic:N) is preserved end-to-end.
/// Covers issues #125, #126, and #123 cleanup.
/// </summary>
public sealed class GatewayHostBindingRoutingTests
{
    private const string AgentIdStr = "agent-a";
    private const string SessionIdStr = "session-bind-1";

    // ──────────────────────────────────────────────────────────────────────
    // #126 — Non-streaming direct send must include BindingId, DisplayPrefix
    // and preserve the composite channel address (no separate ThreadId field).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonStreamingPath_MessageWithCompositeAddress_DirectSendIncludesBindingFields()
    {
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("bind-tg-1"),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-100/topic:42"),
            DisplayPrefix = "[Bot]",
            Mode = BindingMode.Interactive
        };

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-thread-test"),
            AgentId = AgentId.From(AgentIdStr)
        };
        conversation.ChannelBindings.Add(binding);

        var routingResult = new ConversationRoutingResult(
            conversation,
            SessionId.From(SessionIdStr),
            false,
            OriginatingBinding: binding);

        var convRouter = new Mock<IConversationRouter>();
        convRouter
            .Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<BotNexus.Domain.Primitives.ChannelAddress>(),
                It.IsAny<BotNexus.Domain.Primitives.ConversationId?>(), It.IsAny<CancellationToken>(), It.IsAny<BotNexus.Domain.World.CitizenId?>()))
            .ReturnsAsync(routingResult);
        convRouter
            .Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var handle = CreatePromptHandle(AgentIdStr, SessionIdStr, "response text");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From(AgentIdStr), SessionId.From(SessionIdStr), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From(SessionIdStr), AgentId.From(AgentIdStr));

        var channel = CreateChannelAdapter("telegram", supportsStreaming: false);
        OutboundMessage? capturedOutbound = null;
        channel
            .Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => capturedOutbound = m)
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(supervisor.Object, sessions, convRouter.Object, CreateChannelManager(channel.Object));

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("chat-100/topic:42"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>()
        });

        capturedOutbound.ShouldNotBeNull("adapter.SendAsync should have been called for non-streaming path");
        capturedOutbound!.ChannelAddress.ShouldBe(ChannelAddress.From("chat-100/topic:42"), "composite ChannelAddress must round-trip on direct send");
        capturedOutbound.BindingId?.Value.ShouldBe("bind-tg-1", "BindingId from originating binding must be stamped on direct send");
        capturedOutbound.DisplayPrefix.ShouldBe("[Bot]", "DisplayPrefix from originating binding must be stamped on direct send");
    }

    // ──────────────────────────────────────────────────────────────────────
    // #125 — Streaming direct send must use the composite channel address verbatim
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamingPath_MessageWithCompositeAddress_StreamingUsesCorrectConversationId()
    {
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("bind-tg-2"),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-200/topic:99"),
            Mode = BindingMode.Interactive
        };

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-stream-test"),
            AgentId = AgentId.From(AgentIdStr)
        };
        conversation.ChannelBindings.Add(binding);

        var routingResult = new ConversationRoutingResult(
            conversation,
            SessionId.From(SessionIdStr),
            false,
            OriginatingBinding: binding);

        var convRouter = new Mock<IConversationRouter>();
        convRouter
            .Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<BotNexus.Domain.Primitives.ChannelAddress>(),
                It.IsAny<BotNexus.Domain.Primitives.ConversationId?>(), It.IsAny<CancellationToken>(), It.IsAny<BotNexus.Domain.World.CitizenId?>()))
            .ReturnsAsync(routingResult);
        convRouter
            .Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(AgentIdStr));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(SessionIdStr));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello world" }
            ]));
        handle.Setup(h => h.StreamAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello world" }
            ]));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From(AgentIdStr), SessionId.From(SessionIdStr), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From(SessionIdStr), AgentId.From(AgentIdStr));

        var capturedStreamConversationIds = new List<string>();
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(ChannelKey.From("telegram"));
        channel.SetupGet(c => c.DisplayName).Returns("telegram");
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<ChannelStreamTarget, string, CancellationToken>((target, _, _) => capturedStreamConversationIds.Add(target.ChannelAddress.Value))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(supervisor.Object, sessions, convRouter.Object, CreateChannelManager(channel.Object));

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("chat-200/topic:99"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>()
        });

        capturedStreamConversationIds.ShouldNotBeEmpty("SendStreamDeltaAsync must be called");
        // The ChannelAddress on the stream target is the composite ChannelAddress verbatim —
        // adapters fold the topic suffix into it themselves before the gateway ever sees it.
        capturedStreamConversationIds.ShouldAllBe(
            cid => cid == "chat-200/topic:99",
            "Streaming target's ChannelAddress must be the composite address so the adapter can route to the correct topic");
    }

    // ──────────────────────────────────────────────────────────────────────
    // #123 cleanup — BindingId should come from resolved binding for fan-out exclusion
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiChannelFanOut_OriginatingBinding_UsedForBindingIdStamp()
    {
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("bind-origin"),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-300"),
            Mode = BindingMode.Interactive
        };

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-fanout-test"),
            AgentId = AgentId.From(AgentIdStr)
        };
        conversation.ChannelBindings.Add(binding);

        var routingResult = new ConversationRoutingResult(
            conversation,
            SessionId.From(SessionIdStr),
            false,
            OriginatingBinding: binding);

        BindingId? capturedOriginatingBindingId = null;
        var convRouter = new Mock<IConversationRouter>();
        convRouter
            .Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<BotNexus.Domain.Primitives.ChannelAddress>(),
                It.IsAny<BotNexus.Domain.Primitives.ConversationId?>(), It.IsAny<CancellationToken>(), It.IsAny<BotNexus.Domain.World.CitizenId?>()))
            .ReturnsAsync(routingResult);
        convRouter
            .Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .Callback<SessionId, BindingId?, CancellationToken>((_, bindId, _) => capturedOriginatingBindingId = bindId)
            .ReturnsAsync([]);

        var handle = CreatePromptHandle(AgentIdStr, SessionIdStr, "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From(AgentIdStr), SessionId.From(SessionIdStr), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From(SessionIdStr), AgentId.From(AgentIdStr));

        var channel = CreateChannelAdapter("telegram", supportsStreaming: false);

        await using var host = CreateHost(supervisor.Object, sessions, convRouter.Object, CreateChannelManager(channel.Object));

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            SenderId = "user-1",
            Sender = CitizenId.Of(UserId.From("user-1")),
            ChannelAddress = ChannelAddress.From("chat-300"),
            Content = "hello",
            Metadata = new Dictionary<string, object?>()
        });

        capturedOriginatingBindingId.ShouldNotBeNull();
        capturedOriginatingBindingId.Value.Value.ShouldBe("bind-origin",
            "The originating BindingId from the resolved binding must be passed to GetOutboundBindingsAsync for fan-out exclusion");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionId));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        handle.Setup(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        return handle;
    }

    private static Mock<IChannelAdapter> CreateChannelAdapter(string channelType, bool supportsStreaming)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(ChannelKey.From(channelType));
        channel.SetupGet(c => c.DisplayName).Returns(channelType);
        channel.SetupGet(c => c.SupportsStreaming).Returns(supportsStreaming);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return channel;
    }

    private static IChannelManager CreateChannelManager(IChannelAdapter? adapter = null)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns(adapter is null ? [] : [adapter]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            adapter is not null && channelType.Equals(adapter.ChannelType) ? adapter : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            adapter is not null && channelType.Equals(adapter.ChannelType) ? adapter : null);
        return manager.Object;
    }

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IConversationRouter? conversationRouter,
        IChannelManager channelManager)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentIdStr]);

        return new GatewayHost(
            supervisor,
            router.Object,
            sessions,
            new NullActivityBroadcaster(),
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: conversationRouter);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class NullActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }
}


