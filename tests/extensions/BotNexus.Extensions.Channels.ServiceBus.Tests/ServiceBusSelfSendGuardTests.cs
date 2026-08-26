using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// #3501: the adapter must never publish a reply into the queue its own processor consumes
/// from. A self-send is an unbounded redelivery loop that is only ever terminated by
/// <c>maxDeliveryCount</c> dead-lettering, after the throughput and DLQ capacity are already
/// spent.
/// <para>
/// The reply queue is resolved from three independent sources
/// (<c>ResolveReplyQueue</c>: outbound metadata, the pending inbound context, then the configured
/// default), and the per-message value can arrive from an untrusted producer via either the JSON
/// envelope <c>replyTo</c> field or a Service Bus <c>applicationProperties["replyTo"]</c> entry.
/// Every one of those branches is covered here, because a guard applied only to the configured
/// default leaves the injected path — the actually hostile one — wide open.
/// </para>
/// </summary>
public sealed class ServiceBusSelfSendGuardTests
{
    private const string InboundQueue = "test-inbound";

    private static ServiceBusChannelOptions Options(string? defaultReplyQueue = "test-outbound")
        => new()
        {
            ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
            InboundQueueName = InboundQueue,
            DefaultReplyQueueName = defaultReplyQueue!,
        };

    private static ServiceBusChannelAdapter CreateAdapter(
        ServiceBusChannelOptions options,
        FakeServiceBusAdapterClientFactory factory,
        ILogger<ServiceBusChannelAdapter>? logger = null)
        => new(
            logger ?? new CapturingLogger<ServiceBusChannelAdapter>(),
            new OptionsWrapper<ServiceBusChannelOptions>(options),
            factory);

