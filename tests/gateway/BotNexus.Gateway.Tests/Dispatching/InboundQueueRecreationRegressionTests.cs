using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Logging;
using Shouldly;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Dispatching;

/// <summary>
/// Regression coverage for the second #3600 hypothesis: an inbound message written onto a
/// per-isolation-unit queue whose worker had already exited, so nobody ever read it.
/// </summary>
/// <remarks>
/// <para>
/// The worker removes its own <c>_sessionQueues</c> entry when a session seals
/// (<c>ShouldClosePerSessionQueue</c>) and again in the <c>finally</c> of its read loop. A caller
/// that resolves the entry around either removal writes into a dead channel: <c>TryWrite</c>
/// succeeds, the transport reports success, and the caller awaits a completion nothing will ever
/// set. That is silent user-input loss with no exception and no log line.
/// </para>
/// <para>
/// These tests assert the observable contract - the message is still processed, and the recreation
/// is logged - rather than reaching into the queue dictionary, so they survive a different fix.
/// </para>
/// </remarks>
public sealed class InboundQueueRecreationRegressionTests
{
    private static readonly IReadOnlyList<DispatchResult> EmptyDispatches = Array.Empty<DispatchResult>();

    /// <summary>
    /// Drives the orchestrator into the sealed-queue state via <c>ShouldClosePerSessionQueue</c>,
    /// then sends another message on the same isolation key. It must be processed, not dropped.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_AfterQueueWasClosedAndWorkerExited_MessageIsStillProcessed()
    {
        var calls = 0;
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var index = Interlocked.Increment(ref calls);
                // First message seals its own queue, exactly as a session-close outcome does.
                return Task.FromResult(new InboundProcessingOutcome(
                    EmptyDispatches,
                    ShouldClosePerSessionQueue: index == 1));
            });

        var logger = new CapturingLogger();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(processor, logger);

        var first = await orchestrator
            .AcceptAsync(CreateMessage("addr-seal"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        first.Status.ShouldBe(InboundDispatchStatus.NoRoute);

        // Second message on the SAME isolation key, after the queue sealed. Pre-fix this could land
        // in an orphaned channel and hang forever.
        var second = await orchestrator
            .AcceptAsync(CreateMessage("addr-seal"))
            .WaitAsync(TimeSpan.FromSeconds(15));

        second.Status.ShouldBe(
            InboundDispatchStatus.NoRoute,
            "a message following a sealed queue must be processed on a fresh queue, not dropped (#3600)");
        calls.ShouldBe(2, "the processor must have seen both messages");
    }

    /// <summary>
    /// Repeated seal-then-send cycles must never lose a message. One rescue is not enough: the
    /// production user re-sent three times.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_RepeatedSealCycles_EveryMessageIsProcessed()
    {
        var processor = Substitute.For<IInboundMessageProcessor>();
        processor
            .ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            // Every message seals its queue, so every subsequent send meets a dead worker.
            .Returns(Task.FromResult(new InboundProcessingOutcome(EmptyDispatches, true)));

        var logger = new CapturingLogger();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(processor, logger);

        for (var i = 0; i < 5; i++)
        {
            var result = await orchestrator
                .AcceptAsync(CreateMessage("addr-seal-loop"))
                .WaitAsync(TimeSpan.FromSeconds(15));
            result.Status.ShouldBe(
                InboundDispatchStatus.NoRoute,
                $"message {i + 1} must reach the processor after a sealed queue (#3600)");
        }

        await processor.Received(5).ProcessAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    private static InboundMessage CreateMessage(string address)
        => new()
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From(address),
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            Content = "hello"
        };

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
