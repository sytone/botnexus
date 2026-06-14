using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway;
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
/// Tests for #1394: <c>GatewayHost.FanOutResponseAsync</c> must no longer re-read the session from
/// the store to recover the last assistant content (the caller already holds it), and its per-binding
/// delivery + stale-heal must live in an independently testable <c>DeliverToBindingAsync</c>.
/// </summary>
public sealed class FanOutRedundantReadTests
{
    private const string AgentName = "agent-fan";

    /// <summary>
    /// Finding 1 (perf): the fan-out method must NOT call <see cref="ISessionStore.GetAsync"/>.
    /// The content + conversation id are passed in by the caller, so a full session re-read inside the
    /// fan-out is redundant on every multi-binding fan-out. A mocked router supplies the bindings
    /// directly so the ONLY store access that could occur is the one this change removed.
    /// </summary>
    [Fact]
    public async Task FanOut_DoesNotReReadSessionFromStore()
    {
        var convId = ConversationId.Create();
        var telegram = CreateChannelRecorder("telegram");
        var manager = CreateChannelManager(telegram.Adapter.Object);

        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("tel"),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-100"),
            Mode = BindingMode.Interactive
        };

        var convRouter = new Mock<IConversationRouter>();
        convRouter.Setup(r => r.GetOutboundBindingsAsync(It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([binding]);

        var countingStore = new CountingSessionStore(new InMemorySessionStore());
        await using var host = CreateHostWithStore(countingStore, manager, convRouter.Object);
        countingStore.ResetGetCount();

        await InvokeFanOutAsync(
            host,
            CreateInbound("hello", "signalr", "chat-1", "session-fan"),
            "session-fan",
            lastAssistantContent: "no-reread",
            conversationId: convId);

        // The fan-out delivered the reply to the other binding using the passed content...
        telegram.Messages.Count.ShouldBe(1);
        telegram.Messages[0].Content.ShouldBe("no-reread");
        // ...without the fan-out method itself issuing any GetAsync.
        countingStore.GetCallCount.ShouldBe(0,
            "fan-out must reuse the caller's in-memory content, not re-read the session from the store");
    }

    /// <summary>
    /// Finding 1 (behavior): the delivered content comes from the value the caller passes in, not from
    /// any store read. Proven by giving the store a session with NO assistant entry while still passing
    /// content through — delivery must still happen with the passed content.
    /// </summary>
    [Fact]
    public async Task FanOut_DeliversPassedContent_EvenWhenStoredSessionHasNoAssistantEntry()
    {
        var harness = CreateHarness(responseContent: "ignored-by-fanout");
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(AgentName),
            Title = "Direct",
            ActiveSessionId = SessionId.From("direct-session"),
            ChannelBindings =
            [
                new ChannelBinding { BindingId = BindingId.From("sig"), ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("chat-1") },
                new ChannelBinding { BindingId = BindingId.From("tel"), ChannelType = ChannelKey.From("telegram"), ChannelAddress = ChannelAddress.From("chat-100") }
            ]
        };
        await harness.Conversations.CreateAsync(conversation);

        // Stored session has no assistant entry at all — if fan-out read content from the store it
        // would find nothing and deliver nothing.
        var session = await harness.Sessions.GetOrCreateAsync(SessionId.From("direct-session"), AgentId.From(AgentName));
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);

