using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// Unit tests for <see cref="ServiceBusChannelAdapter"/>.
/// All tests use <see cref="FakeServiceBusAdapterClientFactory"/> to avoid real Azure connections.
/// </summary>
public sealed class ServiceBusChannelAdapterTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ServiceBusChannelAdapter CreateAdapter(
        ServiceBusChannelOptions? options = null,
        FakeServiceBusAdapterClientFactory? factory = null)
    {
        var opts = options ?? new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
            InboundQueueName = "test-inbound",
            DefaultReplyQueueName = "test-outbound",
        };

        return new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            new OptionsWrapper<ServiceBusChannelOptions>(opts),
            factory ?? new FakeServiceBusAdapterClientFactory());
    }

    private static Mock<IChannelDispatcher> StartAdapter(ServiceBusChannelAdapter adapter)
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        adapter.StartAsync(dispatcher.Object).GetAwaiter().GetResult();
        return dispatcher;
    }

    // ── Test 1: Inbound envelope maps all fields correctly ─────────────────────

    [Fact]
    public async Task HandleMessageBodyAsync_FullEnvelope_DispatchesWithAllFieldsMapped()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = StartAdapter(adapter);

        var json = JsonSerializer.Serialize(new
        {
            messageId = "msg-001",
            correlationId = "corr-abc",
            agentId = "coding-agent",
            conversationId = "conv-xyz",
            sessionId = "sess-123",
            senderId = "user@org.com",
            role = "user",
            content = "Hello agent",
            replyTo = "botnexus-outbound",
            timestamp = "2026-05-13T12:22:56Z",
        });

        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        var dispatched = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .Single();

        dispatched.ChannelType.ShouldBe(ChannelKey.From("servicebus"));
        dispatched.SenderId.ShouldBe("user@org.com");
        dispatched.Content.ShouldBe("Hello agent");
        dispatched.TargetAgentId.ShouldBe("coding-agent");
        dispatched.SessionId.ShouldBe("sess-123");
        dispatched.ConversationId.ShouldBe("conv-xyz");
        dispatched.ChannelAddress.Value.ShouldBe("conv-xyz");
    }

    // ── Test 2: Missing optional fields handled gracefully ─────────────────────

    [Fact]
    public async Task HandleMessageBodyAsync_MinimalEnvelope_DispatchesWithFallbacks()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = StartAdapter(adapter);

        var json = """{ "content": "minimal message", "senderId": "bot@sys.com" }""";

        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        var dispatched = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .Single();

        dispatched.Content.ShouldBe("minimal message");
        dispatched.SenderId.ShouldBe("bot@sys.com");
        dispatched.TargetAgentId.ShouldBeNull();
        dispatched.SessionId.ShouldBeNull();
        dispatched.ConversationId.ShouldBeNull();
        // ChannelAddress falls back to senderId when conversationId is absent.
        dispatched.ChannelAddress.Value.ShouldBe("bot@sys.com");
    }

    [Fact]
    public async Task HandleMessageBodyAsync_MissingSenderId_UsesUnknownFallback()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = StartAdapter(adapter);

        var json = """{ "content": "no sender" }""";

        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        var dispatched = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .Single();

        dispatched.SenderId.ShouldBe("unknown");
    }

    // ── Test 3: Allow-list blocks unauthorised senders ─────────────────────────

    [Fact]
    public async Task HandleMessageBodyAsync_SenderNotInAllowList_DoesNotDispatch()
    {
        var options = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake/;SharedAccessKeyName=x;SharedAccessKey=y=",
            InboundQueueName = "q",
            DefaultReplyQueueName = "q-out",
        };
        options.AllowedSenderIds.Add("allowed@org.com");

        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(options, factory);
        var dispatcher = StartAdapter(adapter);

        var json = """{ "content": "blocked", "senderId": "unknown@evil.com" }""";

        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleMessageBodyAsync_SenderInAllowList_Dispatches()
    {
        var options = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake/;SharedAccessKeyName=x;SharedAccessKey=y=",
            InboundQueueName = "q",
            DefaultReplyQueueName = "q-out",
        };
        options.AllowedSenderIds.Add("allowed@org.com");

        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(options, factory);
        var dispatcher = StartAdapter(adapter);

        var json = """{ "content": "hello", "senderId": "allowed@org.com" }""";

        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 4: SendAsync routes to replyTo queue when present ────────────────

    [Fact]
    public async Task SendAsync_WithReplyToInPendingContext_SendsToReplyToQueue()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        // Simulate an inbound message that sets up the pending reply context.
        var json = """{ "content": "hi", "senderId": "u@x.com", "conversationId": "conv-1", "replyTo": "custom-reply-queue", "correlationId": "corr-99" }""";
        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = "reply text",
            SessionId = "sess-abc",
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        factory.Senders.ShouldContainKey("custom-reply-queue");
        factory.Senders["custom-reply-queue"].SentMessages.ShouldHaveSingleItem();
    }

    // ── Test 5: SendAsync falls back to default reply queue ───────────────────

    [Fact]
    public async Task SendAsync_NoReplyTo_SendsToDefaultReplyQueue()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        // Inbound with no replyTo → pending context has null ReplyTo.
        var json = """{ "content": "hi", "senderId": "u@x.com", "conversationId": "conv-2" }""";
        await adapter.HandleMessageBodyAsync(json, null, null, CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-2"),
            Content = "reply",
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        factory.Senders.ShouldContainKey("test-outbound");
        factory.Senders["test-outbound"].SentMessages.ShouldHaveSingleItem();
    }

    // ── Test 6: Correlation ID preserved in reply ─────────────────────────────

    [Fact]
    public async Task SendAsync_WithCorrelationId_PreservesCorrelationIdInEnvelope()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        const string correlationId = "corr-preserved-42";

        var inboundJson = $$"""{ "content": "q", "senderId": "s@x.com", "conversationId": "conv-3", "correlationId": "{{correlationId}}" }""";
        await adapter.HandleMessageBodyAsync(inboundJson, null, null, CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-3"),
            Content = "answer",
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        var sent = factory.Senders["test-outbound"].SentMessages.Single();
        sent.CorrelationId.ShouldBe(correlationId);

        // Also verify the JSON body carries correlationId.
        var envelope = JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(
            sent.Body.ToString(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        envelope.ShouldNotBeNull();
        envelope!.CorrelationId.ShouldBe(correlationId);
    }

    // ── Test 7: Options bind from IOptions ────────────────────────────────────

    [Fact]
    public void ChannelAdapter_WithConfiguredOptions_ReflectsOptionValues()
    {
        var options = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v=",
            InboundQueueName = "my-inbound",
            DefaultReplyQueueName = "my-outbound",
            MaxConcurrentCalls = 4,
        };

        var adapter = new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            new OptionsWrapper<ServiceBusChannelOptions>(options),
            new FakeServiceBusAdapterClientFactory());

        adapter.ChannelType.ShouldBe(ChannelKey.From("servicebus"));
        adapter.DisplayName.ShouldBe("Azure Service Bus");
        adapter.SupportsStreaming.ShouldBeFalse();
    }

    // ── Test 8: Start / stop manages processor lifecycle ─────────────────────

    [Fact]
    public async Task StartAsync_StartsProcessor_StopAsync_StopsProcessor()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = new Mock<IChannelDispatcher>();

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        factory.Processor.StartCalled.ShouldBeTrue();
        adapter.IsRunning.ShouldBeTrue();

        await adapter.StopAsync(CancellationToken.None);
        factory.Processor.StopCalled.ShouldBeTrue();
        adapter.IsRunning.ShouldBeFalse();
    }

    // ── Test 9: Malformed JSON is logged and dropped without throwing ─────────

    [Fact]
    public async Task HandleMessageBodyAsync_InvalidJson_DropsMessageWithoutException()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = StartAdapter(adapter);

        // Should not throw; message is silently abandoned (logged at Warning level).
        await adapter.HandleMessageBodyAsync("not-valid-json}{", null, null, CancellationToken.None);

        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test: Application properties fall back for missing envelope fields ─────

    [Fact]
    public async Task HandleMessageBodyAsync_ApplicationPropertiesProvidesFallbacks()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        var dispatcher = StartAdapter(adapter);

        var appProps = new Dictionary<string, object>
        {
            ["senderId"] = "fallback@sys.com",
            ["agentId"] = "fallback-agent",
            ["conversationId"] = "fallback-conv",
            ["sessionId"] = "fallback-sess",
            ["correlationId"] = "fallback-corr",
            ["replyTo"] = "fallback-queue",
        };

        // Envelope has only content — everything else from application properties.
        var json = """{ "content": "from app props" }""";

        await adapter.HandleMessageBodyAsync(json, appProps, null, CancellationToken.None);

        var dispatched = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .Single();

        dispatched.SenderId.ShouldBe("fallback@sys.com");
        dispatched.TargetAgentId.ShouldBe("fallback-agent");
        dispatched.SessionId.ShouldBe("fallback-sess");
        dispatched.ConversationId.ShouldBe("fallback-conv");
    }

    // ── Test: SendAsync uses replyTo from metadata when no pending context ────

    [Fact]
    public async Task SendAsync_ReplyToInOutboundMetadata_UsedWhenNoPendingContext()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        // No prior inbound → no pending context; replyTo comes from metadata.
        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("direct-conv"),
            Content = "direct reply",
            Metadata = new Dictionary<string, object?>
            {
                [ServiceBusChannelAdapter.MetaReplyTo] = "direct-reply-queue",
                [ServiceBusChannelAdapter.MetaCorrelationId] = "direct-corr",
            },
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        factory.Senders.ShouldContainKey("direct-reply-queue");
        var sent = factory.Senders["direct-reply-queue"].SentMessages.Single();
        sent.CorrelationId.ShouldBe("direct-corr");
    }

    // ── Test: Concurrent inbound messages for same conversation route independently ─

    [Fact]
    public async Task SendAsync_ConcurrentInboundSameConversation_RepliesRouteIndependently()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        // Two inbound messages for the same conversationId but distinct replyTo/correlationId.
        // This simulates MaxConcurrentCalls > 1 with two messages in-flight simultaneously.
        var jsonA = """{ "content": "msg A", "senderId": "u@x.com", "conversationId": "conv-concurrent", "replyTo": "reply-queue-A", "correlationId": "corr-A" }""";
        var jsonB = """{ "content": "msg B", "senderId": "u@x.com", "conversationId": "conv-concurrent", "replyTo": "reply-queue-B", "correlationId": "corr-B" }""";

        await adapter.HandleMessageBodyAsync(jsonA, null, "sb-msg-id-A", CancellationToken.None);
        await adapter.HandleMessageBodyAsync(jsonB, null, "sb-msg-id-B", CancellationToken.None);

        // Reply for B is sent first (out-of-order) to prove routing is by key, not FIFO.
        var outboundB = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-concurrent"),
            Content = "reply B",
            Metadata = new Dictionary<string, object?> { [ServiceBusChannelAdapter.MetaRequestKey] = "sb-msg-id-B" },
        };
        await adapter.SendAsync(outboundB, CancellationToken.None);

        // Reply for A sent second.
        var outboundA = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv-concurrent"),
            Content = "reply A",
            Metadata = new Dictionary<string, object?> { [ServiceBusChannelAdapter.MetaRequestKey] = "sb-msg-id-A" },
        };
        await adapter.SendAsync(outboundA, CancellationToken.None);

        // A's reply must land in A's queue with A's correlationId.
        factory.Senders.ShouldContainKey("reply-queue-A");
        factory.Senders["reply-queue-A"].SentMessages.ShouldHaveSingleItem();
        factory.Senders["reply-queue-A"].SentMessages[0].CorrelationId.ShouldBe("corr-A");

        // B's reply must land in B's queue with B's correlationId.
        factory.Senders.ShouldContainKey("reply-queue-B");
        factory.Senders["reply-queue-B"].SentMessages.ShouldHaveSingleItem();
        factory.Senders["reply-queue-B"].SentMessages[0].CorrelationId.ShouldBe("corr-B");
    }
}
