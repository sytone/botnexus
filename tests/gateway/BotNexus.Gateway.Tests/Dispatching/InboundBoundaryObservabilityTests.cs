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
    /// <b>Why there is no wall-clock race here (#3854).</b> Earlier revisions made a DURATION the
    /// arbiter of this ordering property. A 50ms bound flaked because a loaded 4-CPU runner needed
    /// longer than that merely to SCHEDULE the worker, so a perfectly healthy message was reported
    /// <c>Stalled</c>; the bound was raised to 2s, which lowered the flake rate without removing the
    /// race. It duly failed again on run <c>33833181660</c> (PR #3852) with
    /// "should be NoRoute but was Stalled", on a diff confined to <c>BotNexus.Cli</c> that cannot
    /// reach this orchestrator at all. Raising a constant never fixes a race; it only moves it.
    /// </para>
    /// <para>
    /// <b>Phase A - the healthy accepts.</b> All five accepts are retained and each must still be
    /// <see cref="InboundDispatchStatus.NoRoute"/>; the assertion is NOT weakened. What is removed is
    /// the timing arbiter: the queue-wait bound is set far above any scheduling jitter, so thread-pool
    /// starvation can no longer manufacture a <c>Stalled</c> verdict for a healthy message. On its own
    /// this phase would be deterministic but VACUOUS - a mis-wired timer would sail straight through
    /// it, because a healthy processor returns before any bound could bite.
    /// </para>
    /// <para>
    /// <b>Phase B - non-vacuity, restored by signal instead of by duration.</b> Detecting a timer
    /// mis-wired to the processor's own work requires a processor that outlasts the bound. Expressing
    /// "outlasts" as a sleep would reintroduce exactly the race this issue is about, so the duration is
    /// replaced by an observation: a second message queued behind the wedged head returns
    /// <c>Stalled</c>, and that status is emitted ONLY after <c>_queueWaitTimeout</c> has expired
    /// inside <c>WaitForProcessingStartAsync</c>. Receiving it is proof - on the orchestrator's clock,
    /// not the test's - that the bound has fired while the head was still running. The head is then
    /// released and must still report its real <c>NoRoute</c> outcome. Wiring the bound to the
    /// processor's own work (<c>Completion.Task.WaitAsync(_queueWaitTimeout, ...)</c>) turns this red,
    /// because the head is then truncated instead of returning its outcome. A slow runner can only
    /// delay the stall verdict; it cannot invert it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HealthyProcessor_NeverReportsStalled_EvenWithShortBound()
    {
        // Phase A: five healthy accepts, with no duration acting as arbiter.
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(EmptyDispatches, false));

        await using (var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            new CapturingLogger<DefaultInboundMessageOrchestrator>(),
            queueWaitTimeout: HealthyPathQueueWaitBound))
        {
            for (var i = 0; i < 5; i++)
            {
                // The WaitAsync here is a deadlock fuse, not the assertion: it can only turn a hang
                // into a failure, never a pass into a failure. Task.Delay is banned by
                // TestDelayFlakeFenceTests; WaitAsync is the sanctioned form.
                var result = await orchestrator
                    .AcceptAsync(CreateMessage("addr-fast"))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                result.Status.ShouldBe(
                    InboundDispatchStatus.NoRoute,
                    $"accept {i}: a healthy turn must never be reported as stalled");
            }
        }

        // Phase B: non-vacuity. Proves the bound is wired to the queue wait, not the processor's work.
        await AssertHealthyTurnSurvivesAnElapsedBoundAsync();
    }

    /// <summary>
    /// The queue-wait bound used for the healthy accepts. Chosen to be unreachable by thread-pool
    /// scheduling jitter rather than to be "short": the non-vacuity of this test comes from
    /// <see cref="AssertHealthyTurnSurvivesAnElapsedBoundAsync"/>, which observes the bound elapsing
    /// via the orchestrator's own <see cref="InboundDispatchStatus.Stalled"/> verdict, so nothing is
    /// lost by making the healthy path immune to runner load (#3854).
    /// </summary>
    private static readonly TimeSpan HealthyPathQueueWaitBound = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Non-vacuity half of <see cref="HealthyProcessor_NeverReportsStalled_EvenWithShortBound"/>: a
    /// healthy turn whose processing outlasts the queue-wait bound must still return its real outcome.
    /// The bound is proven to have elapsed by signal (a queued-behind message reported
    /// <see cref="InboundDispatchStatus.Stalled"/>), never by a measured duration.
    /// </summary>
    private static async Task AssertHealthyTurnSurvivesAnElapsedBoundAsync()
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
                return new InboundProcessingOutcome(EmptyDispatches, false);
            });

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            new CapturingLogger<DefaultInboundMessageOrchestrator>(),
            queueWaitTimeout: TimeSpan.FromMilliseconds(100));

        var head = orchestrator.AcceptAsync(CreateMessage("addr-healthy-long"));

        // Release before disposal on every path - a still-wedged worker hangs DisposeAsync.
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var behind = await orchestrator
                .AcceptAsync(CreateMessage("addr-healthy-long"))
                .WaitAsync(TimeSpan.FromSeconds(30));

            behind.Status.ShouldBe(
                InboundDispatchStatus.Stalled,
                "the message behind a wedged head must hit the #3600 queue-wait bound; that verdict is " +
                "what proves the bound has elapsed without the test measuring time itself");

            head.IsCompleted.ShouldBeFalse(
                "the queue-wait bound has demonstrably elapsed, yet it must not truncate the running turn");

            release.SetResult(true);

            var result = await head.WaitAsync(TimeSpan.FromSeconds(30));
            result.Status.ShouldBe(
                InboundDispatchStatus.NoRoute,
                "a healthy turn that outlasts the queue-wait bound must still report its real outcome, " +
                "never Stalled - this is what fails if the timer is wired to the processor's own work");
        }
        finally
        {
            release.TrySetResult(true);
            try { await head.WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* not under test */ }
            try { await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The bound applies to the wait for the queue to move, NOT to the processor's own work. A turn
    /// that legitimately runs far longer than the bound must still return its real outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there is no wall-clock race here (#3849).</b> The original revision proved
    /// non-truncation by racing the head's accept task against a fixed 2-second <c>WaitAsync</c> and
    /// requiring a <see cref="TimeoutException"/>. That made a scheduling duration the arbiter of a
    /// correctness property, and on a cold or loaded runner it inverted the verdict: run
    /// <c>33804982935</c> on PR #3830 went red with "should throw System.TimeoutException but did
    /// not" on a diff that cannot reach the inbound dispatching path at all.
    /// </para>
    /// <para>
    /// The replacement takes its signal from the orchestrator's OWN clock rather than the test's. A
    /// second message is queued behind the wedged head on the same isolation key; when the
    /// orchestrator returns <see cref="InboundDispatchStatus.Stalled"/> for it, the #3600 queue-wait
    /// bound has DEMONSTRABLY elapsed - that status is emitted only after <c>_queueWaitTimeout</c>
    /// expires inside <c>WaitForProcessingStartAsync</c>. At that instant the head's accept task must
    /// still be incomplete. No duration the test picks can change that verdict: a slow runner merely
    /// delays the stall verdict, it cannot make the bound truncate a started turn.
    /// </para>
    /// <para>
    /// <b>Assertion strength is unchanged or stronger.</b> The test still asserts the accept task is
    /// incomplete while the processor runs, and additionally asserts it is neither faulted nor
    /// cancelled, then that it resolves <c>Accepted</c> with its real dispatch payload after the
    /// processor is released. Mutating <c>AcceptAsync</c> to apply the bound to the post-<c>Started</c>
    /// await (<c>Completion.Task.WaitAsync(_queueWaitTimeout, cancellationToken)</c>) turns this red,
    /// which is what the strengthened incompleteness assertion is for.
    /// </para>
    /// <para>
    /// The scenario runs <see cref="NonTruncationIterations"/> times in a single <c>[Fact]</c> with
    /// the iteration index in every failure message, per the #3617 precedent, so an ordering-sensitive
    /// regression cannot hide behind a single lucky pass.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LongRunningTurn_IsNotTruncatedByTheBound()
    {
        for (var iteration = 0; iteration < NonTruncationIterations; iteration++)
        {
            await AssertLongRunningTurnIsNotTruncatedAsync(iteration);
        }
    }

    /// <summary>
    /// Number of times <see cref="LongRunningTurn_IsNotTruncatedByTheBound"/> repeats its scenario
    /// inside the one fact. Each iteration is fully deterministic, so this is a guard against an
    /// ordering-sensitive regression rather than a probabilistic sweep.
    /// </summary>
    private const int NonTruncationIterations = 25;

    private static async Task AssertLongRunningTurnIsNotTruncatedAsync(int iteration)
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

        // Both messages carry the same address and therefore the same isolation key, so the second is
        // genuinely queued behind the first.
        var accept = orchestrator.AcceptAsync(CreateMessage("addr-long"));

        // Release before disposal in all paths - see the note in StalledQueue_SendsUserVisibleChannelFeedback.
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The orchestrator's own bound, observed rather than timed. Stalled is returned ONLY after
            // _queueWaitTimeout has elapsed inside WaitForProcessingStartAsync, so receiving it is
            // proof - on the orchestrator's clock, not the test's - that the #3600 bound has fired
            // while the head turn was still running. The generous WaitAsync here is a deadlock fuse,
            // not the assertion: it can only ever turn a hang into a failure, never a pass into a
            // failure. Task.Delay is banned in tests by TestDelayFlakeFenceTests.
            var behind = await orchestrator
                .AcceptAsync(CreateMessage("addr-long"))
                .WaitAsync(TimeSpan.FromSeconds(30));

            behind.Status.ShouldBe(
                InboundDispatchStatus.Stalled,
                $"iteration {iteration}: the message queued behind a running turn must hit the #3600 " +
                "queue-wait bound, which is what makes this a proof that the bound has elapsed");

            accept.IsCompleted.ShouldBeFalse(
                $"iteration {iteration}: the #3600 bound has demonstrably elapsed (the message behind " +
                "this one was just reported Stalled) and yet it must not truncate a turn that is " +
                "genuinely running");

            accept.IsFaulted.ShouldBeFalse($"iteration {iteration}: a running turn must not be faulted by the bound");
            accept.IsCanceled.ShouldBeFalse($"iteration {iteration}: a running turn must not be cancelled by the bound");

            release.SetResult(true);
            var result = await accept.WaitAsync(TimeSpan.FromSeconds(30));
            result.Status.ShouldBe(
                InboundDispatchStatus.Accepted,
                $"iteration {iteration}: the long-running turn must still return its real outcome");
            result.Dispatches.Count.ShouldBe(
                1,
                $"iteration {iteration}: the turn's real dispatch payload must survive the bound");
        }
        finally
        {
            release.TrySetResult(true);
            try { await accept.WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* not under test */ }
            try { await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
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
