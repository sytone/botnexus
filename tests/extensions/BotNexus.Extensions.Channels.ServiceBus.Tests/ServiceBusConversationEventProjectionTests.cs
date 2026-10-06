using System.Collections.Immutable;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// Real-collaborator coverage for the conversation publisher to Service Bus projection seam.
/// The Azure transport alone is replaced with the in-memory sender factory.
/// </summary>
public sealed class ServiceBusConversationEventProjectionTests
{
    [Fact]
    public async Task Publisher_ProjectsOnlyOriginRequestInOrder_AndCleansPendingStateOnCompletion()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory);
        StartAdapter(adapter);

        await RegisterPendingRequestAsync(adapter, "request-a", "reply-a", "corr-a");
        await RegisterPendingRequestAsync(adapter, "request-b", "reply-b", "corr-b");

        var conversationId = ConversationId.From("external-conversation");
        var sessionId = SessionId.From("session-a");
        var originBindingId = BindingId.From("servicebus-origin");
        var bindings = ImmutableArray.Create(
            Binding(originBindingId, "servicebus", "external-conversation", BindingMode.Interactive),
            Binding(BindingId.From("servicebus-observer"), "servicebus", "external-conversation", BindingMode.Interactive),
            Binding(BindingId.From("servicebus-muted"), "servicebus", "external-conversation", BindingMode.Muted),
            Binding(BindingId.From("telegram-observer"), "telegram", "chat-1", BindingMode.Interactive));

        await using var publisher = new ConversationEventPublisher(
            [(IConversationEventSink)adapter],
            logger: NullLogger<ConversationEventPublisher>.Instance);

        foreach (var streamEvent in new[]
                 {
                     new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello " },
                     new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world" },
                     new AgentStreamEvent { Type = AgentStreamEventType.RunEnded },
                 })
        {
            var accepted = await publisher.PublishAsync(new ConversationAgentEvent
            {
                AgentId = AgentId.From("agent-a"),
                ConversationId = conversationId,
                SessionId = sessionId,
                Origin = new ConversationEventOrigin(originBindingId, CorrelationId: "request-a"),
                Bindings = bindings,
                StreamEvent = streamEvent with
                {
                    AgentId = AgentId.From("agent-a"),
                    ConversationId = conversationId,
                    SessionId = sessionId,
                },
            });

            accepted.ShouldBeTrue();
        }

        await publisher.WaitForDrainAsync(CancellationToken.None);

        var projected = factory.Senders["reply-a"].SentMessages
            .Select(message => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(message.Body.ToString()))
            .Select(envelope => envelope ?? throw new InvalidOperationException("Projection emitted an invalid envelope."))
            .ToArray();

        projected.Select(envelope => envelope.Type).ShouldBe(["delta", "delta", "done"]);
        projected.Select(envelope => envelope.Sequence).ShouldBe([0L, 1L, 2L]);
        projected.Select(envelope => envelope.Content).ShouldBe(["Hello ", "world", "Hello world"]);
        projected.Select(envelope => envelope.CorrelationId).ShouldAllBe(correlation => correlation == "corr-a");
        factory.Senders.ShouldNotContainKey("reply-b");

        await publisher.PublishAsync(new ConversationAgentEvent
        {
            AgentId = AgentId.From("agent-a"),
            ConversationId = conversationId,
            SessionId = sessionId,
            Origin = new ConversationEventOrigin(originBindingId, CorrelationId: "request-a"),
            Bindings = bindings,
            StreamEvent = new AgentStreamEvent
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = "late",
                AgentId = AgentId.From("agent-a"),
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        });
        await publisher.WaitForDrainAsync(CancellationToken.None);

        factory.Senders["reply-a"].SentMessages.Count.ShouldBe(3);
        factory.Senders.ShouldNotContainKey("reply-b");

        foreach (var streamEvent in new[]
                 {
                     new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Second" },
                     new AgentStreamEvent { Type = AgentStreamEventType.RunEnded },
                 })
        {
            await publisher.PublishAsync(new ConversationAgentEvent
            {
                AgentId = AgentId.From("agent-a"),
                ConversationId = conversationId,
                SessionId = SessionId.From("session-b"),
                Origin = new ConversationEventOrigin(originBindingId, CorrelationId: "request-b"),
                Bindings = bindings,
                StreamEvent = streamEvent with
                {
                    AgentId = AgentId.From("agent-a"),
                    ConversationId = conversationId,
                    SessionId = SessionId.From("session-b"),
                },
            });
        }
        await publisher.WaitForDrainAsync(CancellationToken.None);

        var secondProjection = factory.Senders["reply-b"].SentMessages
            .Select(message => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(message.Body.ToString()))
            .Select(envelope => envelope ?? throw new InvalidOperationException("Projection emitted an invalid envelope."))
            .ToArray();

        secondProjection.Select(envelope => envelope.Type).ShouldBe(["delta", "done"]);
        secondProjection.Select(envelope => envelope.Sequence).ShouldBe([0L, 1L]);
        secondProjection.Select(envelope => envelope.Content).ShouldBe(["Second", "Second"]);
        secondProjection.Select(envelope => envelope.CorrelationId).ShouldAllBe(correlation => correlation == "corr-b");
        factory.Senders["reply-a"].SentMessages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Publisher_UnrelatedMutedAndUnsupportedEvents_DoNotSendOrConsumePendingRequest()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateAdapter(factory);
        StartAdapter(adapter);
        await RegisterPendingRequestAsync(adapter, "request-a", "reply-a", "corr-a");

        var conversationId = ConversationId.From("external-conversation");
        var sessionId = SessionId.From("session-a");
        var bindingId = BindingId.From("servicebus-origin");
        var applicable = Binding(bindingId, "servicebus", "external-conversation", BindingMode.Interactive);
        await using var publisher = new ConversationEventPublisher(
            [(IConversationEventSink)adapter],
            logger: NullLogger<ConversationEventPublisher>.Instance);

        // An unrelated binding, a muted origin and a conversation without a Service Bus binding
        // must not consume the pending request or emit a transport message.
        foreach (var bindings in new[]
                 {
                     ImmutableArray.Create(Binding(BindingId.From("telegram-binding"), "telegram", "chat-1", BindingMode.Interactive)),
                     ImmutableArray.Create(Binding(bindingId, "servicebus", "external-conversation", BindingMode.Muted)),
                     ImmutableArray<ConversationBindingSnapshot>.Empty,
                     ImmutableArray.Create(Binding(bindingId, "servicebus", "other-conversation", BindingMode.Interactive)),
                 })
        {
            (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, bindingId, bindings,
                AgentStreamEventType.ContentDelta, "not for Service Bus"))).ShouldBeTrue();
            (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, bindingId, bindings,
                AgentStreamEventType.RunEnded))).ShouldBeTrue();
        }

        (await publisher.PublishAsync(new ConversationCreatedEvent
        {
            AgentId = AgentId.From("agent-a"),
            ConversationId = conversationId,
            Bindings = ImmutableArray.Create(applicable),
            Title = "lifecycle only",
        })).ShouldBeTrue();

        await publisher.WaitForDrainAsync(CancellationToken.None);
        factory.Senders.ShouldNotContainKey("reply-a");

        // A positive event after the no-ops proves the pending context was not silently retired.
        var bindingsForReply = ImmutableArray.Create(applicable);
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, bindingId, bindingsForReply,
            AgentStreamEventType.ContentDelta, "delivered"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, bindingId, bindingsForReply,
            AgentStreamEventType.RunEnded))).ShouldBeTrue();
        await publisher.WaitForDrainAsync(CancellationToken.None);

        var projected = factory.Senders["reply-a"].SentMessages
            .Select(message => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(message.Body.ToString())
                ?? throw new InvalidOperationException("Projection emitted an invalid envelope."))
            .ToArray();
        projected.Select(envelope => envelope.Type).ShouldBe(["delta", "done"]);
        projected.Select(envelope => envelope.Content).ShouldBe(["delivered", "delivered"]);
        projected.Select(envelope => envelope.CorrelationId).ShouldAllBe(correlation => correlation == "corr-a");
    }

    private static ConversationAgentEvent AgentEvent(
        ConversationId conversationId,
        SessionId sessionId,
        BindingId originBindingId,
        ImmutableArray<ConversationBindingSnapshot> bindings,
        AgentStreamEventType type,
        string? contentDelta = null)
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            ConversationId = conversationId,
            SessionId = sessionId,
            Origin = new ConversationEventOrigin(originBindingId, CorrelationId: "request-a"),
            Bindings = bindings,
            StreamEvent = new AgentStreamEvent
            {
                Type = type,
                ContentDelta = contentDelta,
                AgentId = AgentId.From("agent-a"),
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        };

    private static ConversationBindingSnapshot Binding(
        BindingId bindingId,
        string channelType,
        string channelAddress,
        BindingMode mode)
        => new(
            bindingId,
            ChannelKey.From(channelType),
            AdapterId: null,
            ChannelAddress.From(channelAddress),
            mode,
            ThreadingMode.Single);

    private static ServiceBusChannelAdapter CreateAdapter(FakeServiceBusAdapterClientFactory factory)
        => new(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            new OptionsWrapper<ServiceBusChannelOptions>(new ServiceBusChannelOptions
            {
                ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
                InboundQueueName = "test-inbound",
                DefaultReplyQueueName = "test-outbound",
            }),
            factory);

    private static void StartAdapter(ServiceBusChannelAdapter adapter)
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(candidate => candidate.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        adapter.StartAsync(dispatcher.Object).GetAwaiter().GetResult();
    }

    private static Task RegisterPendingRequestAsync(
        ServiceBusChannelAdapter adapter,
        string requestId,
        string replyQueue,
        string correlationId)
        => adapter.HandleMessageBodyAsync(
            $$"""{ "content": "question", "senderId": "user", "conversationId": "external-conversation", "replyTo": "{{replyQueue}}", "correlationId": "{{correlationId}}", "streamResponse": true }""",
            applicationProperties: null,
            messageId: requestId,
            CancellationToken.None);
}
