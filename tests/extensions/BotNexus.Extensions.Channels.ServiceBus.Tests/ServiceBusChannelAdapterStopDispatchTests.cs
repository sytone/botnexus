using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// Settlement tests for the shutdown race that acknowledged Service Bus messages which were never
/// dispatched (#3594).
/// </summary>
/// <remarks>
/// <c>ProcessMessageCoreAsync</c> derives acknowledgement from the handler returning without
/// throwing. When <c>ChannelAdapterBase</c> dropped an inbound message because the dispatcher had
/// already been released, the handler returned normally, so the broker was told the work was done
/// and never redelivered it. These tests assert the observable settlement decision - abandon vs
/// complete - not an internal flag.
/// </remarks>
public sealed class ServiceBusChannelAdapterStopDispatchTests
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

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        public List<InboundMessage> Dispatched { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(message);
            return Task.CompletedTask;
        }
    }

    private static ServiceBusChannelAdapter CreateAdapter(CapturingLogger logger, params string[] allowedSenders)
    {
        var opts = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
            InboundQueueName = "test-inbound",
            DefaultReplyQueueName = "test-outbound",
        };

        foreach (var sender in allowedSenders)
            opts.AllowedSenderIds.Add(sender);

        return new ServiceBusChannelAdapter(
            logger,
            new OptionsWrapper<ServiceBusChannelOptions>(opts),
            new FakeServiceBusAdapterClientFactory());
    }

    private static string Envelope(string senderId = "user@example.com", string messageId = "msg-3594") =>
        JsonSerializer.Serialize(new
        {
            messageId,
            conversationId = "conv-3594",
            senderId,
            role = "user",
            content = "did this survive shutdown?",
            replyTo = "test-outbound",
        });

    // ── AC1: a message that could not be dispatched is ABANDONED, not completed ──

    [Fact]
    public async Task ProcessMessageCore_AdapterStopped_AbandonsAndDoesNotComplete()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);
        await adapter.StopAsync();

        var abandonCalls = 0;
        var completeCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            ct => adapter.HandleMessageBodyAsync(Envelope(), null, "msg-3594", ct),
            _ => { completeCalls++; return Task.CompletedTask; },
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-3594",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.AbandonedNotDispatched, outcome);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(0, completeCalls);
        Assert.Empty(dispatcher.Dispatched);
    }

    // ── AC5: the operator sees a Warning saying it was NOT processed ────────────

    [Fact]
    public async Task ProcessMessageCore_AdapterStopped_LogsWarningThatTheMessageWasNotProcessed()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);

        await adapter.StartAsync(new RecordingDispatcher());
        await adapter.StopAsync();
        logger.Records.Clear();

        await adapter.ProcessMessageCoreAsync(
            ct => adapter.HandleMessageBodyAsync(Envelope(), null, "msg-3594", ct),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            "msg-3594",
            CancellationToken.None);

        var warnings = logger.Records.Where(r => r.Level >= LogLevel.Warning).ToList();
        Assert.Contains(warnings, r => r.Message.Contains("NOT processed", StringComparison.Ordinal));
        Assert.Contains(warnings, r => r.Message.Contains("abandoned", StringComparison.OrdinalIgnoreCase));
    }

    // ── AC3: an allow-list drop still settles as before ────────────────────────

    [Fact]
    public async Task ProcessMessageCore_SenderBlockedByAllowList_CompletesWithoutAbandon()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger, "allowed@example.com");
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);

        var abandonCalls = 0;
        var completeCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            ct => adapter.HandleMessageBodyAsync(Envelope("blocked@example.com"), null, "msg-blocked", ct),
            _ => { completeCalls++; return Task.CompletedTask; },
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-blocked",
            CancellationToken.None);

        // A policy drop is not a delivery failure: redelivering it would only block it again.
        Assert.Equal(MessageProcessingOutcome.Completed, outcome);
        Assert.Equal(1, completeCalls);
        Assert.Equal(0, abandonCalls);
        Assert.Empty(dispatcher.Dispatched);
    }

    // ── The running adapter is unaffected: still dispatched, still completed ────

    [Fact]
    public async Task ProcessMessageCore_AdapterRunning_DispatchesAndCompletes()
    {
        var logger = new CapturingLogger();
        var adapter = CreateAdapter(logger);
        var dispatcher = new RecordingDispatcher();

        await adapter.StartAsync(dispatcher);

        var abandonCalls = 0;

        var outcome = await adapter.ProcessMessageCoreAsync(
            ct => adapter.HandleMessageBodyAsync(Envelope(), null, "msg-3594", ct),
            _ => Task.CompletedTask,
            () => { abandonCalls++; return Task.CompletedTask; },
            "msg-3594",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.Completed, outcome);
        Assert.Equal(0, abandonCalls);
        Assert.Single(dispatcher.Dispatched);
    }

    // ── Redelivery after restart actually works (the point of abandoning) ───────

    [Fact]
    public async Task AbandonedNotDispatchedMessage_IsDispatchedOnRedeliveryAfterRestart()
    {
        var logger = new CapturingLogger();
        var stopping = CreateAdapter(logger);
        var dispatcher = new RecordingDispatcher();

        await stopping.StartAsync(dispatcher);
        await stopping.StopAsync();

        var first = await stopping.ProcessMessageCoreAsync(
            ct => stopping.HandleMessageBodyAsync(Envelope(), null, "msg-3594", ct),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            "msg-3594",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.AbandonedNotDispatched, first);
        Assert.Empty(dispatcher.Dispatched);

        // A restart is a new gateway process, so it is a new adapter instance with an empty #2525
        // suppression cache. The abandoned message is redelivered and must now actually be routed -
        // that redelivery is the whole reason abandoning beats completing.
        var restarted = CreateAdapter(logger);
        await restarted.StartAsync(dispatcher);

        var second = await restarted.ProcessMessageCoreAsync(
            ct => restarted.HandleMessageBodyAsync(Envelope(), null, "msg-3594", ct),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            "msg-3594",
            CancellationToken.None);

        Assert.Equal(MessageProcessingOutcome.Completed, second);
        Assert.Single(dispatcher.Dispatched);
    }
}
