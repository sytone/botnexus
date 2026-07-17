using Azure.Messaging.ServiceBus;
using BotNexus.TeamsProxy.Configuration;
using BotNexus.TeamsProxy.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.TeamsProxy.Services;

/// <summary>
/// Publishes an inbound Teams message to the BotNexus Service Bus inbound queue
/// using the <see cref="ServiceBusInboundEnvelope"/> contract from PR #215.
/// Also stores the Teams routing context in <see cref="ConversationContextStore"/>
/// so the outbound worker can retrieve it when sending the reply.
/// </summary>
public sealed class InboundQueuePublisher : IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ConversationContextStore _contextStore;
    private readonly TeamsProxyOptions _options;

    public InboundQueuePublisher(
        ServiceBusClient serviceBusClient,
        ConversationContextStore contextStore,
        IOptions<TeamsProxyOptions> options)
    {
        _sender = serviceBusClient.CreateSender(options.Value.InboundQueueName);
        _contextStore = contextStore;
        _options = options.Value;
    }

    public async Task PublishAsync(
        BotNexusInboundMessage inboundMessage,
        CancellationToken cancellationToken)
    {
        // Preserve Teams routing data so the outbound worker can reply to this conversation.
        _contextStore.Store(inboundMessage.ConversationId, new TeamsConversationContext(
            ConversationId: inboundMessage.ConversationId,
            ServiceUrl: inboundMessage.ServiceUrl,
            ChannelId: inboundMessage.ChannelId,
            ActivityId: inboundMessage.ActivityId,
            From: inboundMessage.From,
            Recipient: inboundMessage.Recipient,
            Conversation: inboundMessage.RawActivity.Conversation));

        var correlationId = Guid.NewGuid().ToString();

        var metadata = new Dictionary<string, object?>
        {
            ["teams.serviceUrl"] = inboundMessage.ServiceUrl,
            ["teams.activityId"] = inboundMessage.ActivityId,
            ["teams.channelId"] = inboundMessage.ChannelId,
            ["teams.tenantId"] = inboundMessage.TenantId,
        };

        if (inboundMessage.From is not null)
        {
            metadata["teams.from.id"] = inboundMessage.From.Id;
            metadata["teams.from.name"] = inboundMessage.From.Name;
        }

        if (inboundMessage.Recipient is not null)
        {
            metadata["teams.recipient.id"] = inboundMessage.Recipient.Id;
            metadata["teams.recipient.name"] = inboundMessage.Recipient.Name;
        }

        var envelope = new ServiceBusInboundEnvelope
        {
            MessageId = inboundMessage.MessageId,
            CorrelationId = correlationId,
            AgentId = _options.AgentId,
            ConversationId = inboundMessage.ConversationId,
            SessionId = string.IsNullOrWhiteSpace(_options.SessionId) ? null : _options.SessionId,
            SenderId = inboundMessage.From?.Id ?? inboundMessage.From?.Name,
            Role = "user",
            Content = inboundMessage.Text ?? string.Empty,
            ReplyTo = _options.OutboundQueueName,
            Timestamp = inboundMessage.ReceivedAt,
            StreamResponse = _options.StreamResponses,
            Metadata = metadata
        };

        var serviceBusMessage = new ServiceBusMessage(
            BinaryData.FromObjectAsJson(envelope, JsonDefaults.Options))
        {
            ContentType = "application/json",
            MessageId = envelope.MessageId,
            CorrelationId = correlationId,
            Subject = "teams.message.received"
        };

        serviceBusMessage.ApplicationProperties["conversationId"] = inboundMessage.ConversationId;
        serviceBusMessage.ApplicationProperties["agentId"] = _options.AgentId;
        serviceBusMessage.ApplicationProperties["senderId"] = envelope.SenderId ?? string.Empty;
        serviceBusMessage.ApplicationProperties["replyTo"] = _options.OutboundQueueName;

        await _sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
    }
}
