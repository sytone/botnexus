using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed partial class GatewayHostTests
{
    [Fact]
    public async Task DispatchAsync_RoutesMessageToResolvedAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response" && m.SessionId == "session-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        session.History.Select(e => $"{e.Role}:{e.Content}").ToList().ShouldBe(new[] { "user:hello", "assistant:agent-response" });
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.MessageReceived);
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentProcessing);
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentCompleted);
    }

    [Fact]
    public async Task DispatchAsync_WithStreaming_RecordsAccumulatedHistory()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world" }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: true);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        session.History.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "hello world");
        channel.Verify(c => c.SendStreamDeltaAsync(
            It.Is<ChannelStreamTarget>(t =>
                t.SessionId == SessionId.From("session-1") &&
                t.ChannelAddress == ChannelAddress.From("conv-1")),
            "hello ",
            It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.SendStreamDeltaAsync(
            It.Is<ChannelStreamTarget>(t =>
                t.SessionId == SessionId.From("session-1") &&
                t.ChannelAddress == ChannelAddress.From("conv-1")),
            "world",
            It.IsAny<CancellationToken>()),
            Times.Once);
        // Write-ahead (user msg + sentinel) + ProcessAndSaveAsync final = at least 3 saves
        sessions.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }

    // #739/#1695 regression: a STREAMING turn (portal/SignalR is the dominant path) on a
    // still-default-titled conversation must trigger auto-title generation. The service-level
    // ConversationAutoTitleServiceTests prove the titler works in isolation; this asserts the
    // GatewayHost streaming finalizer actually wires it, which is where the live no-fire lived:
    // 82 portal conversations sat on "New conversation" despite real exchanges because nothing
    // exercised this path end-to-end.
    [Fact]
    public async Task DispatchAsync_Streaming_DefaultTitledConversation_TriggersAutoTitle()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var agentId = BotNexus.Domain.Primitives.AgentId.From("agent-a");
        var sessionId = BotNexus.Domain.Primitives.SessionId.From("web:addr-1:agent-a");
        var convId = BotNexus.Domain.Primitives.ConversationId.From("c_autotitlestream1");

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "the answer " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "is 42" }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(sessionId, agentId);

        // Conversation row exists in the store with the default title (#739 gate).
        var conversationStore = new InMemoryConversationStore();
        await conversationStore.SaveAsync(
            new BotNexus.Gateway.Abstractions.Models.Conversation
            {
                ConversationId = convId,
                AgentId = agentId,
                Title = ConversationAutoTitleService.DefaultTitle,
            },
            CancellationToken.None);

        // Dispatcher stamps the session with the resolved conversation id so the auto-title
        // guard's IsInitialized() check passes on the streaming path.
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(convId, sessionId, false, false)));

        var channel = CreateChannelAdapter("web", supportsStreaming: true);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            conversationDispatcher: conversationDispatcher.Object,
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Meaning Of Life"));

        await host.DispatchAsync(CreateMessage("what is the answer", channelType: "web", conversationId: "addr-1"));

        // TriggerBestEffort is fire-and-forget (Task.Run); poll for the persisted title change.
        string? finalTitle = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
            finalTitle = conv?.Title;
            if (!ConversationAutoTitleService.IsDefaultTitle(finalTitle))
                break;
            await Task.Delay(100);
        }

        finalTitle.ShouldBe("Meaning Of Life");
    }

    // Minimal LlmClient backed by a fake provider returning a fixed title, mirroring the seam in
    // ConversationAutoTitleServiceTests so the GatewayHost-level auto-title wiring is exercisable.
    private static LlmClient CreateFakeTitleLlmClient(string responseText)
    {
        var modelRegistry = new ModelRegistry();
        var fakeModel = new LlmModel(
            Id: "fake-model",
            Name: "fake-model",
            Api: "fake-api",
            Provider: "fake",
            BaseUrl: "https://fake.example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 512);
        modelRegistry.Register("fake", fakeModel);

        var providerRegistry = new ApiProviderRegistry();
        providerRegistry.Register(new FakeTitleApiProvider(responseText));
        return new LlmClient(providerRegistry, modelRegistry);
    }

    private sealed class FakeTitleApiProvider : IApiProvider
    {
        private readonly string _responseText;
        public FakeTitleApiProvider(string responseText) => _responseText = responseText;
        public string Api => "fake-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new NotImplementedException("FakeTitleApiProvider only supports StreamSimple");

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var msg = new AssistantMessage(
                Content: [new TextContent(_responseText)],
                Api: "fake-api",
                Provider: "fake",
                ModelId: "fake-model",
                Usage: new Usage(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var stream = new LlmStream();
            stream.Push(new DoneEvent(StopReason.Stop, msg));
            return stream;
        }
    }

    [Fact]
    public async Task DispatchAsync_WithNoRoute_ReturnsGracefully()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new Mock<ISessionStore>();
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        sessions.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithPendingAskUserRequest_ConsumesInboundAsToolResponse()
    {
        var conversationId = BotNexus.Domain.Primitives.ConversationId.From("conversation-ask");
        var sessionId = BotNexus.Domain.Primitives.SessionId.From("session-ask");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(sessionId, BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        session.Session.ConversationId = conversationId;
        await sessions.SaveAsync(session);

        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var supervisor = new Mock<IAgentSupervisor>();

        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(dispatcher => dispatcher.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(conversationId, sessionId, false, false)));

        var askUserRegistry = new AskUserResponseRegistry();
        var (requestId, pendingTask) = askUserRegistry.Register(conversationId, TimeSpan.FromMinutes(1));
        var interceptor = new PendingAskUserInterceptor(askUserRegistry);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object,
            pendingAskUserInterceptor: interceptor);

        await host.DispatchAsync(CreateMessage("Deploy to staging", sessionId: sessionId.Value, conversationId: conversationId.Value));

        var response = await pendingTask;
        response.RequestId.ShouldBe(requestId);
        response.FreeFormText.ShouldBe("Deploy to staging");
        supervisor.Verify(s => s.GetOrCreateAsync(
            It.IsAny<BotNexus.Domain.Primitives.AgentId>(),
            It.IsAny<BotNexus.Domain.Primitives.SessionId>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_BroadcastsActivityEvents()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.Select(a => a.Type).ToList().ShouldBe(new[] {
            GatewayActivityType.MessageReceived,
            GatewayActivityType.AgentProcessing,
            GatewayActivityType.AgentCompleted });
    }

    [Fact]
    public async Task DispatchAsync_WithConcurrentMessages_ProcessesAllSafely()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("dynamic"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string msg, CancellationToken _) => new AgentResponse { Content = $"echo:{msg}" });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentUserMessage msg, CancellationToken _) => new AgentResponse { Content = $"echo:{msg.Content}" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, activity, CreateChannelManager(channel.Object));

        var dispatches = Enumerable.Range(1, 20)
            .Select(i => host.DispatchAsync(CreateMessage($"m{i}", conversationId: $"conv-{i}")));
        await Task.WhenAll(dispatches);

        var allSessions = await sessions.ListAsync();
        allSessions.Count().ShouldBe(20);
        allSessions.ShouldAllBe(s => s.History.Count == 2);
        activity.Activities.Count(a => a.Type == GatewayActivityType.Error).ShouldBe(0);
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentThrows_PublishesErrorActivity()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.Error && a.Message == "boom");
        // Write-ahead saves the user message + crash sentinel before the LLM call (#363),
        // so SaveAsync is called even when the agent throws.
        sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.AtLeast(1));
        // Crash sentinel survives in history because the turn did not complete cleanly.
        session.History.ShouldContain(e => e.IsCrashSentinel, "crash sentinel must remain when agent throws");
    }

    [Fact]
    public async Task DispatchAsync_WithNewConversation_CreatesDerivedSession()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "web:conv-1:agent-a", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") });
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello"));

        sessions.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("web:conv-1:agent-a"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SetsSessionChannelTypeFromInboundMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "cron:job-1:run-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("cron:job-1:run-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("run", channelType: "cron", conversationId: "cron:job-1:run-1", sessionId: "cron:job-1:run-1"));

        session.ChannelType.ShouldBe(ChannelKey.From("cron"));
    }

    [Fact]
    public async Task DispatchAsync_ExpiredSession_ReactivatesAndProcesses()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        session.Status = SessionStatus.Expired;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await sessions.SaveAsync(session);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync(SessionId.From("session-1"));
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(SessionStatus.Active);
        reloaded.ExpiresAt.ShouldBeNull();
        reloaded.History.Select(e => $"{e.Role}:{e.Content}").ToList().ShouldBe(new[] { "user:hello", "assistant:agent-response" });
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response" && m.SessionId == "session-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SealedSession_ReactivatesAndProcesses()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        session.Status = SessionStatus.Sealed;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync(SessionId.From("session-1"));
        reloaded!.Status.ShouldBe(SessionStatus.Active);
        supervisor.Verify(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SuspendedSession_RejectsMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        session.Status = SessionStatus.Suspended;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredSession_ClearsExpiresAt()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        session.Status = SessionStatus.Expired;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        var reloaded = await sessions.GetAsync(SessionId.From("session-1"));
        reloaded.ShouldNotBeNull();
        reloaded!.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithSuspendedSession_RejectsNewMessages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"), Status = SessionStatus.Suspended };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSessionHasNoHistory_StopsExistingHandleBeforePrompt()
    {
        // Phase 3d invariant (#537): system prompt is initialised exactly when
        // session.History.Count == 0. A brand-new session has no entries, so we
        // expect the supervisor to be told to drop any stale handle so the next
        // GetOrCreateAsync rebuilds the prompt from workspace files.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.StopAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSessionHasHistory_DoesNotStopExistingHandle()
    {
        // Phase 3d invariant (#537): a session with any history entries has already
        // had its system prompt initialised on a prior turn, so the supervisor handle
        // must be reused — re-stopping would force the isolation strategy to rebuild
        // the prompt from disk and clobber any in-memory continuation state.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        // Pre-existing turn from a previous dispatch — proves the session is not fresh.
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "earlier turn",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WhenCompactedSessionHasMarkedHistoryAndSummary_DoesNotStopExistingHandle()
    {
        // Phase 3a + Phase 3d interaction (#531 + #537): after in-session compaction,
        // older turns are marked IsHistory=true (not deleted) and a summary entry is
        // inserted at the boundary. The session is no longer "fresh" — History.Count > 0 —
        // so the prompt-init force-stop must not fire, even though the LLM-visible
        // projection only sees the summary + preserved tail.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        // Reproduce the canonical post-compaction shape: [historical..., summary, preserved tail].
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "summarised user turn",
            IsHistory = true,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "summarised assistant turn",
            IsHistory = true,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-9)
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "compaction summary",
            IsCompactionSummary = true,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-8)
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "preserved tail user turn",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager());

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        supervisor.Verify(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithSteerControl_RoutesToSteerHandler()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") });
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager());

        await host.DispatchAsync(CreateMessage("nudge", sessionId: "session-1", metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // #1903 gap #2: HandleSteeringAsync adds a User entry but historically never invoked titling,
    // so a portal follow-up (Steer) on a still-default-titled conversation could never title. This
    // drives the steer path end-to-end and asserts the persisted title flips off the default,
    // proving the titling hook is now wired into the steering path.
    [Fact]
    public async Task DispatchAsync_WithSteerControl_DefaultTitledConversation_TriggersAutoTitle()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var agentId = BotNexus.Domain.Primitives.AgentId.From("agent-a");
        var sessionId = BotNexus.Domain.Primitives.SessionId.From("session-steer-1");
        var convId = BotNexus.Domain.Primitives.ConversationId.From("c_steerautotitle1");

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(agentId, sessionId))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-steer-1",
                AgentId = agentId,
                SessionId = sessionId,
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var seeded = await sessions.GetOrCreateAsync(sessionId, agentId);
        // Prior assistant turn already present so the steer's new user entry completes an exchange.
        seeded.Session.ConversationId = convId;
        seeded.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "earlier assistant answer" });
        await sessions.SaveAsync(seeded);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.SaveAsync(
            new BotNexus.Gateway.Abstractions.Models.Conversation
            {
                ConversationId = convId,
                AgentId = agentId,
                Title = ConversationAutoTitleService.DefaultTitle,
            },
            CancellationToken.None);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationStore: conversationStore,
            llmClient: CreateFakeTitleLlmClient("Steered Chat Title"));

        await host.DispatchAsync(CreateMessage(
            "follow up question",
            sessionId: sessionId.Value,
            metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        handle.Verify(h => h.SteerAsync("follow up question", It.IsAny<CancellationToken>()), Times.Once);

        // TriggerBestEffort is fire-and-forget; poll for the persisted title change.
        string? finalTitle = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conv = await conversationStore.GetAsync(convId, CancellationToken.None);
            finalTitle = conv?.Title;
            if (!ConversationAutoTitleService.IsDefaultTitle(finalTitle))
                break;
            await Task.Delay(100);
        }

        finalTitle.ShouldBe("Steered Chat Title");
    }

    [Fact]
    public async Task DispatchAsync_WithSteerControl_WhenAgentNotRunning_DoesNotFallThroughToNormalProcessing()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1")))
            .Returns(new AgentInstance
            {
                InstanceId = "agent-a::session-1",
                AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
                IsolationStrategy = "in-process"
            });
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("nudge", sessionId: "session-1", metadata: new Dictionary<string, object?> { ["control"] = "steer" }));

        // Steering IS called even though agent is not running (fix: no IsRunning gate)
        handle.Verify(h => h.SteerAsync("nudge", It.IsAny<CancellationToken>()), Times.Once);
        // Steering still does NOT fall through to PromptAsync
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "nudge"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WithOriginatingBinding_PreservesSourceMetadataOnOutboundMessage()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-target", "reply");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-target"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var sourceBinding = new ChannelBinding
        {
            BindingId = BotNexus.Domain.Primitives.BindingId.From("binding-source"),
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("chat-42/topic:42"),
            DisplayPrefix = "[topic] ",
            Mode = BindingMode.Interactive
        };
        var conversation = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-target"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelBindings = [sourceBinding]
        };
        var conversationRouter = new Mock<IConversationRouter>();
        conversationRouter.Setup(r => r.GetOutboundBindingsAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-target"),
                sourceBinding.BindingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<InboundMessageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                new ChannelSource(
                    sourceBinding.ChannelType,
                    sourceBinding.ChannelAddress,
                    context.Source.SenderId,
                    sourceBinding.BindingId,
                    sourceBinding.DisplayPrefix),
                new ConversationSessionResolution(
                    conversation.ConversationId,
                    BotNexus.Domain.Primitives.SessionId.From("session-target"),
                    false,
                    false,
                    sourceBinding.BindingId,
                    sourceBinding.DisplayPrefix)));

        var channel = CreateChannelAdapter("telegram", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            conversationDispatcher: conversationDispatcher.Object,
            conversationRouter: conversationRouter.Object);

        await host.DispatchAsync(CreateMessage("hello", channelType: "telegram", conversationId: "chat-42/topic:42"));

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m =>
                    m.ChannelType == BotNexus.Domain.Primitives.ChannelKey.From("telegram") &&
                    m.SessionId == "session-target" &&
                    m.ChannelAddress == ChannelAddress.From("chat-42/topic:42") &&
                    m.BindingId == sourceBinding.BindingId &&
                    m.DisplayPrefix == "[topic] "),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_InternalWakeMessage_WithSessionId_BypassesConversationDispatcher()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "parent-session", "wake-ack");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var parentSession = await sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        parentSession.Session.ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-parent");
        await sessions.SaveAsync(parentSession);

        var conversationDispatcher = new Mock<IConversationDispatcher>();

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("internal"),
            SenderId = "subagent:test",
            Sender = CitizenId.Of(BotNexus.Domain.Primitives.AgentId.From("subagent:test")),
            ChannelAddress = ChannelAddress.From("parent-session"),
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                RequestedSessionId: BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                RequestedConversationId: null),
            Content = "subagent completion wake-up",
            Metadata = new Dictionary<string, object?> { ["messageType"] = "subagent-completion" }
        });

        conversationDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        supervisor.Verify(
            s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var reloaded = await sessions.GetAsync(BotNexus.Domain.Primitives.SessionId.From("parent-session"));
        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(2);
        reloaded.Session.ConversationId.ShouldBe(BotNexus.Domain.Primitives.ConversationId.From("conv-parent"));
    }

    [Fact]
    public async Task DispatchAsync_InternalWakeMessage_WithoutSessionId_UsesChannelAddressAndBypassesConversationDispatcher()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "parent-session", "wake-ack");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var parentSession = await sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            BotNexus.Domain.Primitives.AgentId.From("agent-a"));
        parentSession.Session.ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-parent");
        await sessions.SaveAsync(parentSession);

        var conversationDispatcher = new Mock<IConversationDispatcher>();

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("internal"),
            SenderId = "subagent:test",
            Sender = CitizenId.Of(BotNexus.Domain.Primitives.AgentId.From("subagent:test")),
            ChannelAddress = ChannelAddress.From("parent-session"),
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                RequestedSessionId: null,
                RequestedConversationId: null),
            Content = "subagent completion wake-up",
            Metadata = new Dictionary<string, object?> { ["messageType"] = "subagent-completion" }
        });

        conversationDispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        supervisor.Verify(
            s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("parent-session"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_TriggersWhenAboveThreshold()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(true);
        compactor.Setup(c => c.CompactAsync(session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult { Summary = string.Empty });

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        compactor.Verify(c => c.CompactAsync(session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_DoesNotTriggerBelowThreshold()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "ok");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(false);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        compactor.Verify(c => c.CompactAsync(session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GatewayHost_AutoCompaction_FailureDoesNotBlockProcessing()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-1"), BotNexus.Domain.Primitives.AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var compactor = new Mock<ISessionCompactor>();
        compactor.Setup(c => c.ShouldCompact(session.Session, It.IsAny<CompactionOptions>())).Returns(true);
        compactor.Setup(c => c.CompactAsync(session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compaction failed"));
        var channel = CreateChannelAdapter("web", supportsStreaming: false);

        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            compactor: compactor.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content == "agent-response"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSessionQueueIsFull_ReturnsBusyResponse()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(250);
                return new AgentResponse { Content = "ok" };
            });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(250);
                return new AgentResponse { Content = "ok" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 1);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        // Yield until the first dispatch is actively running (PromptAsync takes 250ms, so both
        // are in-flight by the time we need to verify busy rejection).
        await Task.WhenAny(first, second, Task.Delay(100));
        await host.DispatchAsync(CreateMessage("three", sessionId: "session-1"));
        await Task.WhenAll(first, second);

        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("busy", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DispatchAsync_WithSameSessionMessages_ProcessesQueueSequentially()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var inFlight = 0;
        var maxInFlight = 0;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (message, _) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, current));
                await Task.Delay(75);
                Interlocked.Decrement(ref inFlight);
                return new AgentResponse { Content = $"echo:{message}" };
            });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns<AgentUserMessage, CancellationToken>(async (message, _) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, current));
                await Task.Delay(75);
                Interlocked.Decrement(ref inFlight);
                return new AgentResponse { Content = $"echo:{message.Content}" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            sessionQueueCapacity: 8);

        var first = host.DispatchAsync(CreateMessage("one", sessionId: "session-1"));
        var second = host.DispatchAsync(CreateMessage("two", sessionId: "session-1"));
        await Task.WhenAll(first, second);

        maxInFlight.ShouldBe(1);
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "one"), It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.Is<AgentUserMessage>(m => m.Content == "two"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithSuspendedSession_RejectsMessagesWithoutPromptingAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"));
        session.Status = SessionStatus.Suspended;
        await sessions.SaveAsync(session);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));
        await host.DispatchAsync(CreateMessage("again", sessionId: "session-1"));

        supervisor.Verify(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.SendAsync(
                It.Is<OutboundMessage>(m => m.Content.Contains("suspended", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenStarted_ManagesChannelLifecycleAndShutdown()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.StopAllAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var channelStartedTcs = new TaskCompletionSource();
        var firstChannel = CreateChannelAdapter("web", supportsStreaming: false);
        firstChannel.Setup(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()))
            .Callback(() => channelStartedTcs.TrySetResult())
            .Returns(Task.CompletedTask);
        firstChannel.Setup(c => c.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var secondChannel = CreateChannelAdapter("telegram", supportsStreaming: false);
        secondChannel.Setup(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("start failed"));
        secondChannel.Setup(c => c.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([firstChannel.Object, secondChannel.Object]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            channelType.Equals(ChannelKey.From("web"))
                ? firstChannel.Object
                : channelType.Equals(ChannelKey.From("telegram"))
                    ? secondChannel.Object
                    : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            channelType.Equals(ChannelKey.From("web"))
                ? firstChannel.Object
                : channelType.Equals(ChannelKey.From("telegram"))
                    ? secondChannel.Object
                    : null);

        await using var host = new GatewayHost(
            supervisor.Object,
            Mock.Of<IMessageRouter>(),
            new InMemorySessionStore(),
            new RecordingActivityBroadcaster(),
            manager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        // Wait until the channel adapter's StartAsync has been invoked (event-driven, not time-based).
        await channelStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync(CancellationToken.None);

        firstChannel.Verify(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.StartAsync(It.IsAny<IChannelDispatcher>(), It.IsAny<CancellationToken>()), Times.Once);
        firstChannel.Verify(c => c.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        supervisor.Verify(s => s.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithBinaryImageContentPart_ForwardsImageToAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var imageData = new byte[] { 0xFF, 0xD8, 0xFF }; // minimal JPEG header
        var message = CreateMessage("look at this", sessionId: "session-1") with
        {
            ContentParts = [new BinaryContentPart { MimeType = "image/jpeg", Data = imageData }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images.ShouldHaveSingleItem();
        capturedMessage.Images![0].Value.ShouldBe($"data:image/jpeg;base64,{Convert.ToBase64String(imageData)}");
    }

    [Fact]
    public async Task DispatchAsync_WithReferenceImageContentPart_ForwardsUrlToAgent()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        const string imageUrl = "https://example.com/photo.jpg";
        var message = CreateMessage("describe this", sessionId: "session-1") with
        {
            ContentParts = [new ReferenceContentPart { MimeType = "image/jpeg", Uri = imageUrl }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images.ShouldHaveSingleItem();
        capturedMessage.Images![0].Value.ShouldBe(imageUrl);
    }

    [Fact]
    public async Task DispatchAsync_WithNoImageContentParts_UsesStringOverload()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = CreatePromptHandle("agent-a", "session-1", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("plain text", sessionId: "session-1"));

        // GatewayHost always calls the UserMessage overload; string overload is never used directly
        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithNonImageBinaryPart_ProducesNoImagesInUserMessage()
    {
        // A BinaryContentPart with audio/wav must NOT be converted to an image
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var message = CreateMessage("transcribe this", sessionId: "session-1") with
        {
            ContentParts = [new BinaryContentPart { MimeType = "audio/wav", Data = [0x52, 0x49, 0x46, 0x46] }]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithMixedImageAndNonImageParts_ForwardsOnlyImageParts()
    {
        // Only image/* MIME types should be forwarded; non-image parts are silently excluded
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var imageData = new byte[] { 0xFF, 0xD8, 0xFF };
        var message = CreateMessage("analyze this", sessionId: "session-1") with
        {
            ContentParts =
            [
                new BinaryContentPart { MimeType = "image/png", Data = imageData },
                new BinaryContentPart { MimeType = "audio/wav", Data = [0x52, 0x49, 0x46, 0x46] },
                new ReferenceContentPart { MimeType = "application/pdf", Uri = "https://example.com/doc.pdf" }
            ]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images!.Count.ShouldBe(1);
        capturedMessage.Images[0].Value.ShouldBe($"data:image/png;base64,{Convert.ToBase64String(imageData)}");
    }

    [Fact]
    public async Task DispatchAsync_WithMultipleImageParts_ForwardsAllImages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var jpeg1 = new byte[] { 0xFF, 0xD8, 0xFF };
        var png1 = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        const string imageUrl = "https://cdn.example.com/img3.webp";

        var message = CreateMessage("compare these", sessionId: "session-1") with
        {
            ContentParts =
            [
                new BinaryContentPart { MimeType = "image/jpeg", Data = jpeg1 },
                new BinaryContentPart { MimeType = "image/png", Data = png1 },
                new ReferenceContentPart { MimeType = "image/webp", Uri = imageUrl }
            ]
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldNotBeNull();
        capturedMessage.Images!.Count.ShouldBe(3);
        capturedMessage.Images[0].Value.ShouldBe($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg1)}");
        capturedMessage.Images[1].Value.ShouldBe($"data:image/png;base64,{Convert.ToBase64String(png1)}");
        capturedMessage.Images[2].Value.ShouldBe(imageUrl);
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyContentParts_ProducesNoImages()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        AgentUserMessage? capturedMessage = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => capturedMessage = m)
            .ReturnsAsync(new AgentResponse { Content = "ok" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        var message = CreateMessage("plain text", sessionId: "session-1") with
        {
            ContentParts = []
        };

        await host.DispatchAsync(message);

        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Images.ShouldBeNull();
    }

    private static Mock<IAgentHandle> CreatePromptHandle(string agentId, string sessionId, string content)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From(agentId));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From(sessionId));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = content });
        return handle;
    }

    private static Mock<IChannelAdapter> CreateChannelAdapter(string channelType, bool supportsStreaming)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(channelType);
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
            adapter is not null && channelType.Equals(adapter.ChannelType)
                ? adapter
                : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            adapter is not null && channelType.Equals(adapter.ChannelType)
                ? adapter
                : null);
        return manager.Object;
    }

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager,
        int sessionQueueCapacity = 64,
        ISessionCompactor? compactor = null,
        IOptionsMonitor<CompactionOptions>? compactionOptions = null,
        IConversationDispatcher? conversationDispatcher = null,
        IConversationRouter? conversationRouter = null,
        PendingAskUserInterceptor? pendingAskUserInterceptor = null,
        IConversationStore? conversationStore = null,
        IOptions<PlatformConfig>? platformConfig = null,
        LlmClient? llmClient = null,
        ISessionTurnTracker? turnTracker = null)
        => new(
            supervisor,
            router,
            sessions,
            activity,
            channelManager,
            compactor ?? Mock.Of<ISessionCompactor>(),
            compactionOptions ?? new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            sessionQueueCapacity,
            conversationDispatcher: conversationDispatcher,
            conversationRouter: conversationRouter,
            pendingAskUserInterceptor: pendingAskUserInterceptor,
            conversationStore: conversationStore,
            platformConfig: platformConfig,
            llmClient: llmClient,
            turnTracker: turnTracker);

    private static InboundMessage CreateMessage(
        string content,
        string? sessionId = null,
        string conversationId = "conv-1",
        string channelType = "web",
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = metadata ?? new Dictionary<string, object?>()
        };

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }

    [Fact]
    public async Task ProcessInboundMessage_StampsSessionWithConversationId()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "web:addr-1:agent-a", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var sessions = new InMemorySessionStore();
        var expectedConvId = BotNexus.Domain.Primitives.ConversationId.From("c_stamptest1");
        var expectedSessionId = BotNexus.Domain.Primitives.SessionId.From("web:addr-1:agent-a");
        await sessions.GetOrCreateAsync(expectedSessionId, BotNexus.Domain.Primitives.AgentId.From("agent-a"));

        var conversation = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = expectedConvId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
        };
        var conversationDispatcher = new Mock<IConversationDispatcher>();
        conversationDispatcher
            .Setup(d => d.DispatchAsync(
                It.IsAny<InboundMessageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                context.Source,
                new ConversationSessionResolution(
                    conversation.ConversationId,
                    expectedSessionId,
                    false,
                    false)));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = new GatewayHost(
            supervisor.Object,
            router.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object),
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationDispatcher: conversationDispatcher.Object);

        await host.DispatchAsync(CreateMessage("hello", channelType: "web", conversationId: "addr-1"));
        conversationDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<InboundMessageContext>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce, "IConversationDispatcher.DispatchAsync must be called");

        var savedSession = await sessions.GetAsync(expectedSessionId, CancellationToken.None);
        savedSession.ShouldNotBeNull();
        savedSession!.Session.ConversationId.IsInitialized().ShouldBeTrue("Session.ConversationId must be stamped after inbound message");
        savedSession.Session.ConversationId.Value.ShouldBe("c_stamptest1");
    }

    [Fact]
    public async Task DispatchAsync_PersistsUserMessageBeforeAgentCall()
    {
        // Arrange: capture SaveAsync calls to verify user message is persisted before LLM call
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-write-ahead"]);

        var userMessageSavedBeforeLlm = false;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-write-ahead"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-wa"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "response" });

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wa"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-write-ahead")
        };

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("session-wa"),
            BotNexus.Domain.Primitives.AgentId.From("agent-write-ahead"),
            It.IsAny<CancellationToken>())).ReturnsAsync(session);

        // First SaveAsync call should be write-ahead with user message in history
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) =>
            {
                if (!userMessageSavedBeforeLlm && s.History.Any(e => e.Role == MessageRole.User && e.Content == "hello wa"))
                    userMessageSavedBeforeLlm = true;
            })
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-write-ahead"),
            BotNexus.Domain.Primitives.SessionId.From("session-wa"),
            It.IsAny<CancellationToken>())).ReturnsAsync(handle.Object);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        // Act
        await host.DispatchAsync(CreateMessage("hello wa", sessionId: "session-wa"));

        // Assert: user message was saved before PromptAsync was called
        userMessageSavedBeforeLlm.ShouldBeTrue("user message must be persisted to session store before the LLM call (#363)");
    }

    [Fact]
    public async Task DispatchAsync_RemovesCrashSentinelFromHistoryOnSuccess()
    {
        // Arrange
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-sentinel"]);

        var handle = CreatePromptHandle("agent-sentinel", "session-sentinel", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-sentinel"),
            BotNexus.Domain.Primitives.SessionId.From("session-sentinel"),
            It.IsAny<CancellationToken>())).ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-sentinel"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-sentinel")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("session-sentinel"),
            BotNexus.Domain.Primitives.AgentId.From("agent-sentinel"),
            It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        // Act
        await host.DispatchAsync(CreateMessage("hello sentinel", sessionId: "session-sentinel"));

        // Assert: no crash sentinel entries remain in history after clean completion
        session.History.ShouldNotContain(e => e.IsCrashSentinel,
            "crash sentinel must be removed from session history on clean turn completion (#363)");
    }

    [Fact]
    public async Task DispatchAsync_WhenPreviousTurnAbandonedMidTool_StopsHandleAndNotifiesUser()
    {
        // Arrange: a session whose history ends with a dangling ToolStart (no matching ToolEnd)
        // from a prior user turn. When the new inbound message arrives, PrepareTurnAsync's
        // abandoned-turn detection (#790) must fire: stop the stale handle and add a
        // Notification entry so the user knows the previous turn did not complete.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-abandon"]);

        var handle = CreatePromptHandle("agent-abandon", "session-abandon", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-abandon"),
            BotNexus.Domain.Primitives.SessionId.From("session-abandon"),
            It.IsAny<CancellationToken>())).ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-abandon"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-abandon")
        };
        // Seed a previous user turn that stalled mid-tool: a User entry followed by a
        // ToolStart (ToolArgs set) with no matching ToolEnd.
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "do the thing" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "some_tool", ToolCallId = "call-1", ToolArgs = "{}" });

        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From("session-abandon"),
            BotNexus.Domain.Primitives.AgentId.From("agent-abandon"),
            It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        // Act
        await host.DispatchAsync(CreateMessage("second message", sessionId: "session-abandon"));

        // Assert: the stale handle was stopped (forces fresh context on recreate) and a
        // Notification entry describing the abandoned turn was added to history.
        supervisor.Verify(s => s.StopAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-abandon"),
            BotNexus.Domain.Primitives.SessionId.From("session-abandon"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        session.History.ShouldContain(
            e => e.Role.Equals(MessageRole.Notification) && e.Content != null && e.Content.Contains("did not complete"),
            "abandoned-turn detection must add a notification entry when a prior turn stalled mid-tool (#790)");
    }

    [Fact]
    public async Task StreamEvents_FanOutToSignalRObserverBindings_WhenOriginatingChannelIsNotSignalR()
    {
        // Arrange: telegram message with a SignalR observer binding on the conversation
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Telegram is the originating (streaming) channel
        var telegramChannel = new Mock<IChannelAdapter>();
        telegramChannel.SetupGet(c => c.ChannelType).Returns(BotNexus.Domain.Primitives.ChannelKey.From("telegram"));
        telegramChannel.SetupGet(c => c.DisplayName).Returns("Telegram");
        telegramChannel.SetupGet(c => c.SupportsStreaming).Returns(true);
        telegramChannel.As<IStreamEventChannelAdapter>()
            .Setup(c => c.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        telegramChannel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        telegramChannel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // SignalR is an observer binding
        var signalrChannel = new Mock<IChannelAdapter>();
        signalrChannel.SetupGet(c => c.ChannelType).Returns(BotNexus.Domain.Primitives.ChannelKey.From("signalr"));
        signalrChannel.SetupGet(c => c.DisplayName).Returns("SignalR");
        signalrChannel.SetupGet(c => c.SupportsStreaming).Returns(true);
        signalrChannel.As<IStreamEventChannelAdapter>()
            .Setup(c => c.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        signalrChannel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Channel manager returns both adapters
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([telegramChannel.Object, signalrChannel.Object]);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "telegram")))
            .Returns(telegramChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "telegram"), It.IsAny<string?>()))
            .Returns(telegramChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "signalr")))
            .Returns(signalrChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "signalr"), It.IsAny<string?>()))
            .Returns(signalrChannel.Object);

        // Conversation router returns a SignalR binding as a non-originating binding
        var signalrBinding = new BotNexus.Gateway.Abstractions.Models.ChannelBinding
        {
            ChannelType = BotNexus.Domain.Primitives.ChannelKey.From("signalr"),
            ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress.From("signalr-session-abc"),
            Mode = BotNexus.Gateway.Abstractions.Models.BindingMode.Interactive
        };
        var conversationRouter = new Mock<IConversationRouter>();
        var routingConversation = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-test-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        conversationRouter.Setup(r => r.ResolveInboundAsync(
                It.IsAny<BotNexus.Domain.Primitives.AgentId>(),
                It.IsAny<BotNexus.Domain.Primitives.ChannelKey>(),
                It.IsAny<BotNexus.Domain.Primitives.ChannelAddress>(),
                It.IsAny<BotNexus.Domain.Primitives.ConversationId?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<BotNexus.Domain.World.CitizenId?>()))
            .ReturnsAsync(new ConversationRoutingResult(routingConversation, BotNexus.Domain.Primitives.SessionId.From("session-1"), false));
        conversationRouter.Setup(r => r.GetOutboundBindingsAsync(
                It.IsAny<BotNexus.Domain.Primitives.SessionId>(),
                It.IsAny<BotNexus.Domain.Primitives.BindingId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([signalrBinding]);

        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), channelManager.Object,
            conversationRouter: conversationRouter.Object);

        // Act: dispatch a Telegram message
        var message = CreateMessage("hello", sessionId: "session-1", channelType: "telegram");
        await host.DispatchAsync(message);

        // Assert: SignalR observer received stream events (3 events: MessageStart, ContentDelta, MessageEnd).
        // The stream target is built from the resolved session ID and the observer binding's
        // ChannelAddress + BindingId — not the originating Telegram address. This is the
        // typed-target contract introduced with ChannelStreamTarget (#677).
        signalrChannel.As<IStreamEventChannelAdapter>().Verify(
            c => c.SendStreamEventAsync(
                It.Is<ChannelStreamTarget>(t =>
                    t.SessionId == SessionId.From("session-1") &&
                    t.ChannelAddress == ChannelAddress.From("signalr-session-abc")),
                It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.MessageStart),
                It.IsAny<CancellationToken>()),
            Times.Once);
        signalrChannel.As<IStreamEventChannelAdapter>().Verify(
            c => c.SendStreamEventAsync(
                It.Is<ChannelStreamTarget>(t =>
                    t.SessionId == SessionId.From("session-1") &&
                    t.ChannelAddress == ChannelAddress.From("signalr-session-abc")),
                It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.ContentDelta),
                It.IsAny<CancellationToken>()),
            Times.Once);
        signalrChannel.As<IStreamEventChannelAdapter>().Verify(
            c => c.SendStreamEventAsync(
                It.Is<ChannelStreamTarget>(t =>
                    t.SessionId == SessionId.From("session-1") &&
                    t.ChannelAddress == ChannelAddress.From("signalr-session-abc")),
                It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.MessageEnd),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StreamEvents_InternalPrimaryResolvedToSignalR_DoesNotDuplicateSignalRObserverDelivery()
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" }
            ]));

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("signalr")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.GetAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var signalrChannel = CreateStreamEventChannel("signalr");
        var internalChannel = CreateStreamEventChannel("internal");
        internalChannel.As<IStreamEventChannelAdapter>()
            .Setup(c => c.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns((ChannelStreamTarget target, AgentStreamEvent evt, CancellationToken ct) =>
                signalrChannel.As<IStreamEventChannelAdapter>().Object.SendStreamEventAsync(target, evt, ct));
        internalChannel.As<IChannelDestinationResolver>()
            .Setup(c => c.ResolveStreamDestinationAsync(SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signalrChannel.Object);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([internalChannel.Object, signalrChannel.Object]);
        channelManager.Setup(m => m.Get(It.Is<ChannelKey>(k => k.Value == "internal")))
            .Returns(internalChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<ChannelKey>(k => k.Value == "internal"), It.IsAny<string?>()))
            .Returns(internalChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<ChannelKey>(k => k.Value == "signalr")))
            .Returns(signalrChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<ChannelKey>(k => k.Value == "signalr"), It.IsAny<string?>()))
            .Returns(signalrChannel.Object);

        var signalrBinding = new ChannelBinding
        {
            BindingId = BindingId.From("signalr-binding"),
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("portal"),
            Mode = BindingMode.Interactive
        };
        var duplicateGroupBinding = new ChannelBinding
        {
            BindingId = BindingId.From("signalr-binding-2"),
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("portal-2"),
            Mode = BindingMode.Interactive
        };
        var conversationRouter = CreateObserverConversationRouter(
            "conv-test-internal", "session-1", signalrBinding, duplicateGroupBinding);

        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), channelManager.Object,
            conversationRouter: conversationRouter.Object);

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1", channelType: "internal"));

        signalrChannel.As<IStreamEventChannelAdapter>().Verify(
            c => c.SendStreamEventAsync(
                It.IsAny<ChannelStreamTarget>(),
                It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.ContentDelta),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<IChannelAdapter> CreateStreamEventChannel(string channelType)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(ChannelKey.From(channelType));
        channel.SetupGet(c => c.DisplayName).Returns(channelType);
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.As<IStreamEventChannelAdapter>()
            .Setup(c => c.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    private static Mock<IConversationRouter> CreateObserverConversationRouter(
        string conversationId, string sessionId, params ChannelBinding[] bindings)
    {
        var conversationRouter = new Mock<IConversationRouter>();
        var conversation = new Conversation
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From("agent-a")
        };
        conversationRouter.Setup(r => r.ResolveInboundAsync(
                It.IsAny<AgentId>(), It.IsAny<ChannelKey>(), It.IsAny<ChannelAddress>(),
                It.IsAny<ConversationId?>(), It.IsAny<CancellationToken>(), It.IsAny<CitizenId?>()))
            .ReturnsAsync(new ConversationRoutingResult(conversation, SessionId.From(sessionId), false));
        conversationRouter.Setup(r => r.GetOutboundBindingsAsync(
                It.IsAny<SessionId>(), It.IsAny<BindingId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bindings);
        return conversationRouter;
    }

    [Fact]
    public async Task StreamEvents_DoNotFanOutToSignalRObserver_WhenOriginatingChannelIsSignalR()
    {
        // Arrange: SignalR is the originating channel -- no additional fan-out expected
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" }
            ]));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var signalrChannel = new Mock<IChannelAdapter>();
        signalrChannel.SetupGet(c => c.ChannelType).Returns(BotNexus.Domain.Primitives.ChannelKey.From("signalr"));
        signalrChannel.SetupGet(c => c.DisplayName).Returns("SignalR");
        signalrChannel.SetupGet(c => c.SupportsStreaming).Returns(true);
        signalrChannel.As<IStreamEventChannelAdapter>()
            .Setup(c => c.SendStreamEventAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        signalrChannel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([signalrChannel.Object]);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "signalr")))
            .Returns(signalrChannel.Object);
        channelManager.Setup(m => m.Get(It.Is<BotNexus.Domain.Primitives.ChannelKey>(k => k.Value == "signalr"), It.IsAny<string?>()))
            .Returns(signalrChannel.Object);

        // Router should NOT be called for GetOutboundBindings when originating channel is signalr
        var conversationRouter = new Mock<IConversationRouter>();
        var routingConversation2 = new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.From("conv-test-2"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        conversationRouter.Setup(r => r.ResolveInboundAsync(
                It.IsAny<BotNexus.Domain.Primitives.AgentId>(),
                It.IsAny<BotNexus.Domain.Primitives.ChannelKey>(),
                It.IsAny<BotNexus.Domain.Primitives.ChannelAddress>(),
                It.IsAny<BotNexus.Domain.Primitives.ConversationId?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<BotNexus.Domain.World.CitizenId?>()))
            .ReturnsAsync(new ConversationRoutingResult(routingConversation2, BotNexus.Domain.Primitives.SessionId.From("session-1"), false));
        conversationRouter.Setup(r => r.GetOutboundBindingsAsync(
                It.IsAny<BotNexus.Domain.Primitives.SessionId>(),
                It.IsAny<BotNexus.Domain.Primitives.BindingId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), channelManager.Object,
            conversationRouter: conversationRouter.Object);

        var message = CreateMessage("hello", sessionId: "session-1", channelType: "signalr");
        await host.DispatchAsync(message);

        // When originating channel is signalr, the guard prevents the pre-streaming observer resolution.
        // GetOutboundBindings may still be called once by FanOutResponseAsync at turn end -- that's fine.
        // The key assertion: the signalr adapter receives stream events only as the PRIMARY channel,
        // NOT as an observer (i.e. SendStreamEventAsync is called via the primary streaming path, not doubled).
        // Since GetOutboundBindings returns [], no additional observer fan-out occurs.
        signalrChannel.As<IStreamEventChannelAdapter>().Verify(
            c => c.SendStreamEventAsync(
                It.IsAny<ChannelStreamTarget>(),
                It.IsAny<AgentStreamEvent>(),
                It.IsAny<CancellationToken>()),
            Times.Once); // exactly once as the primary channel, not doubled via observer fan-out
    }

    // #756 — transcript mirror isolation: SaveAsync failures after a successful channel send
    // must NOT propagate as delivery failures (which could trigger duplicate retries).

    [Fact]
    public async Task DispatchAsync_WhenSaveAsyncThrows_DoesNotPropagateException_AndChannelSendSucceeded()
    {
        // Arrange: agent responds, channel send succeeds, but SaveAsync (transcript write) throws.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = CreatePromptHandle("agent-a", "session-1", "agent-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var saveCallCount = 0;
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns<GatewaySession, CancellationToken>((_, _) =>
            {
                saveCallCount++;
                // First two saves succeed (write-ahead user message + crash sentinel);
                // the transcript save after channel send throws.
                if (saveCallCount >= 3)
                    throw new InvalidOperationException("Simulated transcript write failure");
                return Task.CompletedTask;
            });
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, activity,
            CreateChannelManager(channel.Object));

        // Act: should NOT throw even though SaveAsync threw on the 3rd call.
        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        // Assert: channel send was called (delivery happened).
        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "agent-response"),
            It.IsAny<CancellationToken>()), Times.Once);
        // AgentCompleted activity published (turn completed despite transcript failure).
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentCompleted);
    }

    [Fact]
    public async Task DispatchAsync_WhenSaveAsyncThrowsException_ChannelSendCalledExactlyOnce()
    {
        // Verify no duplicate send occurs when SaveAsync fails: the channel must see exactly
        // one SendAsync call, proving the transcript failure did not trigger a retry path.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-b"]);
        var handle = CreatePromptHandle("agent-b", "session-2", "only-once");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-b"),
                BotNexus.Domain.Primitives.SessionId.From("session-2"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-2"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-b")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-2"),
                BotNexus.Domain.Primitives.AgentId.From("agent-b"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var saveCallCount = 0;
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns<GatewaySession, CancellationToken>((_, _) =>
            {
                saveCallCount++;
                if (saveCallCount >= 3)
                    throw new IOException("Disk full - transcript write failed");
                return Task.CompletedTask;
            });
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object,
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-2"));

        // Channel send must be called exactly once — no duplicate.
        channel.Verify(c => c.SendAsync(
            It.IsAny<OutboundMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenSaveAsyncSucceeds_NormalBehaviourUnchanged()
    {
        // Regression guard: ensure the happy path still records history and completes normally.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-c"]);
        var handle = CreatePromptHandle("agent-c", "session-3", "normal-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-c"),
                BotNexus.Domain.Primitives.SessionId.From("session-3"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-3"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-c")
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.SessionId.From("session-3"),
                BotNexus.Domain.Primitives.AgentId.From("agent-c"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        var activity = new RecordingActivityBroadcaster();
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, activity,
            CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-3"));

        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "normal-response"),
            It.IsAny<CancellationToken>()), Times.Once);
        session.History.Select(e => $"{e.Role}:{e.Content}").ToList()
            .ShouldBe(["user:hello", "assistant:normal-response"]);
        activity.Activities.Select(a => a.Type).ShouldContain(GatewayActivityType.AgentCompleted);
    }

    // #849 -- thinking-only response stall detection

    [Fact]
    public async Task DispatchAsync_WhenAgentReturnsThinkingOnlyResponse_DoesNotSendMessage()
    {
        // Arrange: agent returns a response that consists solely of a thinking block.
        // The channel does NOT support thinking display (most channels).
        // Expected: no message delivered — thinking-only is silently dropped (#1198).
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var thinkingOnlyContent = "<thinking>Only internal reasoning, no visible answer.</thinking>";
        var handle = CreatePromptHandle("agent-a", "session-1", thinkingOnlyContent);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        // Explicitly confirm SupportsThinkingDisplay is false.
        channel.SetupGet(c => c.SupportsThinkingDisplay).Returns(false);

        await using var host = CreateHost(
            supervisor.Object, router.Object, new InMemorySessionStore(),
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        // Assert: channel received NO message — thinking-only responses are silently dropped.
        channel.Verify(c => c.SendAsync(
            It.IsAny<OutboundMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WhenChannelSupportsThinkingDisplay_ThinkingOnlyResponsePassedThrough()
    {
        // When the channel supports thinking display, thinking-only responses should pass
        // through unchanged -- no stall substitution.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var thinkingOnlyContent = "<thinking>Only internal reasoning, no visible answer.</thinking>";
        var handle = CreatePromptHandle("agent-a", "session-1", thinkingOnlyContent);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                BotNexus.Domain.Primitives.SessionId.From("session-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        channel.SetupGet(c => c.SupportsThinkingDisplay).Returns(true);

        await using var host = CreateHost(
            supervisor.Object, router.Object, new InMemorySessionStore(),
            new RecordingActivityBroadcaster(), CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        // Assert: raw content passed through -- no stall notice injected.
        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == thinkingOnlyContent),
            It.IsAny<CancellationToken>()), Times.Once);
    }

}

