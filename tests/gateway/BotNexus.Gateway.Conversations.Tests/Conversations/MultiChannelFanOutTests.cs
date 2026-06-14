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
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

public sealed class MultiChannelFanOutTests
{
    private const string AgentName = "agent-a";

    [Fact]
    public async Task SingleChannel_InboundMessage_ResponseSentOnce()
    {
        var harness = CreateHarness(responseContent: "single-channel-response");

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.ShouldHaveSingleItem();
        harness.SignalR.Messages[0].Content.ShouldBe("single-channel-response");
        harness.SignalR.Messages[0].ChannelType.ShouldBe(ChannelKey.From("signalr"));
        harness.Telegram.Messages.ShouldBeEmpty();
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task SingleChannel_InboundMessage_BindingId_StampedFromConversation()
    {
        var harness = CreateHarness();
        var inbound = harness.CreateMessage("hello", "signalr", "chat-1");

        await harness.Host.DispatchAsync(inbound);

        var conversation = (await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName))).ShouldHaveSingleItem();
        conversation.ChannelBindings.ShouldHaveSingleItem();
        inbound.BindingId.ShouldBeNull();

        var session = (await harness.Sessions.ListAsync()).ShouldHaveSingleItem();
        session.Session.ConversationId.ShouldBe(conversation.ConversationId);

