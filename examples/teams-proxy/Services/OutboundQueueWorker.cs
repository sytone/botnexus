using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.TeamsProxy.Configuration;
using BotNexus.TeamsProxy.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.TeamsProxy.Services;

/// <summary>
/// Background worker that reads BotNexus reply envelopes from the Service Bus outbound queue
/// and sends them back to the originating Teams conversation via the Bot Connector REST API.
/// Deserializes the <see cref="ServiceBusOutboundEnvelope"/> contract from PR #215.
/// </summary>
public sealed class OutboundQueueWorker : BackgroundService
{
    private readonly BotConnectorClient _botConnectorClient;
    private readonly ConversationContextStore _contextStore;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<OutboundQueueWorker> _logger;
    private readonly TeamsProxyOptions _options;

    public OutboundQueueWorker(
        ServiceBusClient serviceBusClient,
        BotConnectorClient botConnectorClient,
        ConversationContextStore contextStore,
        IOptions<TeamsProxyOptions> options,
        ILogger<OutboundQueueWorker> logger)
    {
        _serviceBusClient = serviceBusClient;
        _botConnectorClient = botConnectorClient;
        _contextStore = contextStore;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = _serviceBusClient.CreateProcessor(
            _options.OutboundQueueName,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = Math.Max(1, _options.OutboundMaxConcurrentCalls)
            });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation(
            "Started outbound Teams queue processor for {QueueName}.",
            _options.OutboundQueueName);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
            await processor.DisposeAsync();
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        ServiceBusOutboundEnvelope? envelope;

        try
        {
            envelope = args.Message.Body.ToObjectFromJson<ServiceBusOutboundEnvelope>(
                JsonDefaults.Options);
        }
        catch (JsonException exception)
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "InvalidJson",
                exception.Message,
                args.CancellationToken);
            return;
        }

        if (envelope is null)
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "EmptyPayload",
                "Outbound envelope payload was empty.",
                args.CancellationToken);
            return;
        }

        // This sample demonstrates pseudo-streaming: request deltas for responsive consumers,
        // but ignore them here and send only the consolidated terminal response to Teams.
        if (!envelope.IsFinal || string.Equals(envelope.Type, "delta", StringComparison.OrdinalIgnoreCase))
        {
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(envelope.ConversationId))
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "MissingConversationId",
                "Outbound envelope is missing conversationId.",
                args.CancellationToken);
            return;
        }

        var context = _contextStore.TryGet(envelope.ConversationId);
        if (context is null)
        {
            // The context store is in-memory: it is populated on inbound publish within the same
            // process lifetime. A missing context means the message arrived for a conversation
            // this instance never handled (e.g., after a restart). Dead-letter so it can be
            // inspected; in production, consider a persistent context store instead.
            _logger.LogWarning(
                "No Teams routing context found for conversationId {ConversationId}; dead-lettering outbound message {MessageId}.",
                envelope.ConversationId,
                args.Message.MessageId);

            await args.DeadLetterMessageAsync(
                args.Message,
                "MissingConversationContext",
                $"No Teams routing context found for conversationId '{envelope.ConversationId}'. The proxy may have restarted since this conversation began.",
                args.CancellationToken);
            return;
        }

        // Reconstruct a BotNexusOutboundMessage from the new envelope + stored Teams context.
        var outboundMessage = new BotNexusOutboundMessage
        {
            ServiceUrl = context.ServiceUrl,
            ConversationId = envelope.ConversationId,
            ReplyToActivityId = context.ActivityId,
            Text = envelope.Content,
            ChannelId = context.ChannelId,
            // On reply: bot identity becomes From, original Teams user becomes Recipient.
            From = context.Recipient,
            Recipient = context.From,
            Conversation = context.Conversation ?? new ConversationAccount { Id = envelope.ConversationId },
        };

        var validationError = outboundMessage.GetValidationError();
        if (validationError is not null)
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "InvalidPayload",
                validationError,
                args.CancellationToken);
            return;
        }

        if (ShouldSkipOutbound(outboundMessage))
        {
            _logger.LogWarning(
                "Completing outbound message {MessageId} without Connector send because serviceUrl host {ServiceUrlHost} is configured as inbound-only. ConversationId={ConversationId}",
                args.Message.MessageId,
                TryGetHost(outboundMessage.ServiceUrl),
                outboundMessage.ConversationId);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        await _botConnectorClient.SendAsync(outboundMessage, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private bool ShouldSkipOutbound(BotNexusOutboundMessage outboundMessage)
    {
        var serviceUrlHost = TryGetHost(outboundMessage.ServiceUrl);
        return !string.IsNullOrWhiteSpace(serviceUrlHost)
            && _options.SkipOutboundServiceUrlHosts.Any(host =>
                string.Equals(host, serviceUrlHost, StringComparison.OrdinalIgnoreCase)
                || serviceUrlHost.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetHost(string? serviceUrl)
    {
        return Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus processor error on {EntityPath} during {ErrorSource}.",
            args.EntityPath,
            args.ErrorSource);
        return Task.CompletedTask;
    }
}
