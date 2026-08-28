using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Regression coverage for #3600: an inbound message accepted by the transport
/// vanished with no response, no error, and no log line, permanently killing the
/// conversation until the whole gateway process was restarted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause.</b> <see cref="DefaultInboundMessageOrchestrator"/> serialises one
/// isolation unit through a single-reader channel whose worker awaits
/// <c>IInboundMessageProcessor.ProcessAsync</c> with no upper time bound. If that call
/// never returns — the production case was a turn wedged immediately after a 602k-token
/// compaction — the worker never loops, so every later message for the same conversation
/// sits unread in the channel. <c>TryWrite</c> still succeeds (capacity 64), so the
/// transport reports success, while <c>queueItem.Completion.Task</c> is awaited forever.
/// Nothing throws, so nothing is logged: the message is unobservable between accept and
/// processing.
/// </para>
/// <para>
/// <b>Why the existing suite missed it.</b> Every pre-#3600 test supplied a processor that
/// returns promptly, so the head of the queue always advanced. The stall state — head
/// in-flight indefinitely while successors accumulate — had no coverage at all.
/// </para>
/// <para>
/// <b>Test construction notes.</b> Two constraints shape every test here. First, the
/// assertions target the OBSERVABLE contract (a bounded outcome, a diagnostic) and never
/// internal queue fields, so they keep their meaning whether the fix is a timeout, a
/// watchdog, or a rewritten worker. Second, each test releases its wedged processor in a
/// <c>finally</c> BEFORE disposing the orchestrator: <c>DisposeAsync</c> awaits every
/// worker task, so a still-wedged worker would hang disposal and take the whole test
/// project down with it — which is exactly what happened on the first run of this file,
/// stalling the CI runner at its deadline with <c>BotNexus.Gateway.Tests</c> outstanding.
/// </para>
/// <para>
/// Waits use <c>Task.WaitAsync(TimeSpan)</c>, which fails deterministically with
/// <see cref="TimeoutException"/>. <c>Task.Delay</c> races are banned by
/// <c>TestDelayFlakeFenceTests</c> and must not be reintroduced here.
/// </para>
/// </remarks>
public sealed class InboundQueueStallRegressionTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    /// <summary>Bounded window in which a correct implementation must resolve a queued message.</summary>
    private static readonly TimeSpan StallBudget = TimeSpan.FromSeconds(15);

    /// <summary>Window for signals a healthy implementation raises almost immediately.</summary>
    private static readonly TimeSpan SignalBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The core #3600 repro. With the head of the queue wedged, a second message on the
    /// same isolation unit must still reach a definite outcome in bounded time. Pre-fix
    /// this never resolves and the test fails with a <see cref="TimeoutException"/>.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_WhenHeadOfQueueIsWedged_SecondMessageStillCompletes()
    {
        using var fixture = new WedgedProcessorFixture();

        var head = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-stall"));
        await fixture.WaitForProcessorEntryAsync();

        // The user re-sending after no reply. It must not vanish into an unbounded await.
        var resend = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-stall"));

        try
        {
            await Should.NotThrowAsync(
                async () =>
                {
                    try
                    {
                        await resend.WaitAsync(StallBudget);
                    }
                    catch (TimeoutException)
                    {
                        throw new ShouldAssertException(
                            "A message accepted onto a stalled queue must reach a definite outcome " +
                            $"within {StallBudget.TotalSeconds:0}s, not hang indefinitely (#3600).");
                    }
                    catch (Exception)
                    {
                        // A thrown dispatch outcome is still a definite outcome; only the
                        // absence of any outcome is the #3600 defect.
                    }
                });
        }
        finally
        {
            await fixture.ReleaseAndDrainAsync(head);
        }
    }

    /// <summary>
    /// A message the processor never handled must not be reported to the caller as
    /// <see cref="InboundDispatchStatus.Accepted"/>. Silent success is what made the
    /// production failure invisible: the user had no reason to suspect a fault.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_WhenQueueStalled_DoesNotReportAcceptedForUnprocessedMessage()
    {
        using var fixture = new WedgedProcessorFixture();

        var head = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-status"));
        await fixture.WaitForProcessorEntryAsync();

        var resend = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-status"));

        try
        {
            InboundDispatchResult? result = null;
            try
            {
                result = await resend.WaitAsync(StallBudget);
            }
            catch (TimeoutException)
            {
                throw new ShouldAssertException(
                    "The queued message never resolved, so its reported status could not be " +
                    "observed at all (#3600).");
            }
            catch (Exception)
            {
                // Threw rather than returned: not a silent false success, which is the concern.
                return;
            }

            result!.Status.ShouldNotBe(
                InboundDispatchStatus.Accepted,
                "a message the processor never handled must not be reported as Accepted (#3600)");
        }
        finally
        {
            await fixture.ReleaseAndDrainAsync(head);
        }
    }

    /// <summary>
    /// The defining property of #3600 was unobservability: three re-sends produced zero log
    /// lines beyond the transport's own accept, leaving an operator nothing to search for.
    /// A stalled or refused inbound message must leave a trail at Warning or above.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_WhenQueueStalled_EmitsDiagnostic()
    {
        var logger = new CapturingLogger();
        using var fixture = new WedgedProcessorFixture(logger);

        var head = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-log"));
        await fixture.WaitForProcessorEntryAsync();

        var resend = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-log"));

        try
        {
            try
            {
                await resend.WaitAsync(StallBudget);
            }
            catch (TimeoutException)
            {
                throw new ShouldAssertException(
                    "The queued message never resolved, so no diagnostic could follow it (#3600).");
            }
            catch (Exception)
            {
                // Outcome shape is asserted elsewhere; this test only cares about observability.
            }

            logger.Entries
                .Any(entry => entry.Level >= LogLevel.Warning)
                .ShouldBeTrue(
                    "A stalled inbound message must be observable in the logs. #3600 shipped " +
                    "precisely because this path was completely silent.");
        }
        finally
        {
            await fixture.ReleaseAndDrainAsync(head);
        }
    }

    /// <summary>
    /// Every message queued behind a wedged head must resolve, not just the first. The
    /// production user re-sent three times; a fix that rescued only the next message in
    /// line would still lose the rest.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_MultipleMessagesBehindWedgedHead_AllComplete()
    {
        using var fixture = new WedgedProcessorFixture();

        var head = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-multi"));
        await fixture.WaitForProcessorEntryAsync();

        var resend1 = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-multi"));
        var resend2 = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-multi"));

        try
        {
            var settled = Task.WhenAll(Settle(resend1), Settle(resend2));
            try
            {
                await settled.WaitAsync(StallBudget);
            }
            catch (TimeoutException)
            {
                throw new ShouldAssertException(
                    "Every message queued behind a wedged head must reach an outcome; at least " +
                    $"one was still unresolved after {StallBudget.TotalSeconds:0}s (#3600).");
            }
        }
        finally
        {
            await fixture.ReleaseAndDrainAsync(head);
        }
    }

    /// <summary>
    /// Non-vacuity guard. Without this, a "fix" that refused or delayed every message would
    /// satisfy all the stall assertions above while destroying normal operation.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_HealthyProcessor_StillAcceptedPromptly()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new InboundProcessingOutcome(new[] { CreateDispatchResult() }, false));

        var orchestrator = new DefaultInboundMessageOrchestrator(
            processor,
            NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await orchestrator
                .AcceptAsync(CreateMessage("addr-healthy"))
                .WaitAsync(SignalBudget);
            stopwatch.Stop();

            result.Status.ShouldBe(InboundDispatchStatus.Accepted);
            stopwatch.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(5),
                "the #3600 fix must not add latency to the healthy path");
        }
        finally
        {
            await orchestrator.DisposeAsync();
        }
    }

    /// <summary>
    /// A wedged conversation must not affect any other. #3600 killed one conversation; the
    /// fix must not generalise that failure across isolation units.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_WedgedIsolationUnit_DoesNotBlockOtherUnits()
    {
        using var fixture = new WedgedProcessorFixture(
            processorForAddress: address => !address.Contains("healthy", StringComparison.Ordinal));

        var wedgedHead = fixture.Orchestrator.AcceptAsync(CreateMessage("addr-wedged"));
        await fixture.WaitForProcessorEntryAsync();

        try
        {
            var healthy = await fixture.Orchestrator
                .AcceptAsync(CreateMessage("addr-healthy-unit"))
                .WaitAsync(SignalBudget);

            healthy.Status.ShouldBe(
                InboundDispatchStatus.NoRoute,
                "an unrelated isolation unit must process normally while another is wedged");
        }
        finally
        {
            await fixture.ReleaseAndDrainAsync(wedgedHead);
        }
    }

    private static async Task Settle(Task<InboundDispatchResult> task)
    {
        try { await task; } catch { /* reaching any outcome is the assertion */ }
    }

    /// <summary>
    /// Owns a processor whose first call blocks indefinitely, plus the orchestrator under
    /// test. Guarantees the wedge is released before disposal so a failing assertion can
    /// never hang the worker and stall the whole test project.
    /// </summary>
    private sealed class WedgedProcessorFixture : IDisposable
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WedgedProcessorFixture(
            ILogger<DefaultInboundMessageOrchestrator>? logger = null,
            Func<string, bool>? processorForAddress = null)
        {
            var processor = Substitute.For<IInboundMessageProcessor>();
            processor
                .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var message = callInfo.Arg<InboundMessage>();
                    var shouldWedge = processorForAddress is null
                        || processorForAddress(message.ChannelAddress.Value);

                    if (!shouldWedge)
                    {
                        return new InboundProcessingOutcome(EmptyDispatches, false);
                    }

                    _entered.TrySetResult(true);
                    await _release.Task;
                    return new InboundProcessingOutcome(EmptyDispatches, false);
                });

            Orchestrator = new DefaultInboundMessageOrchestrator(
                processor,
                logger ?? NullLogger<DefaultInboundMessageOrchestrator>.Instance);
        }

        public DefaultInboundMessageOrchestrator Orchestrator { get; }

        /// <summary>Completes once the wedged processor call is actually in flight.</summary>
        public Task WaitForProcessorEntryAsync() => _entered.Task.WaitAsync(SignalBudget);

        /// <summary>
        /// Releases the wedge and drains the orchestrator. Must run before disposal —
        /// <c>DisposeAsync</c> awaits worker tasks and would otherwise never return.
        /// </summary>
        public async Task ReleaseAndDrainAsync(Task<InboundDispatchResult> head)
        {
            _release.TrySetResult(true);
            try { await head.WaitAsync(SignalBudget); } catch { /* outcome not under test */ }
            try { await Orchestrator.DisposeAsync().AsTask().WaitAsync(SignalBudget); } catch { /* best effort */ }
        }

        public void Dispose() => _release.TrySetResult(true);
    }

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

    private sealed class CapturingLogger : ILogger<DefaultInboundMessageOrchestrator>
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
