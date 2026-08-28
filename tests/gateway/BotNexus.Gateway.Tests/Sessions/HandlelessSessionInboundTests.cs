using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #3609 (clause 7 of #3600): an inbound message addressed to a session whose agent handle no
/// longer exists must either rehydrate the handle or fail loudly - never be silently accepted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The traced finding these tests pin.</b> The agent handle is an in-memory cache in
/// <c>DefaultAgentSupervisor._instances</c>. <c>SessionCompactionCoordinator.CompactAsync</c>
/// evicts it via <c>IAgentSupervisor.StopAsync</c> after every applied compaction, and
/// <c>DefaultAgentSupervisor.StopAsync</c> makes NO <c>ISessionStore</c> call - so the durable
/// store legitimately still reports <c>sessions.status = Active</c> with a live
/// <c>conversations.active_session_id</c>. That durable state is not an inconsistency: it is the
/// input the rebuild reads, because <c>GetOrCreateAsync</c> -> <c>CreateEntryAsync</c> seeds the
/// new handle from <c>existingSession.History</c>.
/// </para>
/// <para>
/// So the two inbound paths diverge, and that divergence is the defect. The DATA path
/// (<c>GatewayHost.ProcessAsync</c>) already rehydrates - AC1 pins that so a future refactor
/// cannot regress it to a <c>GetHandle</c> lookup. The CONTROL path (<c>HandleSteeringAsync</c>)
/// gates on <c>GetInstance</c> and, finding none, discarded the message with a
/// <c>LogInformation</c> and no channel output: a silent accept, and the exact
/// "permanently write-only conversation" symptom.
/// </para>
/// <para>
/// <b>Why the control path fails loudly rather than rehydrating (AC2's permitted branch).</b>
/// Minting a fresh idle handle here and steering into it would be a WORSE silent loss:
/// <c>DefaultInboundDeliveryResolver</c> documents that a steer injected into an idle agent lands
/// in a <c>PendingMessageQueue</c> that nothing will ever drain, because the loop that would read
/// it has already ended. The discard is therefore correct; only its silence was not.
/// </para>
/// </remarks>
public sealed class HandlelessSessionInboundTests
{
    private static readonly AgentId Agent = AgentId.From("agent-a");
    private static readonly SessionId Session = SessionId.From("session-1");

