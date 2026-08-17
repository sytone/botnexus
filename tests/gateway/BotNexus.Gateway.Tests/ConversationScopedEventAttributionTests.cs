using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3065: a conversation-scoped inbound event must carry a non-empty conversation id, sourced
/// from the already-resolved <see cref="ConversationSessionResolution"/> rather than left for the
/// receiver to guess from ambient client state.
/// </summary>
/// <remarks>
/// <para>
/// The defect these tests pin is a <em>silent</em> one. An event emitted without a conversation id
/// is not dropped and does not error - the portal client attributes it to whatever conversation
/// happens to be active, so a stream chunk, tool pill or steering pill from conversation B renders
/// inside conversation A and looks like a UI bug rather than a routing fault. That is why the
/// happy-path assertions below check the id is the one on the RESOLUTION and explicitly differs
/// from any notion of an "active" conversation: an assertion that merely checks "some id is
/// present" would pass against the very fallback being removed.
/// </para>
/// <para>
/// Scope is gateway-side only. The portal's eight <c>?? agent.ActiveConversationId</c> fallbacks
/// are deliberately left in place here - they become provably unreachable for conversation-scoped
/// events, and their deletion belongs to the portal route-ownership arc.
/// </para>
/// </remarks>
public sealed class ConversationScopedEventAttributionTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("session-1");

    // ─────────────────────────────────────────────────────────────────────────────
    // Clause 3: attribution follows the payload, never the "active" conversation.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamEvent_ForNonActiveConversation_IsAttributedToTheConversationInThePayload()
    {
        // The whole point of the issue: an event for conversation B must be attributed to B even
        // while some other conversation is the client's "active" one. The adapter is the last
        // gateway-side hop, so it is where attribution is decided.
        var otherConversation = ConversationId.From("c_the_active_one");
        var eventConversation = ConversationId.From("c_the_real_owner");

        var (adapter, clients, proxy) = CreateAdapter(
            $"conversation:{eventConversation.Value}",
            $"conversation:{otherConversation.Value}");

        var streamEvent = new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            ContentDelta = "chunk",
            SessionId = Session,
            ConversationId = eventConversation
        };

        await adapter.SendStreamEventAsync(
            new ChannelStreamTarget(eventConversation, Session, ChannelAddress.From("addr-1")),
            streamEvent,
            CancellationToken.None);

        clients.Verify(c => c.Group($"conversation:{eventConversation.Value}"), Times.Once);
        clients.Verify(c => c.Group($"conversation:{otherConversation.Value}"), Times.Never,
            "the event must never reach the merely-active conversation's group");
        proxy.Verify(p => p.ContentDelta(It.Is<object>(arg =>
                arg is AgentStreamEvent &&
                ((AgentStreamEvent)arg).ConversationId == eventConversation)),
            Times.Once,
            "the payload must NAME its conversation so the client needs no ambient fallback");
    }

    [Fact]
    public async Task StreamEvent_WithNoConversationId_IsRefused_RatherThanEmittedUnattributed()
    {
        // Sad path. Emitting an unattributed event is what forces the receiver onto its ambient
        // guess, so the honest outcome is to refuse rather than to hand the client a routing
        // decision it cannot make correctly.
        var clients = new Mock<IHubClients<IGatewayHubClient>>(MockBehavior.Strict);
        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
        var adapter = new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object);

        var target = CreateTargetWithoutConversation();
        var streamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "chunk" };

        await adapter.SendStreamEventAsync(target, streamEvent, CancellationToken.None);

        // MockBehavior.Strict: ANY Group() call would throw. Reaching here proves nothing was sent.
        clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never,
            "an event with no conversation id must not be emitted at all");
    }

    [Fact]
    public async Task StreamEvent_TargetSuppliesConversation_WhenEventItselfCarriesNone()
    {
        // Observer fan-out addresses each observer by its own target. The target's conversation is
        // still a RESOLVED id (it comes from session.ConversationId), so this is not a fallback -
        // it is the resolution arriving by the other of two equally authoritative routes.
        var conversation = ConversationId.From("c_from_target");
        var (adapter, clients, proxy) = CreateAdapter($"conversation:{conversation.Value}");

        await adapter.SendStreamEventAsync(
            new ChannelStreamTarget(conversation, Session, ChannelAddress.From("addr-1")),
            new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolName = "read" },
            CancellationToken.None);

        clients.Verify(c => c.Group($"conversation:{conversation.Value}"), Times.Once);
        proxy.Verify(p => p.ToolStart(It.Is<AgentStreamEvent>(e => e.ConversationId == conversation)), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Clause 2: the id on the emitted event comes from the resolution.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SteeringActivity_CarriesTheResolvedConversationId()
    {
        // SteeringInjected/SteeringQueued are conversation-scoped (SteeringSignalRBridge routes
        // them to conversation:{id}) but historically shipped with ConversationId unset, which is
        // exactly how the portal ended up attributing steering feedback by ambient state.
        var resolved = ConversationId.From("c_resolved_by_dispatch");
        var activity = new RecordingActivityBroadcaster();
        var handle = CreateHandle();

        await using var host = CreateHost(handle.Object, activity, resolved, hasInstance: true);
        await host.ProcessAsync(CreateSteerMessage("focus"), CancellationToken.None);

        var injected = activity.Activities
            .Where(a => a.Type == GatewayActivityType.SteeringInjected)
            .ShouldHaveSingleItem();
        injected.ConversationId.ShouldBe(
            resolved.Value,
            "the steering event must name the conversation the dispatcher resolved, not leave it null");
    }

    [Fact]
    public async Task SteeringQueuedActivity_CarriesTheResolvedConversationId()
    {
        // The discard branch is conversation-scoped too: the portal renders a "queued" pill in a
        // specific conversation. Unset there is the same defect with a different event type.
        var resolved = ConversationId.From("c_resolved_by_dispatch");
        var activity = new RecordingActivityBroadcaster();
        var handle = CreateHandle();

        await using var host = CreateHost(handle.Object, activity, resolved, hasInstance: false);
        await host.ProcessAsync(CreateSteerMessage("focus"), CancellationToken.None);

        var queued = activity.Activities
            .Where(a => a.Type == GatewayActivityType.SteeringQueued)
            .ShouldHaveSingleItem();
        queued.ConversationId.ShouldBe(resolved.Value);
    }

    [Fact]
    public async Task SteeringBridge_RefusesToForwardFeedbackWithNoConversationId()
    {
        // Sad path for the bridge. The old code substituted the SESSION id as a conversation group
        // key. No client ever joins "conversation:{sessionId}" - clients subscribe by conversation
        // id - so that send looked delivered while reaching nobody, and the feedback then surfaced
        // in the client's active conversation instead. Refusing makes the drop observable.
        var clients = new Mock<IHubClients<IGatewayHubClient>>(MockBehavior.Strict);
        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        var activity = new ReplayActivityBroadcaster(new GatewayActivity
        {
            Type = GatewayActivityType.SteeringInjected,
            AgentId = Agent.Value,
            SessionId = Session.Value,
            ConversationId = null
        });

        var bridge = new SteeringSignalRBridge(
            activity, hubContext.Object, NullLogger<SteeringSignalRBridge>.Instance);

        await bridge.StartAsync(CancellationToken.None);
        await activity.Completed;
        await bridge.StopAsync(CancellationToken.None);

        clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never,
            "steering feedback with no conversation id must not be forwarded to any group");
    }

    [Fact]
    public async Task SteeringBridge_ForwardsToTheNamedConversationGroup()
    {
        var conversation = ConversationId.From("c_steer_target");
        var proxy = new Mock<IGatewayHubClient>();
        proxy.Setup(p => p.SteeringFeedback(It.IsAny<SteeringFeedbackPayload>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        var activity = new ReplayActivityBroadcaster(new GatewayActivity
        {
            Type = GatewayActivityType.SteeringInjected,
            AgentId = Agent.Value,
            SessionId = Session.Value,
            ConversationId = conversation.Value
        });

        var bridge = new SteeringSignalRBridge(
            activity, hubContext.Object, NullLogger<SteeringSignalRBridge>.Instance);

        await bridge.StartAsync(CancellationToken.None);
        await activity.Completed;
        await bridge.StopAsync(CancellationToken.None);

        clients.Verify(c => c.Group($"conversation:{conversation.Value}"), Times.Once);
        clients.Verify(c => c.Group($"conversation:{Session.Value}"), Times.Never,
            "the session-id-as-conversation-key synonym addressed a group nobody subscribes to");
        proxy.Verify(p => p.SteeringFeedback(
            It.Is<SteeringFeedbackPayload>(pl => pl.ConversationId == conversation.Value)), Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Clause 4: a channel with no conversation obtains one from the gateway first.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChannelWithNoConversation_ObtainsOneFromTheGateway_BeforeEmitting()
    {
        // No dispatcher, no router: the channel genuinely has no conversation. The gateway must
        // mint one server-side rather than emit unattributed and let the client guess.
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var activity = new RecordingActivityBroadcaster();
        var handle = CreateHandle();

        await using var host = CreateHostWithStore(handle.Object, sessions, activity, store);
        await host.ProcessAsync(CreateMessage("hello"), CancellationToken.None);

        var saved = await sessions.GetAsync(Session, CancellationToken.None);
        saved.ShouldNotBeNull();
        saved!.Session.ConversationId.IsInitialized().ShouldBeTrue(
            "a channel with no conversation must obtain one before any conversation-scoped emission");

        var minted = await store.GetAsync(saved.Session.ConversationId, CancellationToken.None);
        minted.ShouldNotBeNull(
            "the conversation must be minted through the store - the same server-side seam POST /conversations uses");
        minted!.AgentId.ShouldBe(Agent);
    }

    [Fact]
    public async Task ResolvedConversation_IsNotReplacedByAMintedOne()
    {
        // The mint must be strictly a last resort. If it ran unconditionally it would silently
        // detach every turn from the conversation the dispatcher already resolved.
        var resolved = ConversationId.From("c_already_resolved");
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var handle = CreateHandle();

        await using var host = CreateHostWithStore(
            handle.Object, sessions, new RecordingActivityBroadcaster(), store, resolved);
        await host.ProcessAsync(CreateMessage("hello"), CancellationToken.None);

        var saved = await sessions.GetAsync(Session, CancellationToken.None);
        saved.ShouldNotBeNull();
        saved!.Session.ConversationId.ShouldBe(
            resolved,
            "the resolution is authoritative; the mint must not fire when a conversation already exists");
        (await store.ListAsync(Agent, CancellationToken.None)).ShouldBeEmpty(
            "no conversation should have been minted on the already-resolved path");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a target whose <see cref="ChannelStreamTarget.ConversationId"/> is the Vogen
    /// uninitialized sentinel - the shape a dropped resolution actually produces. Vogen bans the
    /// <c>default</c> literal (VOG009), so the sentinel is obtained from an unassigned static
    /// field, which is exactly how it leaks through in production.
    /// </summary>
    private static ChannelStreamTarget CreateTargetWithoutConversation()
        => new(s_uninitializedConversationId, Session, ChannelAddress.From("addr-1"));

#pragma warning disable CS0649 // Intentionally unassigned: this IS the uninitialized sentinel.
    private static ConversationId s_uninitializedConversationId;
#pragma warning restore CS0649

    private static (SignalRChannelAdapter Adapter, Mock<IHubClients<IGatewayHubClient>> Clients, Mock<IGatewayHubClient> Proxy)
        CreateAdapter(params string[] groups)
    {
        var proxy = new Mock<IGatewayHubClient>();
        proxy.Setup(p => p.ContentDelta(It.IsAny<object>())).Returns(Task.CompletedTask);
        proxy.Setup(p => p.ToolStart(It.IsAny<AgentStreamEvent>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        foreach (var group in groups)
            clients.Setup(c => c.Group(group)).Returns(proxy.Object);
        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
        return (new SignalRChannelAdapter(NullLogger<SignalRChannelAdapter>.Instance, hubContext.Object), clients, proxy);
    }

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(Agent);
        handle.SetupGet(h => h.SessionId).Returns(Session);
        handle.Setup(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        handle.Setup(h => h.PromptAsync(It.IsAny<BotNexus.Gateway.Abstractions.Models.AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        return handle;
    }

    private static GatewayHost CreateHost(
        IAgentHandle handle,
        IActivityBroadcaster activity,
        ConversationId resolved,
        bool hasInstance)
    {
        var sessions = new InMemorySessionStore();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns(hasInstance
            ? new AgentInstance
            {
                InstanceId = $"{Agent.Value}::{Session.Value}",
                AgentId = Agent,
                SessionId = Session,
                IsolationStrategy = "in-process"
            }
            : null);
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        return BuildHost(supervisor.Object, sessions, activity, conversationStore: null, resolved);
    }

    private static GatewayHost CreateHostWithStore(
        IAgentHandle handle,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IConversationStore store,
        ConversationId? resolved = null)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns((AgentInstance?)null);
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        return BuildHost(supervisor.Object, sessions, activity, store, resolved);
    }

    private static GatewayHost BuildHost(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IConversationStore? conversationStore,
        ConversationId? resolved)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Agent.Value]);

        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns(ChannelKey.From("web"));
        channel.SetupGet(c => c.DisplayName).Returns("web");
        channel.SetupGet(c => c.SupportsStreaming).Returns(false);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([channel.Object]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns(channel.Object);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns(channel.Object);

        IConversationDispatcher? dispatcher = null;
        if (resolved is { } conversationId)
        {
            var mock = new Mock<IConversationDispatcher>();
            mock.Setup(d => d.DispatchAsync(It.IsAny<InboundMessageContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((InboundMessageContext context, CancellationToken _) => new DispatchResult(
                    context,
                    context.Source,
                    new ConversationSessionResolution(conversationId, Session, false, false)));
            dispatcher = mock.Object;
        }

        return new GatewayHost(
            supervisor,
            router.Object,
            sessions,
            activity,
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            sessionQueueCapacity: 64,
            conversationDispatcher: dispatcher,
            conversationStore: conversationStore);
    }

    private static InboundMessage CreateMessage(string content)
        => new()
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From("addr-1"),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, Session.Value, null),
            Metadata = new Dictionary<string, object?>()
        };

    private static InboundMessage CreateSteerMessage(string content)
        => CreateMessage(content) with
        {
            Metadata = new Dictionary<string, object?> { ["control"] = "steer" }
        };

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }

    /// <summary>Replays a fixed activity to the bridge, then signals when it has been consumed.</summary>
    private sealed class ReplayActivityBroadcaster(GatewayActivity activity) : IActivityBroadcaster
    {
        private readonly TaskCompletionSource _consumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _consumed.Task;

        public ValueTask PublishAsync(GatewayActivity item, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return activity;
            // The bridge forwards synchronously after the yield resumes, so signalling on the next
            // MoveNext guarantees the forward attempt has already happened when Completed fires.
            _consumed.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
