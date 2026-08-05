using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ServiceBusStreaming.Tests;

/// <summary>
/// #2815: outbound Service Bus reply envelopes were carrying an agent name (or an internal
/// <c>c_</c> conversation id) in <c>conversationId</c> instead of the originating external Teams
/// address, and the fail-closed Teams relay dead-lettered every one of them.
/// </summary>
/// <remarks>
/// These tests drive the REAL <see cref="ServiceBusChannelAdapter"/> inbound handler and then the
/// REAL outbound send through the REAL <see cref="InternalChannelAdapter"/> re-target. A unit test
/// of <c>ResolveOutboundConversationId</c> alone would pass vacuously, because the defect lives in
/// the plumbing that decides which address reaches that resolver.
/// </remarks>
public sealed class ServiceBusOutboundConversationAddressTests
{
    private const string TeamsAddress = "19:be491e6224514806a319684cc8a98cf0@thread.v2";
    private const string AgentId = "tinker";
    private const string CorrelationId = "1785871962264";

    /// <summary>
    /// AC2 (#2815): an outbound reply produced by an internal-origin turn, whose channel address is
    /// the AGENT ID, must be re-addressed onto the conversation's Service Bus binding so the emitted
    /// envelope carries the originating external <c>19:...@thread.v2</c> address.
    /// </summary>
    [Fact]
    public async Task InternalReply_ForServiceBusSession_EmitsOriginatingTeamsAddressAsConversationId()
    {
        var harness = await Harness.CreateAsync(withServiceBusBinding: true);

        await harness.SendInternalReplyAddressedToAgentIdAsync();

        var envelope = harness.SingleEnvelope();
        envelope.ConversationId.ShouldBe(TeamsAddress);
        envelope.ConversationId.ShouldNotBe(AgentId);
    }

    /// <summary>
    /// AC3 (#2815): the same re-addressed reply must also carry the inbound <c>correlationId</c>, so
    /// the relay can pair it even if the address were unavailable. The correlation is recovered from
    /// the existing <c>PendingReplyContext</c> seam keyed by the external address - deliberately NOT
    /// a second notion of "where does this reply go".
    /// </summary>
    [Fact]
    public async Task InternalReply_ForServiceBusSession_CarriesInboundCorrelationId()
    {
        var harness = await Harness.CreateAsync(withServiceBusBinding: true);

        await harness.SendInternalReplyAddressedToAgentIdAsync();

        harness.SingleEnvelope().CorrelationId.ShouldBe(CorrelationId);
    }

    /// <summary>
    /// AC4 (#2815): when no external destination can be recovered, the only available address is the
    /// agent id. The adapter must REFUSE rather than emit an envelope that is certain to dead-letter.
    /// </summary>
    [Fact]
    public async Task Send_WhoseOnlyDestinationIsAnAgentId_IsRefusedAndEmitsNothing()
    {
        var harness = await Harness.CreateAsync(withServiceBusBinding: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => harness.SendInternalReplyAddressedToAgentIdAsync());

        ex.Message.ShouldContain("#2815");
        ex.Message.ShouldContain("a known agent id");
        harness.Envelopes().ShouldBeEmpty();
    }

    /// <summary>
    /// AC4 (#2815), second shape: an internal <c>c_</c> conversation id used as a channel address is
    /// equally undeliverable and must be refused with the same distinct diagnosis.
    /// </summary>
    [Fact]
    public async Task Send_WhoseOnlyDestinationIsAnInternalConversationId_IsRefusedAndEmitsNothing()
    {
        var harness = await Harness.CreateAsync(withServiceBusBinding: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => harness.ServiceBus.SendAsync(new OutboundMessage
            {
                ChannelType = ChannelKey.From("servicebus"),
                ChannelAddress = ChannelAddress.From("c_fa933fa09c5c4435ad297871d91ac18a"),
                Content = "reply",
                SessionId = Harness.SessionIdValue,
            }));

        ex.Message.ShouldContain("#2815");
        ex.Message.ShouldContain("an internal BotNexus conversation id");
        harness.Envelopes().ShouldBeEmpty();
    }

