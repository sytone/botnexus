using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ServiceBusStreaming.Tests;

/// <summary>
/// Exercises the real publisher/adapter seam with overlapping inbound work, not two sequential
/// streams. Only the Azure transport is replaced; the dispatcher below is the test's event driver,
/// not a substitute for either collaborator at the boundary under test.
/// </summary>
public sealed class ServiceBusConcurrentProjectionSeamTests
{
    private static readonly ConversationId Conversation = ConversationId.From("shared-conversation");
    private static readonly BindingId Origin = BindingId.From("servicebus-origin");
    private static readonly ImmutableArray<ConversationBindingSnapshot> Bindings =
    [
        Binding(Origin, "servicebus", BindingMode.Interactive),
        Binding(BindingId.From("servicebus-observer"), "servicebus", BindingMode.Interactive),
        Binding(BindingId.From("servicebus-muted"), "servicebus", BindingMode.Muted),
        Binding(BindingId.From("telegram-observer"), "telegram", BindingMode.Interactive),
    ];

    /// <summary>
    /// Interleaving must preserve request-local text, sequence and transport identity; completing A
    /// must suppress A's late events without retiring B or duplicating delivery to observer bindings.
    /// </summary>
    [Fact]
    public async Task Publisher_OverlappingInboundRequests_KeepOrderedRepliesIsolatedAndRetireOnlyCompletedRequest()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new RecordingServiceBusFactory();
        var adapter = new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            Options.Create(new ServiceBusChannelOptions
            {
                InboundQueueName = "inbound",
                DefaultReplyQueueName = "unexpected-default",
                MaxConcurrentCalls = 2,
            }),
            transport);
        var driver = new InboundEventDriver();
        await adapter.StartAsync(driver, deadline.Token);
        await using var publisher = new ConversationEventPublisher([(IConversationEventSink)adapter]);
        var inboundA = RegisterAsync(adapter, "a", deadline.Token);
        Task inboundB = Task.CompletedTask;
        var scenarioCompleted = false;
        try
        {
            var requestA = await driver.RequestA.Task.WaitAsync(deadline.Token);
            inboundA.IsCompleted.ShouldBeFalse("request A must still be dispatching when B arrives");
            inboundB = RegisterAsync(adapter, "b", deadline.Token);
            var requestB = await driver.RequestB.Task.WaitAsync(deadline.Token);
            inboundA.IsCompleted.ShouldBeFalse("both inbound handlers must overlap");
            inboundB.IsCompleted.ShouldBeFalse("both inbound handlers must overlap");
            requestA.ChannelAddress.ShouldBe(requestB.ChannelAddress);
            requestA.ChannelRequestId.ShouldBe("request-a");
            requestB.ChannelRequestId.ShouldBe("request-b");
            requestA.StreamResponse.ShouldBeTrue();
            requestB.StreamResponse.ShouldBeTrue();

            // Both accumulators are live before either terminal event. The second A delta also
            // distinguishes a request-local sequence from a conversation-global sequence.
            await PublishAsync(publisher, requestA, AgentStreamEventType.ContentDelta, "A<\n", deadline.Token);
            await PublishAsync(publisher, requestB, AgentStreamEventType.ContentDelta, "B **", deadline.Token);
            await PublishAsync(publisher, requestA, AgentStreamEventType.ContentDelta, "&alpha", deadline.Token);
            await PublishAsync(publisher, requestB, AgentStreamEventType.ContentDelta, "beta**", deadline.Token);
            await publisher.WaitForDrainAsync(deadline.Token);
            AssertReplies(transport, "a", ["delta", "delta"], ["A<\n", "&alpha"]);
            AssertReplies(transport, "b", ["delta", "delta"], ["B **", "beta**"]);
            AssertPublicationOrder(transport, "a:0", "b:0", "a:1", "b:1");

            await PublishAsync(publisher, requestA, AgentStreamEventType.RunEnded, null, deadline.Token);
            await publisher.WaitForDrainAsync(deadline.Token);
            driver.CompleteA.TrySetResult(true);
            await inboundA.WaitAsync(deadline.Token);
            inboundB.IsCompleted.ShouldBeFalse("A's terminal cleanup must leave B active");
            AssertReplies(transport, "a", ["delta", "delta", "done"], ["A<\n", "&alpha", "A<\n&alpha"]);
            AssertReplies(transport, "b", ["delta", "delta"], ["B **", "beta**"]);
            await AssertPendingReplyRetiredAsync(adapter, requestA, deadline.Token);

            // Negative assertions happen only after the real publisher's drain boundary. A's
            // late delta and duplicate terminal must not leak into B or the default queue.
            await PublishAsync(publisher, requestA, AgentStreamEventType.ContentDelta, "LATE-A", deadline.Token);
            await PublishAsync(publisher, requestA, AgentStreamEventType.RunEnded, null, deadline.Token);
            await PublishAsync(publisher, requestB, AgentStreamEventType.ContentDelta, "!", deadline.Token);
            await publisher.WaitForDrainAsync(deadline.Token);
            AssertReplies(transport, "a", ["delta", "delta", "done"], ["A<\n", "&alpha", "A<\n&alpha"]);
            AssertReplies(transport, "b", ["delta", "delta", "delta"], ["B **", "beta**", "!"]);

            await PublishAsync(publisher, requestB, AgentStreamEventType.RunEnded, null, deadline.Token);
            await publisher.WaitForDrainAsync(deadline.Token);
            driver.CompleteB.TrySetResult(true);
            await inboundB.WaitAsync(deadline.Token);
            await AssertPendingReplyRetiredAsync(adapter, requestB, deadline.Token);
            await PublishAsync(publisher, requestB, AgentStreamEventType.ContentDelta, "LATE-B", deadline.Token);
            await PublishAsync(publisher, requestB, AgentStreamEventType.RunEnded, null, deadline.Token);
            await publisher.WaitForDrainAsync(deadline.Token);

            AssertReplies(transport, "a", ["delta", "delta", "done"], ["A<\n", "&alpha", "A<\n&alpha"]);
            AssertReplies(transport, "b", ["delta", "delta", "delta", "done"], ["B **", "beta**", "!", "B **beta**!"]);
            AssertPublicationOrder(transport, "a:0", "b:0", "a:1", "b:1", "a:2", "b:2", "b:3");
            transport.Senders.Keys.Order().ToArray().ShouldBe(new[] { "reply-a", "reply-b" },
                "observer bindings and late events must not create additional reply destinations");
            transport.Messages.Select(item => item.Message.MessageId).ShouldBeUnique();
            scenarioCompleted = true;
        }
        finally
        {
            // Release outstanding driver work even when an assertion fails; no orphan dispatch
            // is allowed to outlive the fixture. The deadline bounds broken readiness/drain paths.
            driver.CompleteA.TrySetResult(true);
            driver.CompleteB.TrySetResult(true);
            try
            {
                await Task.WhenAll(inboundA, inboundB).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (!scenarioCompleted)
            {
                // Observe dispatch failure without replacing the scenario's original failure.
                // Shutdown below must also run when the scenario deadline has already expired.
            }
            finally
            {
                await adapter.StopAsync(CancellationToken.None);
            }
        }
    }

    private static Task RegisterAsync(ServiceBusChannelAdapter adapter, string suffix, CancellationToken cancellationToken)
        => adapter.HandleMessageBodyAsync(
            $$"""{ "content": "question-{{suffix}}", "senderId": "user", "conversationId": "{{Conversation.Value}}", "replyTo": "reply-{{suffix}}", "correlationId": "corr-{{suffix}}", "streamResponse": true }""",
            new Dictionary<string, object> { ["request-marker"] = suffix },
            $"request-{suffix}",
            cancellationToken);

    private static async Task PublishAsync(
        ConversationEventPublisher publisher,
        InboundMessage request,
        AgentStreamEventType type,
        string? delta,
        CancellationToken cancellationToken)
    {
        var session = SessionId.From($"session-{request.Content}");
        (await publisher.PublishAsync(new ConversationAgentEvent
        {
            ConversationId = Conversation,
            AgentId = AgentId.From("agent"),
            SessionId = session,
            Origin = new ConversationEventOrigin(Origin, CorrelationId: request.ChannelRequestId),
            Bindings = Bindings,
            StreamEvent = new AgentStreamEvent
            {
                Type = type,
                ContentDelta = delta,
                ConversationId = Conversation,
                AgentId = AgentId.From("agent"),
                SessionId = session,
            },
        }, cancellationToken)).ShouldBeTrue("the real publisher must accept every test event");
    }

    private static async Task AssertPendingReplyRetiredAsync(
        ServiceBusChannelAdapter adapter, InboundMessage request, CancellationToken cancellationToken)
    {
        // Unlike a duplicate terminal (always harmless), a direct delta requires a live pending
        // context. This observable contract proves cleanup, without inspecting private indexes.
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => adapter.SendStreamEventAsync(
            new ChannelStreamTarget(Conversation, SessionId.From($"session-{request.Content}"),
                request.ChannelAddress, ChannelRequestId: request.ChannelRequestId),
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "retired-probe" },
            cancellationToken));
        exception.Message.ShouldBe($"No pending Service Bus reply context exists for request '{request.ChannelRequestId}'.");
    }

    private static void AssertReplies(RecordingServiceBusFactory transport, string suffix, string[] types, string[] content)
    {
        var messages = transport.Messages.Where(item => item.Queue == $"reply-{suffix}").Select(item => item.Message).ToArray();
        var envelopes = messages.Select(Deserialize).ToArray();
        envelopes.Select(item => item.Type).ShouldBe(types, "exact counts also reject duplicate observer delivery");
        envelopes.Select(item => item.Content).ShouldBe(content, "stream text must remain request-local and verbatim");
        envelopes.Select(item => item.Sequence).ShouldBe(Enumerable.Range(0, types.Length).Select(index => (long)index));
        envelopes.Select(item => item.IsFinal).ShouldBe(types.Select(type => type == "done"));
        envelopes.Select(item => item.CorrelationId).ShouldAllBe(value => value == $"corr-{suffix}");
        envelopes.Select(item => item.ConversationId).ShouldAllBe(value => value == Conversation.Value);
        envelopes.Select(item => item.SessionId).ShouldAllBe(value => value == $"session-question-{suffix}");
        envelopes.Select(item => item.AgentId).ShouldAllBe(value => value == "agent");
        envelopes.Select(item => item.Role).ShouldAllBe(value => value == "assistant");
        foreach (var message in messages)
        {
            var envelope = Deserialize(message);
            message.ContentType.ShouldBe("application/json");
            message.CorrelationId.ShouldBe($"corr-{suffix}");
            message.MessageId.ShouldBe(envelope.MessageId);
            message.ApplicationProperties["request-marker"].ShouldBe(suffix);
            message.ApplicationProperties["type"].ShouldBe(envelope.Type);
            message.ApplicationProperties["sequence"].ShouldBe(envelope.Sequence);
            message.ApplicationProperties["isFinal"].ShouldBe(envelope.IsFinal);
        }
    }

    private static void AssertPublicationOrder(RecordingServiceBusFactory transport, params string[] expected)
        => transport.Messages.Select(item => $"{item.Queue["reply-".Length..]}:{Deserialize(item.Message).Sequence}")
            .ShouldBe(expected, "one conversation's serialized output must follow interleaved publication order");

    private static ServiceBusOutboundEnvelope Deserialize(ServiceBusMessage message)
        => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(message.Body.ToString())
            ?? throw new InvalidOperationException("Service Bus emitted a null envelope.");

    private static ConversationBindingSnapshot Binding(BindingId id, string channel, BindingMode mode)
        => new(id, ChannelKey.From(channel), null, ChannelAddress.From(Conversation.Value), mode, ThreadingMode.Single);

    private sealed class InboundEventDriver : IChannelDispatcher
    {
        public TaskCompletionSource<InboundMessage> RequestA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InboundMessage> RequestB { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CompleteA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CompleteB { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            var isA = message.ChannelRequestId == "request-a";
            (isA ? RequestA : RequestB).TrySetResult(message);
            await (isA ? CompleteA : CompleteB).Task.WaitAsync(cancellationToken);
        }
    }

    // Same Azure-only recording factory pattern as the existing streaming seam, extended to
    // retain actual destination queues and cross-queue send order safely across the pump thread.
    private sealed class RecordingServiceBusFactory : IServiceBusAdapterClientFactory
    {
        public ConcurrentQueue<(string Queue, ServiceBusMessage Message)> Messages { get; } = new();
        public ConcurrentDictionary<string, IServiceBusSenderWrapper> Senders { get; } = new();

        public ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options)
            => new RecordingProcessor();

        public IServiceBusSenderWrapper CreateSender(string queueName)
            => Senders.GetOrAdd(queueName, queue => new RecordingSender(queue, Messages));
    }

    private sealed class RecordingProcessor : ServiceBusProcessor
    {
        public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSender(string queue, ConcurrentQueue<(string Queue, ServiceBusMessage Message)> messages)
        : IServiceBusSenderWrapper
    {
        public Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            messages.Enqueue((queue, message));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