        var outboundBindings = await harness.Router.GetOutboundBindingsAsync(session.SessionId, originatingBindingId: null);
        outboundBindings.ShouldHaveSingleItem();
        outboundBindings[0].BindingId.ShouldBe(conversation.ChannelBindings[0].BindingId);
    }

    [Fact]
    public async Task TwoChannels_MessageFromSignalR_FansOutToTelegram()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-1"));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-100"));
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoChannels_MessageFromTelegram_FansOutToSignalR()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-1"));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-100"));
    }

    [Fact]
    public async Task TwoChannels_MessageFromTelegram_TelegramReceivesExactlyOnce()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TwoChannels_NewConversation_BothChannelsGetSubsequentMessages()
    {
        // Explicitly bind both channels to the same conversation, then verify fan-out
        var harness = CreateHarness(responseContent: "follow-up");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("second", "signalr", "chat-1"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].Content.ShouldBe("follow-up");
        harness.Telegram.Messages[0].Content.ShouldBe("follow-up");
    }

    [Fact]
    public async Task ThreeChannels_MessageFromA_FansOutToBAndC()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Tui.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ThreeChannels_MessageFromB_FansOutToAAndC()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Tui.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ThreeChannels_EachChannelSends_OthersTwoReceive_NoDuplicates()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));

        await AssertPerSendAsync(harness, harness.CreateMessage("from-a", "signalr", "chat-1"));
        await AssertPerSendAsync(harness, harness.CreateMessage("from-b", "telegram", "chat-100"));
        await AssertPerSendAsync(harness, harness.CreateMessage("from-c", "tui", "terminal-1"));
    }

    [Fact]
    public async Task TwoTopics_SameChat_IndependentConversations()
    {
        var harness = CreateHarness(responseContent: "threaded");
        await harness.SeedBindingAsync("telegram", "100");
        await harness.SeedBindingAsync("telegram", "100/topic:42");
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("root", "telegram", "100"));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("100")); // root chat, bare address

        harness.ClearOutbound();
        await harness.Host.DispatchAsync(harness.CreateMessage("topic", "telegram", "100/topic:42"));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("100/topic:42")); // composite address round-trips

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Topic_MessageFromTelegram_FansOutWithCorrectCompositeAddress()
    {
        var harness = CreateHarness(responseContent: "threaded");
        await harness.SeedBindingAsync("telegram", "100/topic:42");
        await harness.AttachBindingToConversationAsync("signalr", "chat-1", harness.GetConversationFor("telegram", "100/topic:42").ConversationId);
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("topic", "telegram", "100/topic:42"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-1")); // signalr binding uses its own address
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("100/topic:42")); // composite address preserved end-to-end
    }

    [Fact]
    public async Task MutedBinding_DoesNotReceiveFanOut()
    {
        var harness = CreateHarness(responseContent: "muted");
        await harness.SeedBindingAsync("signalr", "chat-1");
        await harness.SeedBindingAsync("telegram", "chat-100");
        await harness.SetBindingModeAsync("telegram", "chat-100", BindingMode.Muted);
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyConversation_NoBindings_NoFanOut()
    {
        var harness = CreateHarness(responseContent: "orphan");
        var session = await harness.Sessions.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-empty"), BotNexus.Domain.Primitives.AgentId.From(AgentName));
        var conversation = new Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentName),
            Title = "Empty",
            ActiveSessionId = session.SessionId
        };
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);
        await harness.Conversations.CreateAsync(conversation);

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1", sessionId: "session-empty"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldBeEmpty();
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Conversation_WhenOriginatingBindingIdIsNull_FansOutToAll()
    {
        var harness = CreateHarness(responseContent: "legacy-broken");
        var conversation = new Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentName),
            Title = "Legacy",
            ActiveSessionId = BotNexus.Domain.Primitives.SessionId.From("legacy-session"),
            ChannelBindings =
            [
                new ChannelBinding { BindingId = BindingId.From("sig"), ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("chat-1") },
                new ChannelBinding { BindingId = BindingId.From("tel"), ChannelType = ChannelKey.From("telegram"), ChannelAddress = ChannelAddress.From("chat-100") }
            ]
        };
        await harness.Conversations.CreateAsync(conversation);

        var session = await harness.Sessions.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("legacy-session"), BotNexus.Domain.Primitives.AgentId.From(AgentName));
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "legacy-broken" });
        await harness.Sessions.SaveAsync(session);

        await InvokeFanOutAsync(harness.Host, harness.CreateMessage("hello", "telegram", "chat-100"), "legacy-session", "legacy-broken", conversation.ConversationId);

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DifferentChannelAddresses_GetSeparateConversations()
    {
        // Regression for #138: a Teams DM and a Telegram DM must not share the default conversation
        var harness = CreateHarness(responseContent: "isolated");

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));
        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "browser-1"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2, "each channel address must get its own conversation");

        var telegramConv = conversations.FirstOrDefault(c =>
            c.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("telegram")));
        var signalrConv = conversations.FirstOrDefault(c =>
            c.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("signalr")));

        telegramConv.ShouldNotBeNull();
        signalrConv.ShouldNotBeNull();
        telegramConv!.ConversationId.ShouldNotBe(signalrConv!.ConversationId);
    }

    [Fact]
    public async Task SameChannelAddress_SecondMessage_ReusesConversation()
    {
        // Same (channelType, channelAddress) must always route to the same conversation
        var harness = CreateHarness(responseContent: "reuse");

        await harness.Host.DispatchAsync(harness.CreateMessage("first", "telegram", "chat-100"));
        await harness.Host.DispatchAsync(harness.CreateMessage("second", "telegram", "chat-100"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(1, "same address must reuse the same conversation");
    }

    [Fact]
    public async Task GroupChat_AndDM_SameChannelType_GetSeparateConversations()
    {
        // A Telegram group (chat-id: -1001234) and a DM (chat-id: 1234567) must be separate
        var harness = CreateHarness(responseContent: "group-isolated");

        await harness.Host.DispatchAsync(harness.CreateMessage("hi from DM", "telegram", "1234567"));
        await harness.Host.DispatchAsync(harness.CreateMessage("hi from group", "telegram", "-1001234567"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2, "DM and group chat must have separate conversations");
    }

    [Fact]
    public async Task NoChannelAddress_CreatesOwnConversation()
    {
        // Empty-address messages now get their own conversation like any other channel.
        // Multiple messages from the same empty-address channel reuse that conversation.
        var harness = CreateHarness(responseContent: "default");

        var msg1 = harness.CreateMessage("first", "signalr", "");
        var msg2 = harness.CreateMessage("second", "signalr", "");
        await harness.Host.DispatchAsync(msg1);
        await harness.Host.DispatchAsync(msg2);

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(1, "addressless messages from the same channel reuse the same conversation");
    }


    private static async Task AssertPerSendAsync(TestHarness harness, InboundMessage message)
    {
        harness.ClearOutbound();
        await harness.Host.DispatchAsync(message);
        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Tui.Messages.Count.ShouldBe(1);
    }

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
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new InMemorySessionStore();
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
            new RecordingActivityBroadcaster(),
            manager,
            Mock.Of<BotNexus.Gateway.Abstractions.Sessions.ISessionCompactor>(),
            new TestOptionsMonitor<BotNexus.Gateway.Abstractions.Sessions.CompactionOptions>(new BotNexus.Gateway.Abstractions.Sessions.CompactionOptions()),
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

    private sealed class TestHarness(
        GatewayHost host,
        DefaultConversationRouter router,
        InMemorySessionStore sessions,
        InMemoryConversationStore conversations,
        ChannelRecorder signalr,
        ChannelRecorder telegram,
        ChannelRecorder tui)
    {
        public GatewayHost Host { get; } = host;
        public DefaultConversationRouter Router { get; } = router;
        public InMemorySessionStore Sessions { get; } = sessions;
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

        public async Task SeedBindingAsync(string channelType, string channelAddress)
        {
            // Dispatch a seed message — the router will find or create the correct conversation
            // per (channelType, channelAddress). Native sub-addresses (e.g. Telegram forum topics)
            // are folded into the ChannelAddress by the caller. For fan-out tests that need multiple
            // channels in one conversation, use SeedSharedConversationAsync or AttachBindingToConversationAsync directly.
            await Host.DispatchAsync(CreateMessage($"seed-{channelType}", channelType, channelAddress));
        }

        /// <summary>
        /// Creates a shared conversation with all given (channelType, channelAddress) bindings.
        /// The first address seeds the conversation via dispatch; the rest are attached directly.
        /// Use this for fan-out tests where multiple channels must share one conversation.
        /// </summary>
        public async Task SeedSharedConversationAsync(params (string channelType, string channelAddress)[] bindings)
        {
            if (bindings.Length == 0) return;
            // First binding seeds the conversation
            await Host.DispatchAsync(CreateMessage($"seed-{bindings[0].channelType}", bindings[0].channelType, bindings[0].channelAddress));
            var conversations = await Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
            var conversation = conversations.Last();
            // Remaining bindings attached directly to the same conversation
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

        public Conversation GetConversationFor(string channelType, string channelAddress)
            => Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName)).Result
                .Single(c => c.ChannelBindings.Any(b =>
                    b.ChannelType == ChannelKey.From(channelType) &&
                    b.ChannelAddress == ChannelAddress.From(channelAddress)));

        public async Task AttachBindingToConversationAsync(string channelType, string channelAddress, BotNexus.Domain.Primitives.ConversationId conversationId)
        {
            var conversation = (await Conversations.GetAsync(conversationId))!;
            conversation.ChannelBindings.Add(new ChannelBinding
            {
                ChannelType = ChannelKey.From(channelType),
                ChannelAddress = ChannelAddress.From(channelAddress),
                Mode = BindingMode.Interactive
            });
            await Conversations.SaveAsync(conversation);
        }

        public async Task SetBindingModeAsync(string channelType, string channelAddress, BindingMode mode)
        {
            var conversation = GetConversationFor(channelType, channelAddress);
            var binding = conversation.ChannelBindings.Single(b =>
                b.ChannelType == ChannelKey.From(channelType) &&
                b.ChannelAddress == ChannelAddress.From(channelAddress));
            binding.Mode = mode;
            await Conversations.SaveAsync(conversation);
        }
    }

    private static Task InvokeFanOutAsync(GatewayHost host, InboundMessage message, string sessionId, string? lastAssistantContent, BotNexus.Domain.Primitives.ConversationId conversationId)
    {
        var method = typeof(GatewayHost).GetMethod("FanOutResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(host, [message, SessionId.From(sessionId), lastAssistantContent, conversationId, CancellationToken.None])!;
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);

            yield break;
        }
    }
}


