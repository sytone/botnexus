using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;
using Shouldly;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Companion coverage for #3600: the defect was not only that a message hung, it was that the hop
/// between <c>Hub SendMessage</c> and <c>GatewayHost.ProcessAsync</c> emitted nothing at all, so
/// three re-sends produced zero log lines and zero user feedback.
/// </summary>
/// <remarks>
/// These tests pin the two observability seams the fix adds:
/// the <see cref="GatewayHubApplicationService"/> boundary log (isolation key + terminal status for
/// every inbound message), and the user-visible channel feedback for a stalled queue.
/// </remarks>
public sealed class InboundBoundaryObservabilityTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    [Fact]
    public async Task Boundary_LogsIsolationKeyAndStatus_OnAcceptedPath()
    {
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        orchestrator
            .AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundDispatchResult(InboundDispatchStatus.Accepted, EmptyDispatches));

        var logger = new CapturingLogger<GatewayHubApplicationService>();
        var service = CreateService(orchestrator, logger);

        var result = await service.AcceptAsync(CreateMessage("addr-ok", sessionId: "sess-ok"));

        result.Status.ShouldBe(InboundDispatchStatus.Accepted);
        logger.Entries.ShouldContain(
            e => e.Message.Contains("session:sess-ok") && e.Message.Contains("Accepted"),
            "every inbound message must record its isolation key and terminal status at the boundary (#3600)");
    }

    [Fact]
    public async Task Boundary_LogsAtWarning_WhenOutcomeIsNotAcceptedOrSteered()
    {
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        orchestrator
            .AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(InboundDispatchResult.Stalled());

        var logger = new CapturingLogger<GatewayHubApplicationService>();
        var service = CreateService(orchestrator, logger);

        var result = await service.AcceptAsync(CreateMessage("addr-stall", sessionId: "sess-stall"));

        result.Status.ShouldBe(InboundDispatchStatus.Stalled);
        logger.Entries.ShouldContain(
            e => e.Level >= LogLevel.Warning
                 && e.Message.Contains("session:sess-stall")
                 && e.Message.Contains("Stalled"),
            "a non-accepted inbound outcome must be loud, not silent (#3600)");
    }

    /// <summary>
    /// A stalled queue must surface to the originating channel. In production the user re-sent three
    /// times because nothing ever came back; a status the transport can read is only half the fix.
    /// </summary>
    [Fact]
    public async Task StalledQueue_SendsUserVisibleChannelFeedback()
    {
        var wedged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                firstStarted.TrySetResult(true);
                await wedged.Task;
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var sent = new List<OutboundMessage>();
        var adapter = Substitute.For<IChannelAdapter>();
        adapter
            .SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci => { lock (sent) { sent.Add(ci.Arg<OutboundMessage>()); } return Task.CompletedTask; });

        var channelManager = Substitute.For<IChannelManager>();
        channelManager.Get(Arg.Any<ChannelKey>()).Returns(adapter);

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            new CapturingLogger<DefaultInboundMessageOrchestrator>(),
            channelManager,
            queueWaitTimeout: TimeSpan.FromMilliseconds(300));

        var head = orchestrator.AcceptAsync(CreateMessage("addr-feedback"));

        // The wedge MUST be released before disposal even when an assertion fails: DisposeAsync
        // awaits every worker task, so a still-wedged worker hangs disposal and takes the whole test
        // project down with it. An earlier revision of these tests did exactly that and stalled CI
        // at its deadline with BotNexus.Gateway.Tests outstanding.
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var second = await orchestrator
                .AcceptAsync(CreateMessage("addr-feedback"))
                .WaitAsync(TimeSpan.FromSeconds(15));

            second.Status.ShouldBe(InboundDispatchStatus.Stalled);
            lock (sent)
            {
                sent.ShouldContain(
                    m => m.Content == DefaultInboundMessageOrchestrator.StalledMessage,
                    "a stalled message must produce user-visible channel feedback (#3600)");
            }
        }
        finally
        {
            wedged.TrySetResult(true);
            try { await head.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* not under test */ }
            try { await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Non-vacuity guard for the bound: the healthy path must never be reported as stalled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the choice of bound.</b> An earlier revision used 50ms to make the point emphatically
    /// and flaked on CI: on a loaded 4-CPU runner the worker occasionally needed longer than that
    /// merely to be SCHEDULED, so a perfectly healthy message was reported <c>Stalled</c>. That was a
    /// defect in the test's timing assumption, not in the fix - but a non-vacuity guard that fails
    /// intermittently is worse than useless, because it trains everyone to ignore a red run.
    /// </para>
    /// <para>
    /// The bound is now 2s: still far below any plausible real stall (the production default is 10s)
    /// and still fails loudly if the timer is ever wired to the processor's own work instead of the
    /// queue wait, yet comfortably above thread-pool scheduling jitter under CI load. The assertion
    /// itself is unchanged and was NOT weakened to go green.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HealthyProcessor_NeverReportsStalled_EvenWithShortBound()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, false));

        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            new CapturingLogger<DefaultInboundMessageOrchestrator>(),
            queueWaitTimeout: TimeSpan.FromSeconds(2));

        for (var i = 0; i < 5; i++)
        {
            var result = await orchestrator
                .AcceptAsync(CreateMessage("addr-fast"))
                .WaitAsync(TimeSpan.FromSeconds(10));
            result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        }
    }

    /// <summary>
    /// The bound applies to the wait for the queue to move, NOT to the processor's own work. A turn
    /// that legitimately runs far longer than the bound must still return its real outcome.
    /// </summary>
    [Fact]
    public async Task LongRunningTurn_IsNotTruncatedByTheBound()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult(true);
                await release.Task;
                return new InboundProcessingOutcome(new[] { CreateDispatchResult() }, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            new CapturingLogger<DefaultInboundMessageOrchestrator>(),
            queueWaitTimeout: TimeSpan.FromMilliseconds(100));

        var accept = orchestrator.AcceptAsync(CreateMessage("addr-long"));

        // Release before disposal in all paths - see the note in StalledQueue_SendsUserVisibleChannelFeedback.
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Deterministic equivalent of "wait past the bound and confirm nothing resolved": if the fix
            // timed the PROCESSOR rather than the queue wait, `accept` would complete as Stalled on its
            // own. Racing it against a bound many multiples of the configured 100ms timeout therefore
            // proves the turn is not truncated, and a TimeoutException here is the PASSING outcome.
            // Task.Delay is banned in tests by TestDelayFlakeFenceTests; WaitAsync is the sanctioned form.
            await Should.ThrowAsync<TimeoutException>(
                async () => await accept.WaitAsync(TimeSpan.FromSeconds(2)),
                "the #3600 bound must not truncate a turn that is genuinely running");

            accept.IsCompleted.ShouldBeFalse(
                "the #3600 bound must not truncate a turn that is genuinely running");

            release.SetResult(true);
            var result = await accept.WaitAsync(TimeSpan.FromSeconds(10));
            result.Status.ShouldBe(InboundDispatchStatus.Accepted);
        }
        finally
        {
            release.TrySetResult(true);
            try { await accept.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* not under test */ }
            try { await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* best effort */ }
        }
    }

    private static GatewayHubApplicationService CreateService(
        IInboundMessageOrchestrator orchestrator,
        ILogger<GatewayHubApplicationService> logger)
        => new(
            orchestrator,
            Substitute.For<ISessionWarmupService>(),
            Substitute.For<IConversationDispatcher>(),
            Substitute.For<ISessionCompactionCoordinator>(),
            resetService: null,
            logger: logger);

    private static InboundMessage CreateMessage(string address, string? sessionId = null)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From(address),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello",
            RoutingHints = sessionId is null
                ? null
                : InboundMessageRoutingHints.LiftFromStrings(null, sessionId, null)
        };

    private static DispatchResult CreateDispatchResult()
    {
        var message = CreateMessage("addr-disp");
        var source = new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId, message.BindingId);
        var context = new InboundMessageContext(AgentId.From("agent-1"), message, source);
        var resolution = new ConversationSessionResolution(
            ConversationId.From("c_1"),
            SessionId.From("s_1"),
            IsNewConversation: true,
            IsNewSession: true,
            OriginatingBindingId: null,
            DisplayPrefix: null);
        return new DispatchResult(context, source, resolution);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) { return _entries.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
