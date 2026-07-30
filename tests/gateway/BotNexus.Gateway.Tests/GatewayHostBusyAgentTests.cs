using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression coverage for issue #2388: an inbound message arriving while the addressed agent is
/// mid-turn must NOT be lost.
/// <para>
/// Before the fix, <see cref="GatewayHost.ProcessAsync"/> pushed the message straight at the
/// running agent, <c>Agent.RunAsync</c>'s (correct) single-turn guard threw
/// <c>InvalidOperationException: Agent is already running</c>, and the exception escaped through
/// the generic catch as an ERR log plus an <see cref="GatewayActivityType.Error"/> activity - the
/// message itself was discarded with no queueing and no rejection semantics.
/// </para>
/// <para>
/// Both observed production entry paths are pinned here, because the exception surfaced on each of
/// them independently: the streaming path (<c>InProcessAgentHandle.StreamCoreAsync</c>) and the
/// blocking path (<c>InProcessAgentHandle.PromptAsync</c>). A fix that guards only one leaves the
/// other lossy.
/// </para>
/// </summary>
public sealed class GatewayHostBusyAgentTests
{
    [Fact]
    public async Task DispatchAsync_WhenAgentBusy_OnStreamingPath_QueuesFollowUpInsteadOfLosingMessage()
    {
        var (host, handle, activity, session, streamCalls, promptCalls) = await RunBusyDispatchAsync(supportsStreaming: true);
        await host.DisposeAsync();

        // The message must be queued through the existing follow-up seam (#2458), not pushed at
        // the running agent.
        handle.Verify(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        streamCalls.Count.ShouldBe(0, "a busy agent must not be driven into a second concurrent turn on the streaming path");
        promptCalls.Count.ShouldBe(0);

        // No unhandled exception surfaced to the caller as an error outcome.
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.Error,
            "the busy-agent boundary must not surface as an unhandled error (#2388)");

        // The caller is positively told the message was accepted for later delivery.
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.FollowUpQueued,
            "the caller must observe that the message was queued rather than silently dropped");

        // The message survives in the transcript - it is not lost.
        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "second message");
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentBusy_OnBlockingPromptPath_QueuesFollowUpInsteadOfLosingMessage()
    {
        var (host, handle, activity, session, streamCalls, promptCalls) = await RunBusyDispatchAsync(supportsStreaming: false);
        await host.DisposeAsync();

        handle.Verify(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        promptCalls.Count.ShouldBe(0, "a busy agent must not be driven into a second concurrent turn on the blocking prompt path");
        streamCalls.Count.ShouldBe(0);

        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.Error,
            "the busy-agent boundary must not surface as an unhandled error (#2388)");
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.FollowUpQueued);
        session.History.ShouldContain(e => e.Role == MessageRole.User && e.Content == "second message");
    }

    [Fact]
    public async Task DispatchAsync_WhenAgentIdle_StillRunsTheTurnNormally()
    {
        // Non-vacuity companion: the busy-agent boundary must not divert idle traffic into the
        // follow-up queue - an idle agent still runs the turn inline.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var promptCalls = new List<AgentUserMessage>();
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((m, _) => promptCalls.Add(m))
            .ReturnsAsync(new AgentResponse { Content = "idle reply" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = SessionId.From("session-1"), AgentId = AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("hello", sessionId: "session-1"));

        promptCalls.Count.ShouldBe(1, "an idle agent must still be prompted inline");
        activity.Activities.ShouldNotContain(a => a.Type == GatewayActivityType.FollowUpQueued);
        session.History.ShouldContain(e => e.Role == MessageRole.Assistant && e.Content == "idle reply");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives one inbound message at an agent whose handle reports a live run. Both the streaming
    /// and blocking entry points are wired to throw the exact production exception
    /// (<c>InvalidOperationException: Agent is already running.</c>) so that any code path which
    /// still pushes at the busy agent reproduces the #2388 loss rather than passing silently.
    /// </summary>
    private static async Task<(GatewayHost Host, Mock<IAgentHandle> Handle, RecordingActivityBroadcaster Activity,
        GatewaySession Session, List<AgentUserMessage> StreamCalls, List<AgentUserMessage> PromptCalls)>
        RunBusyDispatchAsync(bool supportsStreaming)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var streamCalls = new List<AgentUserMessage>();
        var promptCalls = new List<AgentUserMessage>();

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.SetupGet(h => h.IsRunning).Returns(true);

        // The queue seam accepts the message because a run really is in flight.
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Both entry paths reproduce the real single-turn guard. Agent.RunAsync's guard is correct
        // and is deliberately NOT weakened - the gateway must simply never reach it.
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns((AgentUserMessage m, CancellationToken _) =>
            {
                streamCalls.Add(m);
                return AlreadyRunningStream();
            });
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns((AgentUserMessage m, CancellationToken _) =>
            {
                promptCalls.Add(m);
                throw new InvalidOperationException("Agent is already running.");
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession { SessionId = SessionId.From("session-1"), AgentId = AgentId.From("agent-a") };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var activity = new RecordingActivityBroadcaster();
        var channel = CreateChannelAdapter("web", supportsStreaming);
        var host = CreateHost(supervisor.Object, router.Object, sessions.Object, activity, CreateChannelManager(channel.Object));

        await host.DispatchAsync(CreateMessage("second message", sessionId: "session-1"));

        return (host, handle, activity, session, streamCalls, promptCalls);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> AlreadyRunningStream()
    {
        await Task.Yield();
        throw new InvalidOperationException("Agent is already running.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static Mock<IChannelAdapter> CreateChannelAdapter(string channelType, bool supportsStreaming)
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(channelType);
        channel.SetupGet(c => c.DisplayName).Returns(channelType);
        channel.SetupGet(c => c.SupportsStreaming).Returns(supportsStreaming);
        channel.SetupGet(c => c.SupportsThinkingDisplay).Returns(false);
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
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager)
        => new(
            supervisor,
            router,
            sessions,
            activity,
            channelManager,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);

    private static InboundMessage CreateMessage(
        string content,
        string? sessionId = null,
        string conversationId = "conv-1",
        string channelType = "web")
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null),
            Metadata = new Dictionary<string, object?>()
        };

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<GatewayActivity>();
    }
}
