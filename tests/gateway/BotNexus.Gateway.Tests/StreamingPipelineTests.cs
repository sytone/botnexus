using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class StreamingPipelineTests
{
    [Fact]
    public async Task DispatchAsync_ContentDeltasAccumulateIntoFullResponse()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "stream " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "works" }
            ],
            sessionStore,
            channel.Object,
            out _);

        await host.DispatchAsync(CreateMessage("hello"));
        var session = await sessionStore.GetAsync(SessionId.From("session-1"));

        session!.History.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "stream works");
    }

    [Fact]
    public async Task DispatchAsync_ToolEventsAreTracked()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-1", ToolName = "clock" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-1", ToolName = "clock", ToolResult = "12:00" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "done" }
            ],
            sessionStore,
            channel.Object,
            out _);

        await host.DispatchAsync(CreateMessage("what time"));
        var session = await sessionStore.GetAsync(SessionId.From("session-1"));

        session!.History.ShouldContain(e => e.Role == MessageRole.Tool && e.ToolName == "clock" && e.ToolCallId == "call-1");
        session.History.ShouldContain(e => e.Role == MessageRole.Tool && e.Content == "12:00");
    }

    [Fact]
    public async Task DispatchAsync_MixedStreamEvents_ProducesExpectedHistory()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "The answer is " },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-1", ToolName = "math" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-1", ToolName = "math", ToolResult = "42" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "42." }
            ],
            sessionStore,
            channel.Object,
            out _);

        await host.DispatchAsync(CreateMessage("solve"));
        var session = await sessionStore.GetAsync(SessionId.From("session-1"));

        session!.History.Select(h => h.Role).ShouldBe(new[] {
            MessageRole.User,
            MessageRole.Tool,
            MessageRole.Tool,
            MessageRole.Assistant });
        session.History.Last().Content.ShouldBe("The answer is 42.");
    }

    [Fact]
    public async Task DispatchAsync_WhenStreamIsCancelled_CleansUpEnumerator()
    {
        var sessionStore = new InMemorySessionStore();
        var handle = new CancellableStreamHandle();
        var activity = new RecordingActivityBroadcaster();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost(
            handle.Stream(),
            sessionStore,
            channel.Object,
            activity);

        await host.DispatchAsync(CreateMessage("cancel me"));

        handle.EnumeratorDisposed.ShouldBeTrue();
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.Error);
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyStream_ProducesNoAssistantHistoryEntry()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost([], sessionStore, channel.Object, out _);

        await host.DispatchAsync(CreateMessage("empty"));
        var session = await sessionStore.GetAsync(SessionId.From("session-1"));

        session!.History.Count().ShouldBe(1);
        session.History[0].Role.ShouldBe(MessageRole.User);
        session.History[0].Content.ShouldBe("empty");
    }

    [Fact]
    public async Task DispatchAsync_OptInStreamsThroughAdapterWhoseDefaultIsOneShot()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        channel.SetupGet(c => c.SupportsStreaming).Returns(false);
        var streamChannel = channel.As<IStreamEventChannelAdapter>();
        streamChannel.Setup(c => c.SendStreamEventAsync(
                It.Is<ChannelStreamTarget>(t => t.ChannelRequestId == "request-42"),
                It.IsAny<AgentStreamEvent>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var host = CreateStreamingHost(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "opted in" },
                new AgentStreamEvent { Type = AgentStreamEventType.RunEnded }
            ],
            sessionStore,
            channel.Object,
            out _);

        await host.DispatchAsync(CreateMessage("hello") with
        {
            StreamResponse = true,
            ChannelRequestId = "request-42",
        });

        streamChannel.Verify(c => c.SendStreamEventAsync(
            It.Is<ChannelStreamTarget>(t => t.ChannelRequestId == "request-42"),
            It.Is<AgentStreamEvent>(e => e.Type == AgentStreamEventType.ContentDelta),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_ExplicitOptOutUsesOneShotAndPropagatesRequestIdentity()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        var streamChannel = channel.As<IStreamEventChannelAdapter>();
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.PromptAsync(It.IsAny<BotNexus.Agent.Core.Types.UserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "one shot" });
        await using var host = CreateHostWithHandle(sessionStore, channel.Object, handle.Object);

        await host.DispatchAsync(CreateMessage("hello") with
        {
            StreamResponse = false,
            ChannelRequestId = "request-42",
        });

        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.ChannelRequestId == "request-42" && m.Content == "one shot"),
            It.IsAny<CancellationToken>()), Times.Once);
        streamChannel.Verify(c => c.SendStreamEventAsync(
            It.IsAny<ChannelStreamTarget>(), It.IsAny<AgentStreamEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IChannelAdapter> CreateStreamingChannel()
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(ChannelKey.From("web"));
        channel.SetupGet(c => c.DisplayName).Returns("Web");
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.Setup(c => c.SendStreamDeltaAsync(StreamTargets.For("conv-1"), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    private static GatewayHost CreateStreamingHost(
        IEnumerable<AgentStreamEvent> streamEvents,
        ISessionStore sessionStore,
        IChannelAdapter? streamingChannel,
        out RecordingActivityBroadcaster activity)
        => CreateStreamingHost(ToAsyncEnumerable(streamEvents), sessionStore, streamingChannel, out activity);

    private static GatewayHost CreateStreamingHost(
        IAsyncEnumerable<AgentStreamEvent> streamEvents,
        ISessionStore sessionStore,
        IChannelAdapter? streamingChannel,
        out RecordingActivityBroadcaster activity)
    {
        activity = new RecordingActivityBroadcaster();
        return CreateStreamingHost(streamEvents, sessionStore, streamingChannel, activity);
    }

    private static GatewayHost CreateStreamingHost(
        IAsyncEnumerable<AgentStreamEvent> streamEvents,
        ISessionStore sessionStore,
        IChannelAdapter? streamingChannel,
        RecordingActivityBroadcaster activity)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<BotNexus.Agent.Core.Types.UserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(streamEvents);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("agent-a"), BotNexus.Domain.Primitives.SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(c => c.Adapters).Returns(streamingChannel is null ? [] : [streamingChannel]);
        channelManager.Setup(c => c.Get("web")).Returns(streamingChannel);
        channelManager.Setup(c => c.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey _, string? __) => streamingChannel);
        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessionStore,
            activity,
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);
    }

    private static GatewayHost CreateHostWithHandle(
        ISessionStore sessionStore,
        IChannelAdapter channel,
        IAgentHandle handle)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(c => c.Adapters).Returns([channel]);
        channelManager.Setup(c => c.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns(channel);
        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessionStore,
            new RecordingActivityBroadcaster(),
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);
    }

    private static InboundMessage CreateMessage(string content)
        => new()
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            RoutingHints = new InboundMessageRoutingHints(null, SessionId.From("session-1"), null)
        };

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class CancellableStreamHandle
    {
        public bool EnumeratorDisposed { get; private set; }

        public async IAsyncEnumerable<AgentStreamEvent> Stream()
        {
            try
            {
                yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "partial" };
                await Task.Yield();
                throw new OperationCanceledException("cancelled by test");
            }
            finally
            {
                EnumeratorDisposed = true;
            }
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
}



