using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayHostConversationEventTests
{
    [Fact]
    public async Task DispatchAsync_StreamingCallback_PublishesOrderedConversationFactsWithStableConversationAndReplacementSession()
    {
        var agentId = AgentId.From("agent-2087");
        var conversationId = ConversationId.From("conversation-stable");
        var replacedSessionId = SessionId.From("session-replaced");
        var activeSessionId = SessionId.From("session-active");
        var userId = UserId.From("user-2087");
        var originBindingId = BindingId.From("binding-origin");
        var observerBindingId = BindingId.From("binding-observer");
        var conversationStore = new InMemoryConversationStore();
        var sourceBinding = new ChannelBinding
        {
            BindingId = originBindingId,
            ChannelType = ChannelKey.From("web"),
            AdapterId = "desktop",
            ChannelAddress = ChannelAddress.From("browser-origin"),
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };
        var observerBinding = new ChannelBinding
        {
            BindingId = observerBindingId,
            ChannelType = ChannelKey.From("telegram"),
            AdapterId = "bot-a",
            ChannelAddress = ChannelAddress.From("chat-42"),
            Mode = BindingMode.NotifyOnly,
            ThreadingMode = ThreadingMode.Prefix
        };
        await conversationStore.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = activeSessionId,
            ChannelBindings = [sourceBinding, observerBinding]
        });

        var claims = new ClaimAuditSignal(true, [new ClaimAuditClaim("artifact", "missing citation")]);
        var usage = new AgentResponseUsage(11, 7, 3, 2);
        var emitted = new[]
        {
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "content" },
            new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "thinking" },
            new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-1", ToolName = "read", ToolArgs = new Dictionary<string, object?> { ["path"] = "a.md" } },
            new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-1", ToolName = "read", ToolResult = "done", ToolIsError = false },
            new AgentStreamEvent { Type = AgentStreamEventType.ClaimAudit, ClaimAudit = claims },
            new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, Usage = usage, FinalContent = "content" }
        };

        var capture = new CapturingConversationEventSink();
        await using var publisher = new ConversationEventPublisher([capture]);
        var sessionStore = new InMemorySessionStore();
        var channel = new NoOpStreamChannelAdapter();
        await using var host = CreateHost(
            agentId,
            activeSessionId,
            conversationId,
            originBindingId,
            conversationStore,
            sessionStore,
            channel,
            StreamAndMutateBindings(emitted, sourceBinding, observerBinding),
            publisher);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = channel.ChannelType,
            SenderId = userId.Value,
            Sender = CitizenId.Of(userId),
            ChannelAddress = sourceBinding.ChannelAddress,
            BindingId = originBindingId,
            ChannelRequestId = "request-42",
            Content = "hello",
            RoutingHints = new InboundMessageRoutingHints(agentId, replacedSessionId, conversationId)
        });
        await publisher.WaitForDrainAsync();

        var received = capture.Received.Cast<ConversationAgentEvent>().ToArray();
        received.Length.ShouldBe(emitted.Length);
        received.Select(item => item.StreamEvent.Type).ShouldBe(emitted.Select(item => item.Type));
        received.ShouldAllBe(item => item.AgentId == agentId);
        received.ShouldAllBe(item => item.ConversationId == conversationId);
        received.ShouldAllBe(item => item.SessionId == activeSessionId);
        received.ShouldAllBe(item => item.StreamEvent.AgentId == agentId);
        received.ShouldAllBe(item => item.StreamEvent.ConversationId == conversationId);
        received.ShouldAllBe(item => item.StreamEvent.SessionId == activeSessionId);
        received.ShouldAllBe(item => item.Origin == new ConversationEventOrigin(originBindingId, userId, "request-42"));
        received.ShouldAllBe(item => item.Bindings.Length == 2);
        received.ShouldAllBe(item => item.Bindings[0] == new ConversationBindingSnapshot(
            originBindingId,
            ChannelKey.From("web"),
            "desktop",
            ChannelAddress.From("browser-origin"),
            BindingMode.Interactive,
            ThreadingMode.Single));
        received.ShouldAllBe(item => item.Bindings[1] == new ConversationBindingSnapshot(
            observerBindingId,
            ChannelKey.From("telegram"),
            "bot-a",
            ChannelAddress.From("chat-42"),
            BindingMode.NotifyOnly,
            ThreadingMode.Prefix));

        received[0].StreamEvent.ContentDelta.ShouldBe("content");
        received[1].StreamEvent.ThinkingContent.ShouldBe("thinking");
        received[2].StreamEvent.ToolCallId.ShouldBe("call-1");
        received[2].StreamEvent.ToolName.ShouldBe("read");
        received[2].StreamEvent.ToolArgs!["path"].ShouldBe("a.md");
        received[3].StreamEvent.ToolResult.ShouldBe("done");
        received[3].StreamEvent.ToolIsError.ShouldBeFalse();
        received[4].StreamEvent.ClaimAudit.ShouldBeSameAs(claims);
        received[5].StreamEvent.Usage.ShouldBeSameAs(usage);
        received[5].StreamEvent.FinalContent.ShouldBe("content");

        (await conversationStore.GetAsync(conversationId))!.ActiveSessionId.ShouldBe(activeSessionId);
        (await sessionStore.GetAsync(activeSessionId))!.Session.ConversationId.ShouldBe(conversationId);
        (await sessionStore.GetAsync(replacedSessionId)).ShouldBeNull();
    }

    private static GatewayHost CreateHost(
        AgentId agentId,
        SessionId sessionId,
        ConversationId conversationId,
        BindingId originBindingId,
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        IChannelAdapter channel,
        IAsyncEnumerable<AgentStreamEvent> stream,
        IConversationEventPublisher publisher)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(value => value.AgentId).Returns(agentId);
        handle.SetupGet(value => value.SessionId).Returns(sessionId);
        handle.SetupGet(value => value.IsRunning).Returns(false);
        handle.Setup(value => value.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(stream);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.GetOrCreateAsync(agentId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var router = new Mock<IMessageRouter>();
        router.Setup(value => value.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([agentId.Value]);

        var dispatcher = new Mock<IConversationDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                context,
                new ChannelSource(channel.ChannelType, ChannelAddress.From("browser-origin"), "user-2087", originBindingId, null),
                new ConversationSessionResolution(conversationId, sessionId, false, true, originBindingId, null)));

        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessionStore,
            new NoOpActivityBroadcaster(),
            new ChannelManager([channel]),
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationDispatcher: dispatcher.Object,
            conversationStore: conversationStore,
            conversationEventPublisher: publisher);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> StreamAndMutateBindings(
        IReadOnlyList<AgentStreamEvent> events,
        ChannelBinding sourceBinding,
        ChannelBinding observerBinding)
    {
        yield return events[0];
        sourceBinding.ChannelAddress = ChannelAddress.From("mutated-origin");
        observerBinding.Mode = BindingMode.Muted;
        for (var index = 1; index < events.Count; index++)
        {
            await Task.Yield();
            yield return events[index];
        }
    }

    private sealed class CapturingConversationEventSink : IConversationEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<ConversationEvent> _received = new();

        public IReadOnlyList<ConversationEvent> Received => _received.ToArray();

        public Task OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
        {
            _received.Enqueue(conversationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpStreamChannelAdapter : IChannelAdapter, IStreamEventChannelAdapter
    {
        public ChannelKey ChannelType => ChannelKey.From("web");
        public string? AdapterId => "desktop";
        public string DisplayName => "Web";
        public bool SupportsStreaming => true;
        public bool SupportsSteering => false;
        public bool SupportsFollowUp => false;
        public bool SupportsThinkingDisplay => true;
        public bool SupportsToolDisplay => true;
        public bool SupportsInboundImages => false;
        public bool IsRunning => true;
        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
