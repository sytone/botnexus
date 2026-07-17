using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
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
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("coding-agent");
        dispatched.RoutingHints.RequestedSessionId!.Value.Value.ShouldBe("sess-123");
        dispatched.RoutingHints.RequestedConversationId!.Value.Value.ShouldBe("conv-xyz");
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
        // Minimal envelope: no overrides supplied. LiftFromStrings returns null when all 3 inputs blank,
        // so RoutingHints itself is null on the inbound message.
        dispatched.RoutingHints.ShouldBeNull();
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
        dispatched.RoutingHints.ShouldNotBeNull();
        dispatched.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("fallback-agent");
        dispatched.RoutingHints.RequestedSessionId!.Value.Value.ShouldBe("fallback-sess");
        dispatched.RoutingHints.RequestedConversationId!.Value.Value.ShouldBe("fallback-conv");
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

    // ── Test: Retry/redelivery of same messageId does not poison FIFO queue ─────

    [Fact]
    public async Task SendAsync_MessageRedeliveredWithSameMessageId_DoesNotPoisonFifoForNextPendingReply()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);

        // 1. First delivery of message A (simulates initial attempt that gets abandoned).
        const string retryMessageId = "sb-retry-msg-id";
        const string conversationId = "conv-retry-fifo";

        var jsonA1 = $$"""{ "content": "msg A first delivery", "senderId": "u@x.com", "conversationId": "{{conversationId}}", "replyTo": "reply-queue-A", "correlationId": "corr-A" }""";
        await adapter.HandleMessageBodyAsync(jsonA1, null, retryMessageId, CancellationToken.None);

        // 2. Service Bus redelivers the same message (identical messageId) after abandonment.
        var jsonA2 = $$"""{ "content": "msg A retry", "senderId": "u@x.com", "conversationId": "{{conversationId}}", "replyTo": "reply-queue-A", "correlationId": "corr-A" }""";
        await adapter.HandleMessageBodyAsync(jsonA2, null, retryMessageId, CancellationToken.None);

        // 3. A second, distinct message B arrives for the same conversation.
        var jsonB = $$"""{ "content": "msg B", "senderId": "u@x.com", "conversationId": "{{conversationId}}", "replyTo": "reply-queue-B", "correlationId": "corr-B" }""";
        await adapter.HandleMessageBodyAsync(jsonB, null, "sb-distinct-msg-id-B", CancellationToken.None);

        // 4. Reply for A uses FIFO fallback (no MetaRequestKey), mirroring the live gateway path
        //    where OutboundMessage.Metadata does not carry inbound metadata.
        //    This dequeues "sb-retry-msg-id" (the only entry), removes context_A, and sends to reply-queue-A.
        //    Without the fix, the queue would be ["sb-retry-msg-id","sb-retry-msg-id","sb-distinct-msg-id-B"]
        //    and this step would still succeed — the corruption only manifests in step 5.
        var outboundA = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = "reply A",
        };
        await adapter.SendAsync(outboundA, CancellationToken.None);

        // 5. Reply for B uses FIFO fallback (no MetaRequestKey in metadata).
        //    With fix: dequeues "sb-distinct-msg-id-B" → routes to reply-queue-B ✓
        //    Without fix: dequeues stale duplicate "sb-retry-msg-id" → TryRemove fails → null
        //                 → falls through to default queue "test-outbound" ✗
        var outboundB = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From(conversationId),
            Content = "reply B",
        };
        await adapter.SendAsync(outboundB, CancellationToken.None);

        // A's reply must have gone to reply-queue-A.
        factory.Senders.ShouldContainKey("reply-queue-A");
        factory.Senders["reply-queue-A"].SentMessages.ShouldHaveSingleItem();
        factory.Senders["reply-queue-A"].SentMessages[0].CorrelationId.ShouldBe("corr-A");

        // B's reply must have gone to reply-queue-B, NOT the default queue.
        factory.Senders.ShouldContainKey("reply-queue-B");
        factory.Senders["reply-queue-B"].SentMessages.ShouldHaveSingleItem();
        factory.Senders["reply-queue-B"].SentMessages[0].CorrelationId.ShouldBe("corr-B");

        // Default queue must NOT have received any message.
        factory.Senders.ShouldNotContainKey("test-outbound");
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


    [Fact]
    public async Task HandleMessageBodyAsync_StreamResponseTrue_MapsTypedPreferenceAndRequestIdentity()
    {
        var adapter = CreateAdapter();
        var dispatcher = StartAdapter(adapter);

        await adapter.HandleMessageBodyAsync(
            """{ "content": "stream", "senderId": "u", "streamResponse": true }""",
            null,
            "request-1",
            CancellationToken.None);

        var inbound = dispatcher.Invocations
            .Select(i => i.Arguments[0])
            .OfType<InboundMessage>()
            .Single();
        inbound.StreamResponse.ShouldBeTrue();
        inbound.ChannelRequestId.ShouldBe("request-1");
        adapter.SupportsStreaming.ShouldBeFalse();
        adapter.ShouldBeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"streamResponse\": null }")]
    [InlineData("{ \"streamResponse\": false }")]
    public async Task HandleMessageBodyAsync_StreamingNotExplicitlyEnabled_RemainsOneShot(string extraJson)
    {
        var adapter = CreateAdapter();
        var dispatcher = StartAdapter(adapter);
        var suffix = extraJson == "{}" ? string.Empty : ", " + extraJson[2..^2];

        await adapter.HandleMessageBodyAsync(
            "{ \"content\": \"one shot\", \"senderId\": \"u\"" + suffix + " }",
            null,
            "request-1",
            CancellationToken.None);

        dispatcher.Invocations.Select(i => i.Arguments[0]).OfType<InboundMessage>()
            .Single().StreamResponse.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("17")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task HandleMessageBodyAsync_MalformedStreamingPreference_RemainsBackwardCompatible(string value)
    {
        var adapter = CreateAdapter();
        var dispatcher = StartAdapter(adapter);

        await adapter.HandleMessageBodyAsync(
            $$"""{ "content": "one shot", "senderId": "u", "streamResponse": {{value}}, "futureStreamingOption": true }""",
            null,
            "request-1",
            CancellationToken.None);

        dispatcher.Invocations.Select(i => i.Arguments[0]).OfType<InboundMessage>()
            .Single().StreamResponse.ShouldBeFalse();
    }

    [Fact]
    public async Task SendStreamEventAsync_DeltasThenRunEnded_SendsOrderedDeltasAndOneConsolidatedTerminal()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        var appProps = new Dictionary<string, object> { ["tenant"] = "north", ["custom"] = 17 };
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "conversationId": "conv", "replyTo": "reply", "correlationId": "corr", "streamResponse": true }""",
            appProps,
            "request-1",
            CancellationToken.None);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv"),
            SessionId.From("session"),
            ChannelAddress.From("conv"),
            ChannelRequestId: "request-1");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello " });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        var sent = factory.Senders["reply"].SentMessages;
        sent.Count.ShouldBe(3);
        var envelopes = sent.Select(DeserializeEnvelope).ToList();
        envelopes.Select(e => e.Type).ShouldBe(["delta", "delta", "done"]);
        envelopes.Select(e => e.Sequence).ShouldBe([0L, 1L, 2L]);
        envelopes.Select(e => e.Content).ShouldBe(["Hello ", "world", "Hello world"]);
        envelopes.Select(e => e.IsFinal).ShouldBe([false, false, true]);
        sent.ShouldAllBe(m =>
            m.CorrelationId == "corr" &&
            m.ApplicationProperties["tenant"].ToString() == "north" &&
            m.ApplicationProperties["custom"].ToString() == "17");
    }

    [Fact]
    public async Task SendStreamEventAsync_TerminalSendFails_ContextAndAccumulatorRemainRetryable()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "conversationId": "conv", "replyTo": "reply", "correlationId": "corr", "streamResponse": true }""",
            null,
            "request-1",
            CancellationToken.None);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv"), SessionId.From("session"), ChannelAddress.From("conv"),
            ChannelRequestId: "request-1");
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "answer" });
        factory.FailNextSend = true;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded }));
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        var envelopes = factory.Senders["reply"].SentMessages.Select(DeserializeEnvelope).ToList();
        envelopes.Select(e => e.Type).ShouldBe(["delta", "done"]);
        envelopes.Last().Content.ShouldBe("answer");
    }

    [Fact]
    public async Task SendStreamEventAsync_CancelledSend_RetainsContextForRetry()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "conversationId": "conv", "replyTo": "reply", "correlationId": "corr", "streamResponse": true }""",
            null,
            "request-1",
            CancellationToken.None);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv"), SessionId.From("session"), ChannelAddress.From("conv"),
            ChannelRequestId: "request-1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => adapter.SendStreamEventAsync(
            target,
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "answer" },
            cancellation.Token));
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "answer" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        factory.Senders["reply"].SentMessages.Select(DeserializeEnvelope).Select(e => e.Content)
            .ShouldBe(["answer", "answer"]);
    }

    [Theory]
    [InlineData(AgentStreamEventType.RunEnded)]
    [InlineData(AgentStreamEventType.Error)]
    public async Task SendStreamEventAsync_NoContentTurn_EmitsOnePredictableEmptyTerminal(AgentStreamEventType precedingEvent)
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "replyTo": "reply", "streamResponse": true }""",
            null,
            "request-1",
            CancellationToken.None);
        var target = new ChannelStreamTarget(
            ConversationId.From("conv"), SessionId.From("session"), ChannelAddress.From("u"),
            ChannelRequestId: "request-1");

        if (precedingEvent != AgentStreamEventType.RunEnded)
            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = precedingEvent, ErrorMessage = "failed" });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        var envelope = factory.Senders["reply"].SentMessages.ShouldHaveSingleItem();
        var terminal = DeserializeEnvelope(envelope);
        terminal.Type.ShouldBe("done");
        terminal.Content.ShouldBeEmpty();
        terminal.IsFinal.ShouldBeTrue();
    }

    [Fact]
    public async Task SendStreamEventAsync_ConcurrentSameConversation_UsesExactRequestContext()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "a", "senderId": "u", "conversationId": "conv", "replyTo": "reply-a", "correlationId": "corr-a", "streamResponse": true }""",
            null, "request-a", CancellationToken.None);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "b", "senderId": "u", "conversationId": "conv", "replyTo": "reply-b", "correlationId": "corr-b", "streamResponse": true }""",
            null, "request-b", CancellationToken.None);
        var targetA = new ChannelStreamTarget(ConversationId.From("conv"), SessionId.From("a"), ChannelAddress.From("conv"), ChannelRequestId: "request-a");
        var targetB = new ChannelStreamTarget(ConversationId.From("conv"), SessionId.From("b"), ChannelAddress.From("conv"), ChannelRequestId: "request-b");

        await adapter.SendStreamEventAsync(targetB, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "B" });
        await adapter.SendStreamEventAsync(targetA, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "A" });
        await adapter.SendStreamEventAsync(targetB, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });
        await adapter.SendStreamEventAsync(targetA, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        factory.Senders["reply-a"].SentMessages.Select(DeserializeEnvelope).Select(e => e.Content).ShouldBe(["A", "A"]);
        factory.Senders["reply-b"].SentMessages.Select(DeserializeEnvelope).Select(e => e.Content).ShouldBe(["B", "B"]);
        factory.Senders["reply-a"].SentMessages.ShouldAllBe(m => m.CorrelationId == "corr-a");
        factory.Senders["reply-b"].SentMessages.ShouldAllBe(m => m.CorrelationId == "corr-b");
    }

    [Fact]
    public async Task SendAsync_FailedFallbackSend_RetainsQueuedContextForRetry()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "conversationId": "conv", "replyTo": "reply", "correlationId": "corr" }""",
            null, "request-1", CancellationToken.None);
        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv"),
            Content = "answer",
        };
        factory.FailNextSend = true;

        await Should.ThrowAsync<InvalidOperationException>(() => adapter.SendAsync(outbound));
        await adapter.SendAsync(outbound);

        factory.Senders["reply"].SentMessages.ShouldHaveSingleItem().CorrelationId.ShouldBe("corr");
    }

    [Fact]
    public async Task SendAsync_FailedSend_RetainsExactContextForRetry()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory: factory);
        StartAdapter(adapter);
        await adapter.HandleMessageBodyAsync(
            """{ "content": "q", "senderId": "u", "conversationId": "conv", "replyTo": "reply", "correlationId": "corr" }""",
            null, "request-1", CancellationToken.None);
        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conv"),
            Content = "answer",
            ChannelRequestId = "request-1",
        };
        factory.FailNextSend = true;

        await Should.ThrowAsync<InvalidOperationException>(() => adapter.SendAsync(outbound));
        await adapter.SendAsync(outbound);

        factory.Senders["reply"].SentMessages.ShouldHaveSingleItem().CorrelationId.ShouldBe("corr");
    }

    private static ServiceBusOutboundEnvelope DeserializeEnvelope(ServiceBusMessage message)
        => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(
            message.Body.ToString(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Expected a valid outbound envelope.");

    // ── Auth-mode resolution (issue #2002) ─────────────────────────────────────

    [Fact]
    public void ResolveAuthMode_prefers_connection_string_when_both_set()
    {
        var options = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake/;SharedAccessKeyName=x;SharedAccessKey=y=",
            FullyQualifiedNamespace = "ns.servicebus.windows.net",
        };

        ServiceBusChannelAdapter.ResolveAuthMode(options).ShouldBe(ServiceBusAuthMode.ConnectionString);
    }

    [Fact]
    public void ResolveAuthMode_uses_managed_identity_when_only_namespace_set()
    {
        var options = new ServiceBusChannelOptions
        {
            FullyQualifiedNamespace = "ns.servicebus.windows.net",
        };

        ServiceBusChannelAdapter.ResolveAuthMode(options).ShouldBe(ServiceBusAuthMode.ManagedIdentity);
    }

    [Fact]
    public void ResolveAuthMode_returns_none_when_neither_set()
    {
        var options = new ServiceBusChannelOptions();

        ServiceBusChannelAdapter.ResolveAuthMode(options).ShouldBe(ServiceBusAuthMode.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAuthMode_treats_blank_connection_string_as_managed_identity(string blank)
    {
        var options = new ServiceBusChannelOptions
        {
            ConnectionString = blank,
            FullyQualifiedNamespace = "ns.servicebus.windows.net",
        };

        ServiceBusChannelAdapter.ResolveAuthMode(options).ShouldBe(ServiceBusAuthMode.ManagedIdentity);
    }

    // ── Late-load config binding (issue #2010 / servicebus options-binding fix) ─

    [Fact]
    public void ResolveOptions_binds_from_configuration_when_options_empty()
    {
        // Simulates the live gateway: the extension is loaded after the IOptions binding
        // pass, so IOptions<T> is empty and the adapter must self-bind from IConfiguration
        // under "channels:servicebus".
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:servicebus:fullyQualifiedNamespace"] = "botnexus-sbus.servicebus.windows.net",
                ["channels:servicebus:inboundQueueName"] = "botnexus-inbound",
                ["channels:servicebus:defaultReplyQueueName"] = "botnexus-outbound",
            })
            .Build();

        var resolved = ServiceBusChannelAdapter.ResolveOptions(
            new OptionsWrapper<ServiceBusChannelOptions>(new ServiceBusChannelOptions()),
            config);

        resolved.FullyQualifiedNamespace.ShouldBe("botnexus-sbus.servicebus.windows.net");
        resolved.InboundQueueName.ShouldBe("botnexus-inbound");
        resolved.DefaultReplyQueueName.ShouldBe("botnexus-outbound");
        ServiceBusChannelAdapter.ResolveAuthMode(resolved).ShouldBe(ServiceBusAuthMode.ManagedIdentity);
    }

    [Fact]
    public void ResolveOptions_prefers_injected_options_when_auth_present()
    {
        // When IOptions already carries auth material (e.g. tests, or an early-bound host),
        // the injected options win and configuration is ignored.
        var injected = new ServiceBusChannelOptions
        {
            ConnectionString = "Endpoint=sb://fake/;SharedAccessKeyName=x;SharedAccessKey=y=",
            InboundQueueName = "injected-inbound",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:servicebus:fullyQualifiedNamespace"] = "should-be-ignored.servicebus.windows.net",
            })
            .Build();

        var resolved = ServiceBusChannelAdapter.ResolveOptions(
            new OptionsWrapper<ServiceBusChannelOptions>(injected), config);

        resolved.InboundQueueName.ShouldBe("injected-inbound");
        resolved.FullyQualifiedNamespace.ShouldBeNull();
    }

    [Fact]
    public void ResolveOptions_returns_injected_options_when_configuration_null()
    {
        var injected = new ServiceBusChannelOptions();

        var resolved = ServiceBusChannelAdapter.ResolveOptions(
            new OptionsWrapper<ServiceBusChannelOptions>(injected), configuration: null);

        resolved.ShouldBeSameAs(injected);
    }
}
