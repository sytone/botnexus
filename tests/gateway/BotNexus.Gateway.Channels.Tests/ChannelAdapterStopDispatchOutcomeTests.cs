using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Behavioural tests for the inbound-dispatch outcome contract on <see cref="ChannelAdapterBase"/>
/// (#3594).
/// </summary>
/// <remarks>
/// Before #3594 <c>DispatchInboundAsync</c> returned <see cref="Task"/>, so a caller could not tell
/// "I routed this message" from "I silently dropped it because the adapter was stopped". These
/// tests assert the distinction the settlement layer depends on, and that an allow-list block stays
/// on the success side of it - a policy drop is not a delivery failure.
/// </remarks>
public sealed class ChannelAdapterStopDispatchOutcomeTests
{
    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add(new LogRecord(logLevel, formatter(state, exception)));
    }

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        public List<InboundMessage> Dispatched { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal concrete adapter that exposes the two protected dispatch seams so the outcome
    /// contract can be asserted directly rather than inferred through a transport.
    /// </summary>
    private sealed class ProbeAdapter : ChannelAdapterBase
    {
        public ProbeAdapter(ILogger logger, params string[] allowList)
            : base(logger)
            => AllowList = allowList;

        public override ChannelKey ChannelType => ChannelKey.From("probe");

        public override string DisplayName => "Probe";

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChannelDispatchOutcome> DispatchAsync(InboundMessage message, CancellationToken ct = default)
            => DispatchInboundAsync(message, ct);

        public Task<ChannelDispatchOutcome> DispatchOrThrowAsync(InboundMessage message, CancellationToken ct = default)
            => DispatchInboundOrThrowAsync(message, ct);
    }

    private static InboundMessage Message(string senderId = "user@example.com") => new()
    {
        ChannelType = ChannelKey.From("probe"),
        SenderId = senderId,
        Sender = CitizenId.Of(UserId.From(senderId)),
        ChannelAddress = ChannelAddress.From("conv-1"),
        Content = "hello",
    };

    // ── AC2: a stopped adapter reports a distinguishable non-success outcome ───

    [Fact]
    public async Task DispatchInbound_AdapterNeverStarted_ReportsAdapterStopped()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);

        var outcome = await adapter.DispatchAsync(Message());

        Assert.Equal(ChannelDispatchOutcome.AdapterStopped, outcome);
    }

    [Fact]
    public async Task DispatchInbound_AfterStop_ReportsAdapterStoppedNotDispatched()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);
        await adapter.StopAsync();

        var outcome = await adapter.DispatchAsync(Message());

        Assert.Equal(ChannelDispatchOutcome.AdapterStopped, outcome);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task DispatchInbound_WhileRunning_ReportsDispatchedAndRoutesTheMessage()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);

        var outcome = await adapter.DispatchAsync(Message());

        Assert.Equal(ChannelDispatchOutcome.Dispatched, outcome);
        Assert.Single(dispatcher.Dispatched);
    }

    [Fact]
    public async Task DispatchInboundOrThrow_AfterStop_ThrowsChannelStoppedException()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);

        await adapter.StartAsync(new RecordingDispatcher());
        await adapter.StopAsync();

        var ex = await Assert.ThrowsAsync<ChannelStoppedException>(
            () => adapter.DispatchOrThrowAsync(Message()));

        Assert.Equal("probe", ex.ChannelType);
    }

    // ── AC3: an allow-list block is a policy drop, NOT a delivery failure ──────

    [Fact]
    public async Task DispatchInbound_BlockedSender_ReportsBlockedByAllowListAndDoesNotDispatch()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger, "allowed@example.com");
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);

        var outcome = await adapter.DispatchAsync(Message("blocked@example.com"));

        Assert.Equal(ChannelDispatchOutcome.BlockedByAllowList, outcome);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task DispatchInboundOrThrow_BlockedSender_DoesNotThrow()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger, "allowed@example.com");

        await adapter.StartAsync(new RecordingDispatcher());

        // The whole point: a blocked sender must remain settleable. Redelivering it would only
        // block it again, so it must not travel the same failure path as a not-dispatched message.
        var outcome = await adapter.DispatchOrThrowAsync(Message("blocked@example.com"));

        Assert.Equal(ChannelDispatchOutcome.BlockedByAllowList, outcome);
    }

    // ── AC5: the drop is logged at Warning and says DISCARDED, not merely received ──

    [Fact]
    public async Task DispatchInbound_AfterStop_LogsWarningStatingTheMessageWasDiscarded()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);

        await adapter.StartAsync(new RecordingDispatcher());
        await adapter.StopAsync();
        logger.Records.Clear();

        await adapter.DispatchAsync(Message());

        var record = Assert.Single(logger.Records, r => r.Level >= LogLevel.Warning);
        Assert.Contains("discarded", record.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never routed", record.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Shutdown ordering: drain before releasing the dispatcher ───────────────

    [Fact]
    public async Task StopAsync_WaitsForAnInFlightDispatchBeforeReleasingTheDispatcher()
    {
        var logger = new CapturingLogger();
        var adapter = new ProbeAdapter(logger);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new BlockingDispatcher(entered, gate);

        await adapter.StartAsync(dispatcher);

        var inFlight = adapter.DispatchAsync(Message());
        await entered.Task;

        var stop = adapter.StopAsync();

        // The dispatch is past the dispatcher read and still running, so Stop must not have
        // completed - releasing the reference underneath it is exactly the #3594 race.
        Assert.False(stop.IsCompleted);

        gate.SetResult();

        Assert.Equal(ChannelDispatchOutcome.Dispatched, await inFlight);
        await stop;
        Assert.False(adapter.IsRunning);
    }

    private sealed class BlockingDispatcher(TaskCompletionSource entered, TaskCompletionSource gate) : IChannelDispatcher
    {
        public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await gate.Task;
        }
    }
}