    /// <summary>
    /// AC1: an inbound DATA message addressed to a session whose handle has been disposed
    /// (compaction evicted it) rehydrates the handle and processes the message.
    /// </summary>
    [Fact]
    public async Task InboundMessage_AfterHandleEviction_RehydratesHandleAndProcessesMessage()
    {
        // Arrange: model the post-compaction world exactly. GetHandle returns null (the cache was
        // evicted by SessionCompactionCoordinator step 4) while the store still reports Active
        // with history intact. GetOrCreateAsync is the rehydration seam.
        var handle = CreateHandle();
        var rehydrated = 0;
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(Agent, Session)).Returns((IAgentHandle?)null);
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns((AgentInstance?)null);
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object)
            .Callback(() => rehydrated++);

        var session = CreateActiveSessionWithHistory();
        var sessions = CreateSessionStore(session);
        var channel = CreateChannel();
        await using var host = CreateHost(supervisor.Object, sessions.Object, new RecordingActivityBroadcaster(), channel.Object);

        // Act
        await host.ProcessAsync(CreateMessage("still there?"), CancellationToken.None);

        // Assert: the handle was rebuilt exactly once through the create seam...
        rehydrated.ShouldBe(1,
            "an inbound message to a handle-less but Active session must rehydrate via GetOrCreateAsync (#3609 AC1)");
        // ...and the message was genuinely processed, not merely accepted.
        handle.Verify(h => h.PromptAsync(
            It.Is<AgentUserMessage>(m => m.Content == "still there?"), It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "rehydrated-reply"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// AC2: a CONTROL (steer) message to a handle-less session is not silently accepted. It
    /// produces a user-visible error through the originating channel AND a log line at Warning or
    /// above naming the session id.
    /// </summary>
    [Fact]
    public async Task SteerMessage_WithNoHandle_ReportsUserVisibleErrorAndLogsWarningNamingSession()
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns((AgentInstance?)null);
        supervisor.Setup(s => s.GetHandle(Agent, Session)).Returns((IAgentHandle?)null);

        var session = CreateActiveSessionWithHistory();
        var sessions = CreateSessionStore(session);
        var channel = CreateChannel();
        var activity = new RecordingActivityBroadcaster();
        var logger = new RecordingLogger();
        await using var host = CreateHost(supervisor.Object, sessions.Object, activity, channel.Object, logger);

        // Act
        await host.ProcessAsync(CreateSteerMessage("keep going"), CancellationToken.None);

        // Assert 1 - user-visible through the channel. This is the clause that fails on main:
        // the discard previously emitted nothing the user could see.
        var sent = channel.Invocations
            .Where(i => i.Method.Name == nameof(IChannelAdapter.SendAsync))
            .Select(i => (OutboundMessage)i.Arguments[0])
            .ToList();
        sent.ShouldNotBeEmpty("a discarded steer must surface a user-visible error, never a silent accept (#3609 AC2)");
        sent.ShouldContain(m => m.Content.Contains("could not be delivered", StringComparison.OrdinalIgnoreCase),
            "the channel message must state the steer was not delivered");

        // Assert 2 - a log line at Warning or above that NAMES the session id.
        var warnings = logger.Records
            .Where(r => r.Level >= LogLevel.Warning && r.Message.Contains(Session.Value, StringComparison.Ordinal))
            .ToList();
        warnings.ShouldNotBeEmpty(
            $"a discarded steer must log at Warning or above naming session '{Session.Value}' (#3609 AC2)");

        // Assert 3 - an Error activity so portal/observability surfaces see it too.
        activity.Activities.ShouldContain(a => a.Type == GatewayActivityType.Error && a.SessionId == Session.Value);
    }

    /// <summary>
    /// AC3: handle eviction never mutates the store. The observed pairing
    /// (<c>status = Active</c> + live <c>active_session_id</c> + no handle) is REACHED by design
    /// and is precisely the state that drives rehydration, so the correct assertion is that the
    /// eviction leaves the durable row untouched and a subsequent inbound message rebuilds from
    /// it - not that the state is cleared.
    /// </summary>
    [Fact]
    public async Task HandleEviction_LeavesStoreStateIntactAndThatStateDrivesRehydration()
    {
        var handle = CreateHandle();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(Agent, Session)).Returns((IAgentHandle?)null);
        supervisor.Setup(s => s.GetInstance(Agent, Session)).Returns((AgentInstance?)null);
        supervisor.Setup(s => s.GetOrCreateAsync(Agent, Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = CreateActiveSessionWithHistory();
        var conversationId = session.ConversationId;
        var sessions = CreateSessionStore(session);
        await using var host = CreateHost(
            supervisor.Object, sessions.Object, new RecordingActivityBroadcaster(), CreateChannel().Object);

        // Precondition: this IS the state the issue reported as inconsistent.
        session.Status.ShouldBe(SessionStatus.Active);
        session.ConversationId.ShouldBe(conversationId);
        supervisor.Object.GetHandle(Agent, Session).ShouldBeNull();

        await host.ProcessAsync(CreateMessage("resume"), CancellationToken.None);

        // The state was neither cleared nor invalidated by the handle-less arrival...
        session.Status.ShouldBe(SessionStatus.Active,
            "handle eviction is a cache drop and must not demote the durable session status (#3609 AC3)");
        session.ConversationId.ShouldBe(conversationId,
            "the conversation binding must survive handle eviction so the reply routes back (#3609 AC3)");
        // ...and it drove a rebuild rather than being left write-only.
        supervisor.Verify(s => s.GetOrCreateAsync(Agent, Session, It.IsAny<CancellationToken>()), Times.Once);
        handle.Verify(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC4: <c>AgentsController.GetContext</c> returning 404 for a session the store reports as
    /// <c>Active</c> is documented as an expected transient, and the response itself states the
    /// distinguishing condition. Asserted by DRIVING the real action with a supervisor that has no
    /// handle - the exact post-compaction shape - so the guarantee is pinned at the observable HTTP
    /// boundary rather than in prose a caller never sees.
    /// </summary>
    [Fact]
    public void GetContext_WithNoHandle_Returns404DocumentingTheTransientAndItsCondition()
    {
        // A supervisor with no resident handle. This is NOT a broken session: the store row is
        // still Active, and this same pair rehydrates on the next inbound message (see AC1).
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetHandle(Agent, Session)).Returns((IAgentHandle?)null);

        var controller = new AgentsController(
            Mock.Of<IAgentRegistry>(),
            supervisor.Object,
            Mock.Of<IAgentConfigurationWriter>());

        var result = controller.GetContext(Agent.Value, Session.Value);

        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        var body = notFound.Value.ShouldBeOfType<string>();

        // It is named as a transient, so an operator does not read it as corruption...
        body.ShouldContain("transient", Case.Insensitive,
            "the 404 must declare itself an expected transient (#3609 AC4)");
        // ...and the distinguishing condition - cache vs durable store - is stated, which is what
        // separates this 404 from a genuinely absent session.
        body.ShouldContain("cache", Case.Insensitive,
            "the body must state the handle is an in-memory cache: that is the distinguishing condition (#3609 AC4)");
        body.ShouldContain("Active", Case.Sensitive,
            "the body must explain why the session row remains Active (#3609 AC4)");
        body.ShouldContain("GetOrCreateAsync", Case.Sensitive,
            "the body must name the seam that rebuilds the handle on the next message (#3609 AC4)");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static Mock<IAgentHandle> CreateHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(Agent);
        handle.SetupGet(h => h.SessionId).Returns(Session);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.TryFollowUpWhileRunningAsync(
                It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "rehydrated-reply" });
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "rehydrated-reply" });
        return handle;
    }

    /// <summary>
    /// A session in exactly the state the issue reported: Active, bound to a conversation, with
    /// prior history that a rebuilt handle would be seeded from.
    /// </summary>
    private static GatewaySession CreateActiveSessionWithHistory()
    {
        var session = new GatewaySession
        {
            SessionId = Session,
            AgentId = Agent,
            Status = SessionStatus.Active,
            ConversationId = ConversationId.From("conv-3609")
        };
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "four days of work" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "acknowledged" });
        return session;
    }

    private static Mock<ISessionStore> CreateSessionStore(GatewaySession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(Session, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.GetOrCreateAsync(Session, Agent, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static Mock<IChannelAdapter> CreateChannel()
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns("web");
        channel.SetupGet(c => c.DisplayName).Returns("web");
        // Non-streaming so ProcessAsync takes the PromptAsync path, keeping the assertion on
        // "the message was processed" a single deterministic call rather than a stream drain.
        channel.SetupGet(c => c.SupportsStreaming).Returns(false);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    private static GatewayHost CreateHost(
        IAgentSupervisor supervisor,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelAdapter channel,
        ILogger<GatewayHost>? logger = null)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Agent.Value]);

        var channelManager = new Mock<IChannelManager>();
        channelManager.SetupGet(m => m.Adapters).Returns([channel]);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns(channel);
        channelManager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns(channel);

        return new GatewayHost(
            supervisor,
            router.Object,
            sessions,
            activity,
            channelManager.Object,
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            logger ?? NullLogger<GatewayHost>.Instance,
            sessionQueueCapacity: 64);
    }

    private static InboundMessage CreateMessage(string content) => new()
    {
        ChannelType = "web",
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        ChannelAddress = ChannelAddress.From("conv-1"),
        Content = content,
        StreamResponse = false,
        RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, Session.Value, null),
        Metadata = new Dictionary<string, object?>()
    };

    private static InboundMessage CreateSteerMessage(string content) => new()
    {
        ChannelType = "web",
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        ChannelAddress = ChannelAddress.From("conv-1"),
        Content = content,
        StreamResponse = false,
        RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, Session.Value, null),
        Metadata = new Dictionary<string, object?> { ["control"] = "steer" }
    };

    private sealed record LogRecord(LogLevel Level, string Message);

    /// <summary>
    /// Captures level + rendered message so a test can assert BOTH that a line was emitted at
    /// Warning or above AND that it names the session id - the two halves AC2 requires.
    /// </summary>
    private sealed class RecordingLogger : ILogger<GatewayHost>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NoopScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add(new LogRecord(logLevel, formatter(state, exception)));

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
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

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // No test in this file subscribes; the stub only has to satisfy the interface. An
            // INFINITE wait ended by cancellation is the shape the test-delay flake fence
            // (TestDelayFlakeFenceTests) permits (a sentinel), whereas a finite Task.Delay poll loop is a wall-clock wait it
            // rejects. Assertions here read the Activities list directly, so nothing depends on
            // this ever yielding.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the subscription ends when the caller cancels.
            }

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