        await InvokeFanOutAsync(
            harness.Host,
            harness.CreateMessage("hello", "telegram", "chat-100"),
            "direct-session",
            lastAssistantContent: "explicit-content",
            conversationId: conversation.ConversationId);

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].Content.ShouldBe("explicit-content");
    }

    /// <summary>
    /// Finding 1 (behavior): a NO_REPLY-style turn that yields no assistant content must short-circuit
    /// fan-out — nothing is delivered to any other binding. Mirrors the old "missing last-assistant
    /// entry returns early" behaviour.
    /// </summary>
    [Fact]
    public async Task FanOut_WithNullOrEmptyContent_DeliversNothing()
    {
        var harness = CreateHarness();
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(AgentName),
            Title = "Silent",
            ActiveSessionId = SessionId.From("silent-session"),
            ChannelBindings =
            [
                new ChannelBinding { BindingId = BindingId.From("sig"), ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("chat-1") },
                new ChannelBinding { BindingId = BindingId.From("tel"), ChannelType = ChannelKey.From("telegram"), ChannelAddress = ChannelAddress.From("chat-100") }
            ]
        };
        await harness.Conversations.CreateAsync(conversation);
        var session = await harness.Sessions.GetOrCreateAsync(SessionId.From("silent-session"), AgentId.From(AgentName));
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);

        await InvokeFanOutAsync(harness.Host, harness.CreateMessage("hello", "telegram", "chat-100"), "silent-session",
            lastAssistantContent: null, conversationId: conversation.ConversationId);
        await InvokeFanOutAsync(harness.Host, harness.CreateMessage("hello", "telegram", "chat-100"), "silent-session",
            lastAssistantContent: string.Empty, conversationId: conversation.ConversationId);

        harness.SignalR.Messages.ShouldBeEmpty();
        harness.Telegram.Messages.ShouldBeEmpty();
    }

    /// <summary>
    /// Finding 2 (refactor): the extracted <c>DeliverToBindingAsync</c> can be driven in isolation and
    /// performs the stale-binding self-heal (demote to Muted) using the passed conversation id, without
    /// any session re-read.
    /// </summary>
    [Fact]
    public async Task DeliverToBinding_OnStaleConnection_DemotesBindingToMuted()
    {
        var convId = ConversationId.Create();
        var bindingId = BindingId.From("stale-binding");

        var staleAdapter = new Mock<IChannelAdapter>();
        staleAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        staleAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleChannelConnectionException(bindingId, convId));

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([staleAdapter.Object]);
        manager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(staleAdapter.Object);
        manager.Setup(m => m.Get(ChannelKey.From("signalr"), It.IsAny<string?>())).Returns(staleAdapter.Object);

        var convRouter = new Mock<IConversationRouter>();
        convRouter.Setup(r => r.MuteBindingAsync(It.IsAny<ConversationId>(), It.IsAny<BindingId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(manager.Object, convRouter.Object);

        var binding = new ChannelBinding
        {
            BindingId = bindingId,
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("conn-dead"),
            Mode = BindingMode.Interactive
        };

        await InvokeDeliverToBindingAsync(host, binding, "payload", "session-x", convId);

        convRouter.Verify(r => r.MuteBindingAsync(convId, bindingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Finding 2 (refactor): a generic (non-stale) send failure on one binding is swallowed and logged —
    /// it must NOT demote the binding to Muted and must not throw.
    /// </summary>
    [Fact]
    public async Task DeliverToBinding_OnGenericSendFailure_DoesNotMuteAndDoesNotThrow()
    {
        var convId = ConversationId.Create();
        var bindingId = BindingId.From("flaky-binding");

        var flakyAdapter = new Mock<IChannelAdapter>();
        flakyAdapter.SetupGet(a => a.ChannelType).Returns(ChannelKey.From("signalr"));
        flakyAdapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient send failure"));

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([flakyAdapter.Object]);
        manager.Setup(m => m.Get(ChannelKey.From("signalr"))).Returns(flakyAdapter.Object);
        manager.Setup(m => m.Get(ChannelKey.From("signalr"), It.IsAny<string?>())).Returns(flakyAdapter.Object);

        var convRouter = new Mock<IConversationRouter>();

        await using var host = CreateHost(manager.Object, convRouter.Object);

        var binding = new ChannelBinding
        {
            BindingId = bindingId,
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("conn-1"),
            Mode = BindingMode.Interactive
        };

        // Should not throw.
        await InvokeDeliverToBindingAsync(host, binding, "payload", "session-y", convId);

        convRouter.Verify(r => r.MuteBindingAsync(It.IsAny<ConversationId>(), It.IsAny<BindingId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── reflection helpers ───────────────────────────────────────────────────

    private static Task InvokeFanOutAsync(GatewayHost host, InboundMessage message, string sessionId, string? lastAssistantContent, ConversationId conversationId)
    {
        var method = typeof(GatewayHost).GetMethod("FanOutResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(host, [message, SessionId.From(sessionId), lastAssistantContent, conversationId, CancellationToken.None])!;
    }

    private static Task InvokeDeliverToBindingAsync(GatewayHost host, ChannelBinding binding, string content, string sessionId, ConversationId conversationId)
    {
        var method = typeof(GatewayHost).GetMethod("DeliverToBindingAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(host, [binding, content, SessionId.From(sessionId), conversationId, CancellationToken.None])!;
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static GatewayHost CreateHost(IChannelManager channelManager, IConversationRouter conversationRouter)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var messageRouter = new Mock<IMessageRouter>();
        return new GatewayHost(
            supervisor.Object,
            messageRouter.Object,
            new InMemorySessionStore(),
            Mock.Of<IActivityBroadcaster>(),
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: conversationRouter);
    }

    private static GatewayHost CreateHostWithStore(ISessionStore sessions, IChannelManager channelManager, IConversationRouter conversationRouter)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        var messageRouter = new Mock<IMessageRouter>();
        return new GatewayHost(
            supervisor.Object,
            messageRouter.Object,
            sessions,
            Mock.Of<IActivityBroadcaster>(),
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: conversationRouter);
    }

    private static InboundMessage CreateInbound(string content, string channelType, string channelAddress, string? sessionId = null)
        => new()
        {
            ChannelType = ChannelKey.From(channelType),
            SenderId = $"sender-{channelType}",
            Sender = CitizenId.Of(UserId.From($"sender-{channelType}")),
            ChannelAddress = ChannelAddress.From(channelAddress),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = new Dictionary<string, object?>()
        };

    private static TestHarness CreateHarness(string responseContent = "agent-response")
    {
        var messageRouter = new Mock<IMessageRouter>();
        messageRouter.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentName]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(AgentName));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("dynamic"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = responseContent });
        handle.Setup(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = responseContent });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new CountingSessionStore(new InMemorySessionStore());
        var conversations = new InMemoryConversationStore();
        var router = new DefaultConversationRouter(conversations, sessions, NullLogger<DefaultConversationRouter>.Instance);

        var signalr = CreateChannelRecorder("signalr");
        var telegram = CreateChannelRecorder("telegram");
        var tui = CreateChannelRecorder("tui");
        var manager = CreateChannelManager(signalr.Adapter.Object, telegram.Adapter.Object, tui.Adapter.Object);

        var host = new GatewayHost(
            supervisor.Object,
            messageRouter.Object,
            sessions,
            Mock.Of<IActivityBroadcaster>(),
            manager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: router);

        return new TestHarness(host, router, sessions, conversations, signalr, telegram, tui);
    }

    private static ChannelRecorder CreateChannelRecorder(string channelType)
    {
        var messages = new List<OutboundMessage>();
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(c => c.ChannelType).Returns(ChannelKey.From(channelType));
        adapter.SetupGet(c => c.DisplayName).Returns(channelType);
        adapter.SetupGet(c => c.SupportsStreaming).Returns(false);
        adapter.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => messages.Add(m))
            .Returns(Task.CompletedTask);
        adapter.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new ChannelRecorder(adapter, messages);
    }

    private static IChannelManager CreateChannelManager(params IChannelAdapter[] adapters)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns(adapters);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>()))
            .Returns((ChannelKey key) => adapters.FirstOrDefault(a => a.ChannelType == key));
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>()))
            .Returns((ChannelKey key, string? _) => adapters.FirstOrDefault(a => a.ChannelType == key));
        return manager.Object;
    }

    private sealed record ChannelRecorder(Mock<IChannelAdapter> Adapter, List<OutboundMessage> Messages);

    /// <summary>
    /// Decorator over <see cref="InMemorySessionStore"/> that counts <see cref="GetAsync"/> calls so a
    /// test can assert the fan-out path makes no redundant store read (#1394). All other operations
    /// forward to the inner store unchanged.
    /// </summary>
    private sealed class CountingSessionStore(InMemorySessionStore inner) : ISessionStore
    {
        private int _getCount;

        public int GetCallCount => _getCount;
        public void ResetGetCount() => _getCount = 0;

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getCount);
            return inner.GetAsync(sessionId, cancellationToken);
        }

        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => inner.GetOrCreateAsync(sessionId, agentId, cancellationToken);

        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
            => inner.SaveAsync(session, cancellationToken);

        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(sessionId, cancellationToken);

        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => inner.ArchiveAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => inner.ListAsync(agentId, cancellationToken);

        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
            => inner.ListByChannelAsync(agentId, channelType, cancellationToken);

        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversationId, AgentId? agentId = null, CancellationToken cancellationToken = default)
            => inner.ListByConversationAsync(conversationId, agentId, cancellationToken);

        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken = default)
            => inner.GetExistenceAsync(agentId, query, cancellationToken);
    }

    private sealed class TestHarness(
        GatewayHost host,
        DefaultConversationRouter router,
        CountingSessionStore sessions,
        InMemoryConversationStore conversations,
        ChannelRecorder signalr,
        ChannelRecorder telegram,
        ChannelRecorder tui)
    {
        public GatewayHost Host { get; } = host;
        public DefaultConversationRouter Router { get; } = router;
        public CountingSessionStore Sessions { get; } = sessions;
        public InMemoryConversationStore Conversations { get; } = conversations;
        public ChannelRecorder SignalR { get; } = signalr;
        public ChannelRecorder Telegram { get; } = telegram;
        public ChannelRecorder Tui { get; } = tui;

        public InboundMessage CreateMessage(string content, string channelType, string channelAddress, string? sessionId = null)
            => new()
            {
                ChannelType = ChannelKey.From(channelType),
                SenderId = $"sender-{channelType}",
                Sender = CitizenId.Of(UserId.From($"sender-{channelType}")),
                ChannelAddress = ChannelAddress.From(channelAddress),
                Content = content,
                RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
                Metadata = new Dictionary<string, object?>()
            };

        public void ClearOutbound()
        {
            SignalR.Messages.Clear();
            Telegram.Messages.Clear();
            Tui.Messages.Clear();
        }

        public async Task SeedSharedConversationAsync(params (string channelType, string channelAddress)[] bindings)
        {
            if (bindings.Length == 0) return;
            await Host.DispatchAsync(CreateMessage($"seed-{bindings[0].channelType}", bindings[0].channelType, bindings[0].channelAddress));
            var conversations = await Conversations.ListAsync(AgentId.From(AgentName));
            var conversation = conversations.Last();
            foreach (var (channelType, channelAddress) in bindings.Skip(1))
            {
                conversation.ChannelBindings.Add(new ChannelBinding
                {
                    ChannelType = ChannelKey.From(channelType),
                    ChannelAddress = ChannelAddress.From(channelAddress),
                    Mode = BindingMode.Interactive
                });
            }
            await Conversations.SaveAsync(conversation);
        }
    }
}
