using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ServiceBusStreaming.Tests;

public sealed class ServiceBusStreamingSeamTests
{
    [Fact]
    public async Task StreamedDeltas_ThroughRealProjectionAndPersistence_ConsolidateVerbatim()
    {
        const string expected = """
            ## Formatting Test

            This has **bold**, *italic*, and `inline code`.

            [BotNexus](https://github.com/Sytone/botnexus)

            ```powershell
            botnexus gateway status
            ```

            | Component | Status |
            |---|---|
            | Gateway | Running |
            """;
        string[] deltas =
        [
            "##", " Formatting", " Test\n\nThis", " has", " **", "bold", "**, *italic*, and `inline code`.\n\n[",
            "BotNexus](https://github.com/Sytone/botnexus)\n\n```powershell\nbotnexus gateway status\n```\n\n",
            "| Component | Status |\n|---|---|\n| Gateway | Running |"
        ];
        var transport = new RecordingServiceBusFactory();
        var adapter = new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            Options.Create(new ServiceBusChannelOptions
            {
                InboundQueueName = "inbound",
                DefaultReplyQueueName = "outbound",
            }),
            transport);
        await adapter.StartAsync(new NoOpDispatcher());
        await adapter.HandleMessageBodyAsync(
            """{ "content": "question", "senderId": "user", "conversationId": "conversation", "replyTo": "reply", "correlationId": "correlation", "streamResponse": true }""",
            null,
            "request",
            CancellationToken.None);

        var sessionStore = new InMemorySessionStore();
        var sessionId = SessionId.From("session");
        var session = await sessionStore.GetOrCreateAsync(sessionId, AgentId.From("agent"));
        var target = new ChannelStreamTarget(
            ConversationId.From("conversation"),
            sessionId,
            ChannelAddress.From("conversation"),
            ChannelRequestId: "request");
        var events = deltas
            .Select(delta => new AgentStreamEvent
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = delta,
            })
            .Append(new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(events),
            session,
            sessionStore,
            new StreamingSessionOptions(OnEventAsync: (evt, ct) =>
                new ValueTask(adapter.SendStreamEventAsync(target, evt, ct))));

        var envelopes = transport.Sender.Messages.Select(Deserialize).ToList();
        envelopes.Where(envelope => envelope.Type == "delta")
            .Select(envelope => envelope.Content)
            .ShouldBe(deltas);
        var terminal = envelopes.Single(envelope => envelope.IsFinal);
        terminal.Type.ShouldBe("done");
        terminal.Content.ShouldBe(expected);
        terminal.Content.ShouldNotContain("\r\nAg\r\nreed");
        result.AssistantContent.ShouldBe(expected);

        var oneShotTransport = new RecordingServiceBusFactory();
        var oneShotAdapter = new ServiceBusChannelAdapter(
            NullLogger<ServiceBusChannelAdapter>.Instance,
            Options.Create(new ServiceBusChannelOptions
            {
                InboundQueueName = "inbound",
                DefaultReplyQueueName = "outbound",
            }),
            oneShotTransport);
        await oneShotAdapter.StartAsync(new NoOpDispatcher());
        await oneShotAdapter.HandleMessageBodyAsync(
            """{ "content": "question", "senderId": "user", "conversationId": "conversation", "replyTo": "reply", "correlationId": "correlation" }""",
            null,
            "one-shot-request",
            CancellationToken.None);
        await oneShotAdapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("conversation"),
            ChannelRequestId = "one-shot-request",
            Content = expected,
        });
        var oneShotTerminal = Deserialize(oneShotTransport.Sender.Messages.ShouldHaveSingleItem());
        oneShotTerminal.Type.ShouldBe("done");
        oneShotTerminal.IsFinal.ShouldBeTrue();
        oneShotTerminal.Content.ShouldBe(expected);
        terminal.Content.ShouldBe(oneShotTerminal.Content);

        var persisted = await sessionStore.GetAsync(sessionId);
        persisted.ShouldNotBeNull();
        persisted.History.Single(entry => entry.Role == MessageRole.Assistant)
            .Content.ShouldBe(expected);
    }

    private static ServiceBusOutboundEnvelope Deserialize(ServiceBusMessage message)
        => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(message.Body.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Service Bus envelope was null.");

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingServiceBusFactory : IServiceBusAdapterClientFactory
    {
        public RecordingSender Sender { get; } = new();

        public ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options)
            => new RecordingProcessor();

        public IServiceBusSenderWrapper CreateSender(string queueName) => Sender;
    }

    private sealed class RecordingProcessor : ServiceBusProcessor
    {
        public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSender : IServiceBusSenderWrapper
    {
        public List<ServiceBusMessage> Messages { get; } = [];

        public Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