    private static Mock<IChannelDispatcher> StartAdapter(ServiceBusChannelAdapter adapter)
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        adapter.StartAsync(dispatcher.Object).GetAwaiter().GetResult();
        return dispatcher;
    }

    // ── AC1: startup validation ────────────────────────────────────────────────

    /// <summary>
    /// AC1: an operator who points <c>DefaultReplyQueueName</c> at <c>InboundQueueName</c> gets a
    /// warning that names BOTH values. Silently accepting the configuration is what makes this
    /// reachable by pure misconfiguration rather than only by a hostile producer.
    /// </summary>
    [Theory]
    [InlineData(InboundQueue)]
    [InlineData("TEST-INBOUND")] // case-insensitive: Service Bus entity names are not case-sensitive.
    public async Task StartAsync_DefaultReplyQueueEqualsInboundQueue_WarnsNamingBothQueues(string defaultReplyQueue)
    {
        var logger = new CapturingLogger<ServiceBusChannelAdapter>();
        var adapter = CreateAdapter(Options(defaultReplyQueue), new FakeServiceBusAdapterClientFactory(), logger);

        StartAdapter(adapter);

        var warning = logger.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .Select(e => e.Message)
            .FirstOrDefault(m => m.Contains(InboundQueue, StringComparison.OrdinalIgnoreCase));

        warning.ShouldNotBeNull("Startup must warn when the reply queue is the inbound queue.");
        warning.ShouldContain(InboundQueue, Case.Insensitive);
        warning.ShouldContain(defaultReplyQueue, Case.Insensitive);

        await adapter.StopAsync();
    }

    /// <summary>A correctly separated pair must stay silent — the guard must not cry wolf.</summary>
    [Fact]
    public async Task StartAsync_DistinctQueues_DoesNotWarn()
    {
        var logger = new CapturingLogger<ServiceBusChannelAdapter>();
        var adapter = CreateAdapter(Options(), new FakeServiceBusAdapterClientFactory(), logger);

        StartAdapter(adapter);

        logger.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldBeEmpty();

        await adapter.StopAsync();
    }

    // ── AC2/AC3 branch 1: outbound metadata replyTo ────────────────────────────

    /// <summary>
    /// AC2 + AC3 (branch 1 of <c>ResolveReplyQueue</c>): a <c>servicebus.replyTo</c> carried on the
    /// OUTBOUND metadata takes precedence over everything else, so it must be guarded too.
    /// </summary>
    [Fact]
    public async Task SendAsync_MetadataReplyToIsInboundQueue_RefusesAndSendsNothing()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(), factory);
        StartAdapter(adapter);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-meta"),
            Content = "would loop forever",
            Metadata = new Dictionary<string, object?>
            {
                [ServiceBusChannelAdapter.MetaReplyTo] = InboundQueue,
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        // The error must identify the queue AND the resolution source, so an operator can tell a
        // misconfigured default apart from an injected per-message value.
        ex.Message.ShouldContain(InboundQueue);
        ex.Message.ShouldContain("metadata", Case.Insensitive);

        // Fail-closed: nothing is published to the inbound queue.
        factory.Senders.ShouldNotContainKey(InboundQueue);
    }

    // ── AC2/AC3 branch 2: pending context replyTo, from the envelope ───────────

    /// <summary>
    /// AC2 + AC3 (branch 2): a per-message <c>replyTo</c> on the inbound JSON envelope becomes the
    /// pending context's reply queue. This is the untrusted-producer path.
    /// </summary>
    [Fact]
    public async Task SendAsync_EnvelopeReplyToIsInboundQueue_RefusesAndSendsNothing()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(), factory);
        StartAdapter(adapter);

        var json = $$"""
            { "content": "hostile", "senderId": "legit@domain.com", "conversationId": "conv-env",
              "replyTo": "{{InboundQueue}}", "correlationId": "corr-env" }
            """;
        await adapter.HandleMessageBodyAsync(json, null, "msg-env", CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-env"),
            Content = "reply that would be re-consumed as new inbound work",
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        ex.Message.ShouldContain(InboundQueue);
        ex.Message.ShouldContain("pending", Case.Insensitive);
        factory.Senders.ShouldNotContainKey(InboundQueue);
    }

    /// <summary>
    /// AC6, explicitly: the same hostile value injected via Service Bus
    /// <c>applicationProperties["replyTo"]</c> instead of the JSON envelope. The adapter treats
    /// application properties as a fallback source for <c>replyTo</c>, so a guard written only
    /// against the envelope field misses this branch entirely.
    /// </summary>
    [Fact]
    public async Task SendAsync_ApplicationPropertyReplyToIsInboundQueue_RefusesAndSendsNothing()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(), factory);
        StartAdapter(adapter);

        var appProps = new Dictionary<string, object>
        {
            ["senderId"] = "legit@domain.com",
            ["conversationId"] = "conv-props",
            // Injected by the producer; the envelope itself carries no replyTo at all.
            ["replyTo"] = InboundQueue,
        };
        await adapter.HandleMessageBodyAsync(
            """{ "content": "hostile via app props" }""",
            appProps,
            "msg-props",
            CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-props"),
            Content = "reply",
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        ex.Message.ShouldContain(InboundQueue);
        factory.Senders.ShouldNotContainKey(InboundQueue);
    }

    /// <summary>Case-insensitivity: Service Bus entity names do not distinguish case.</summary>
    [Fact]
    public async Task SendAsync_ReplyToDiffersOnlyByCase_StillRefuses()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(), factory);
        StartAdapter(adapter);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-case"),
            Content = "case-shifted loop",
            Metadata = new Dictionary<string, object?>
            {
                [ServiceBusChannelAdapter.MetaReplyTo] = "TeSt-InBoUnD",
            },
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        factory.Senders.Keys.ShouldNotContain(k => k.Equals(InboundQueue, StringComparison.OrdinalIgnoreCase));
    }

    // ── AC2/AC3 branch 3: the configured default ──────────────────────────────

    /// <summary>
    /// AC3 (branch 3): the misconfigured default. The startup warning is a signal, not a
    /// containment — the send itself must still refuse.
    /// </summary>
    [Fact]
    public async Task SendAsync_DefaultReplyQueueEqualsInboundQueue_RefusesAndSendsNothing()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(InboundQueue), factory);
        StartAdapter(adapter);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-default"),
            Content = "reply via misconfigured default",
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        ex.Message.ShouldContain(InboundQueue);
        ex.Message.ShouldContain("default", Case.Insensitive);
        factory.Senders.ShouldNotContainKey(InboundQueue);
    }

    // ── AC3: streaming path shares the same resolution, so it shares the guard ─

    /// <summary>
    /// The streaming send path resolves its reply queue through the SAME
    /// <c>ResolveReplyQueue</c> helper, so a guard placed only in <c>SendAsync</c> would leave a
    /// second, equally looping publisher unguarded.
    /// </summary>
    [Fact]
    public async Task SendStreamEventAsync_PendingReplyToIsInboundQueue_RefusesAndSendsNothing()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(Options(), factory);
        StartAdapter(adapter);

        var json = $$"""
            { "content": "hostile stream", "senderId": "legit@domain.com", "conversationId": "conv-stream",
              "replyTo": "{{InboundQueue}}", "streamResponse": true }
            """;
        await adapter.HandleMessageBodyAsync(json, null, "msg-stream", CancellationToken.None);

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-stream"),
            SessionId.From("sess-stream"),
            ChannelAddress.From("conv-stream"),
            ChannelRequestId: "msg-stream");

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendStreamDeltaAsync(target, "delta", CancellationToken.None));

        factory.Senders.ShouldNotContainKey(InboundQueue);
    }

    // ── AC4: the allow-list block must be operator-visible ────────────────────

    /// <summary>
    /// AC4: a non-empty allow-list that does not contain the real sender blackholes every message.
    /// At <c>LogDebug</c> that is invisible in a normal deployment, so the diagnosis cost is
    /// unbounded. The block must surface at <c>LogWarning</c> or above.
    /// </summary>
    [Fact]
    public async Task DispatchInbound_SenderNotInAllowList_LogsAtWarningOrAbove()
    {
        var options = Options();
        options.AllowedSenderIds.Add("allowed@domain.com");

        var logger = new CapturingLogger<ServiceBusChannelAdapter>();
        var adapter = CreateAdapter(options, new FakeServiceBusAdapterClientFactory(), logger);
        var dispatcher = StartAdapter(adapter);

        await adapter.HandleMessageBodyAsync(
            """{ "content": "blocked", "senderId": "stranger@domain.com" }""",
            null,
            "msg-blocked",
            CancellationToken.None);

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The adapter's own ILogger is the one ChannelAdapterBase writes the block through, so the
        // captured entries are the operator-visible record.
        var blocked = logger.Entries.FirstOrDefault(e => e.Message.Contains("stranger@domain.com", StringComparison.Ordinal));
        blocked.Message.ShouldNotBeNull("The allow-list block must be logged at all.");
        ((int)blocked.Level).ShouldBeGreaterThanOrEqualTo((int)LogLevel.Warning);
        blocked.Message.ShouldContain("allow list", Case.Insensitive);

        await adapter.StopAsync();
    }
}

/// <summary>
/// Minimal in-memory <see cref="ILogger{T}"/> that records level and rendered message so tests can
/// assert on operator visibility rather than on a mock's invocation shape.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
