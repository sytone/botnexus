using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
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
        var session = await sessionStore.GetAsync("session-1");

        session!.History.Should().Contain(e => e.Role == "assistant" && e.Content == "stream works");
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
        var session = await sessionStore.GetAsync("session-1");

        session!.History.Should().Contain(e => e.Role == "tool" && e.ToolName == "clock" && e.ToolCallId == "call-1");
        session.History.Should().Contain(e => e.Role == "tool" && e.Content == "12:00");
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
        var session = await sessionStore.GetAsync("session-1");

        session!.History.Select(h => h.Role).Should().Equal("user", "tool", "tool", "assistant");
        session.History.Last().Content.Should().Be("The answer is 42.");
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

        handle.EnumeratorDisposed.Should().BeTrue();
        activity.Activities.Should().Contain(a => a.Type == GatewayActivityType.Error);
    }

    [Fact]
    public async Task DispatchAsync_WithEmptyStream_ProducesNoAssistantHistoryEntry()
    {
        var sessionStore = new InMemorySessionStore();
        var channel = CreateStreamingChannel();
        await using var host = CreateStreamingHost([], sessionStore, channel.Object, out _);

        await host.DispatchAsync(CreateMessage("empty"));
        var session = await sessionStore.GetAsync("session-1");

        session!.History.Should().HaveCount(1);
        session.History[0].Role.Should().Be("user");
        session.History[0].Content.Should().Be("empty");
    }

    private static Mock<IChannelAdapter> CreateStreamingChannel()
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns("web");
        channel.SetupGet(c => c.DisplayName).Returns("Web");
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.Setup(c => c.SendStreamDeltaAsync("conv-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        handle.SetupGet(h => h.AgentId).Returns("agent-a");
        handle.SetupGet(h => h.SessionId).Returns("session-1");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.StreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(streamEvents);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("agent-a", "session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(c => c.Adapters).Returns(streamingChannel is null ? [] : [streamingChannel]);
        channelManager.Setup(c => c.Get("web")).Returns(streamingChannel);
        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessionStore,
            activity,
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            Options.Create(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);
    }

    private static InboundMessage CreateMessage(string content)
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            ConversationId = "conv-1",
            Content = content,
            SessionId = "session-1"
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