    private sealed class Harness
    {
        internal const string SessionIdValue = "9dcd4b442e994469bf080cd3eb61d516";

        private RecordingServiceBusFactory _transport = null!;
        private InternalChannelAdapter _internal = null!;

        internal ServiceBusChannelAdapter ServiceBus { get; private set; } = null!;

        internal static async Task<Harness> CreateAsync(bool withServiceBusBinding)
        {
            var harness = new Harness();
            harness._transport = new RecordingServiceBusFactory();
            harness.ServiceBus = new ServiceBusChannelAdapter(
                NullLogger<ServiceBusChannelAdapter>.Instance,
                Options.Create(new ServiceBusChannelOptions
                {
                    InboundQueueName = "botnexus-inbound",
                    DefaultReplyQueueName = "botnexus-outbound",
                }),
                harness._transport);
            await harness.ServiceBus.StartAsync(new NoOpDispatcher());

            // REAL inbound handler: the envelope shape the Teams relay actually sends.
            await harness.ServiceBus.HandleMessageBodyAsync(
                $$"""
                {
                  "content": "question",
                  "senderId": "user@example.com",
                  "agentId": "{{AgentId}}",
                  "conversationId": "{{TeamsAddress}}",
                  "replyTo": "botnexus-outbound",
                  "correlationId": "{{CorrelationId}}"
                }
                """,
                null,
                "inbound-172",
                CancellationToken.None);

            var sessionStore = new InMemorySessionStore();
            var sessionId = SessionId.From(SessionIdValue);
            var session = await sessionStore.GetOrCreateAsync(sessionId, BotNexus.Domain.Primitives.AgentId.From(AgentId));
            var conversation = ConversationFactory.CreateForChannel(
                ConversationId.Create(),
                BotNexus.Domain.Primitives.AgentId.From(AgentId));
            if (withServiceBusBinding)
            {
                conversation.ChannelBindings.Add(new ChannelBinding
                {
                    ChannelType = ChannelKey.From("servicebus"),
                    ChannelAddress = ChannelAddress.From(TeamsAddress),
                    Mode = BindingMode.Interactive,
                });
            }

            var conversationStore = new InMemoryConversationStore();
            _ = await conversationStore.CreateAsync(conversation);

            session.ConversationId = conversation.ConversationId;
            session.ChannelType = ChannelKey.From("servicebus");
            await sessionStore.SaveAsync(session);

            var services = new ServiceCollection();
            services.AddSingleton<IConversationStore>(conversationStore);
            services.AddSingleton<IChannelManager>(new ChannelManager([harness.ServiceBus]));

            harness._internal = new InternalChannelAdapter(
                services.BuildServiceProvider(),
                sessionStore,
                NullLogger<InternalChannelAdapter>.Instance);

            return harness;
        }

        /// <summary>
        /// The observed defect shape: an internal-origin outbound whose ChannelAddress is the agent
        /// id (as built by ConversationTool / ConversationCronFailureAlertSink / AskUserCheckpointResumer),
        /// with NO ChannelRequestId and NO adapter metadata - exactly the dead-lettered envelopes'
        /// signature of correlationId: null AND agentId: null.
        /// </summary>
        internal Task SendInternalReplyAddressedToAgentIdAsync()
            => _internal.SendAsync(new OutboundMessage
            {
                ChannelType = ChannelKey.From("internal"),
                ChannelAddress = ChannelAddress.From(AgentId),
                Content = "the agent's reply",
                SessionId = SessionIdValue,
            });

        internal IReadOnlyList<ServiceBusOutboundEnvelope> Envelopes()
            => _transport.Sender.Messages
                .Select(m => JsonSerializer.Deserialize<ServiceBusOutboundEnvelope>(
                    m.Body.ToString(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToList();

        internal ServiceBusOutboundEnvelope SingleEnvelope() => Envelopes().ShouldHaveSingleItem();
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
