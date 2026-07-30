using Azure.Messaging.ServiceBus;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// Behavioural tests for the acknowledgement path of <see cref="ServiceBusChannelAdapter"/> (#2525).
///
/// These assert what an operator and the broker actually observe — which outcome was reached,
/// whether abandon was called, and at what severity the failure was reported — rather than
/// asserting that a configuration property holds a particular number.
/// </summary>
public sealed class ServiceBusChannelAdapterAckFailureTests
{
    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<ServiceBusChannelAdapter>
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

    private static ServiceBusChannelAdapter CreateAdapter(CapturingLogger logger)
    {
        var opts = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
            InboundQueueName = "test-inbound",
            DefaultReplyQueueName = "test-outbound",
        };

        return new ServiceBusChannelAdapter(
            logger,
            new OptionsWrapper<ServiceBusChannelOptions>(opts),
            new FakeServiceBusAdapterClientFactory());
    }

    [Fact]
    public async Task ProcessMessageCore_HandlerSucceedsAndCompleteSucceeds_CompletesWithoutAbandon()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-1",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.Completed, outcome);
        Assert.Equal(0, abandonCalls);
        Assert.DoesNotContain(logger.Records, r => r.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ProcessMessageCore_HandlerThrows_AbandonsAndReportsProcessingFailure()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;
        var completeCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            _ => throw new InvalidOperationException("handler blew up"),
            _ => { completeCalls++; return Task.CompletedTask; },
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-2",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.AbandonedAfterHandlerFailure, outcome);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(0, completeCalls);
        Assert.Contains(logger.Records, r => r.Level == LogLevel.Error && r.Message.Contains("unhandled error processing"));
    }

    [Fact]
    public async Task ProcessMessageCore_LockLostOnComplete_DoesNotAbandonAndIsNotReportedAsProcessingFailure()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;
        var handlerRan = false;

        var outcome = await adapter.ProcessMessageCoreAsync(
            _ => { handlerRan = true; return Task.CompletedTask; },
            _ => throw new ServiceBusException("The lock supplied is invalid.", ServiceBusFailureReason.MessageLockLost),
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-3",
            CancellationToken.None);

        Assert.True(handlerRan);
        Assert.Equal(MessageProcessingOutcome.CompleteFailedLockLost, outcome);

        // The lock is already invalid: abandoning cannot succeed and must not be attempted.
        Assert.Equal(0, abandonCalls);

        // The work succeeded. Reporting this as a processing error is the misleading behaviour
        // that #2525 is about, so no Error-level record may be produced.
        Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Error);

        var warning = Assert.Single(logger.Records, r => r.Level == LogLevel.Warning);
        Assert.Contains("successfully", warning.Message);
        Assert.Contains("redeliver", warning.Message);
    }

    [Fact]
    public async Task ProcessMessageCore_SessionLockLostOnComplete_IsTreatedAsLockExpiry()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            _ => Task.CompletedTask,
            _ => throw new ServiceBusException("session lock lost", ServiceBusFailureReason.SessionLockLost),
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-4",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.CompleteFailedLockLost, outcome);
        Assert.Equal(0, abandonCalls);
        Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ProcessMessageCore_NonLockCompleteFailure_AbandonsButIsNotReportedAsProcessingFailure()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            _ => Task.CompletedTask,
            _ => throw new ServiceBusException("transient", ServiceBusFailureReason.ServiceTimeout),
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-5",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.CompleteFailedAbandoned, outcome);

        // The lock may still be held, so releasing it promptly is the deliberate choice here.
        Assert.Equal(1, abandonCalls);
        Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Error);

        var warning = Assert.Single(logger.Records, r => r.Level == LogLevel.Warning);
        Assert.Contains("could not acknowledge", warning.Message);
    }

    [Fact]
    public async Task ProcessMessageCore_CancellationDuringHandler_AbandonsForShutdown()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var abandonCalls = 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await adapter.ProcessMessageCoreAsync(
            ct => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-6",
            cts.Token);

        Assert.Equal(MessageProcessingOutcome.AbandonedForShutdown, outcome);
        Assert.Equal(1, abandonCalls);
        Assert.DoesNotContain(logger.Records, r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ProcessMessageCore_CompleteIsOnlyAttemptedAfterHandlerReturns()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var order = new List<string>();

        var outcome = await adapter.ProcessMessageCoreAsync(
            async _ => { await Task.Yield(); order.Add("handle"); },
            _ => { order.Add("complete"); return Task.CompletedTask; },
            () => { order.Add("abandon"); return Task.CompletedTask; },
            "msg-7",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.Completed, outcome);

        // At-least-once delivery: acknowledging before the work would trade a duplicate for
        // silent loss on a mid-turn crash, which is the worse failure mode.
        Assert.Equal(["handle", "complete"], order);
    }
}
