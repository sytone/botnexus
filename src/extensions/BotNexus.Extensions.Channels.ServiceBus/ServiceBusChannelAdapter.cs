using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.ServiceBus;

/// <summary>
/// Azure Service Bus channel adapter.
/// Receives JSON-enveloped messages from an inbound queue, routes them through the gateway,
/// and sends agent replies to a reply queue (either the per-message <c>replyTo</c> queue
/// or the configured <see cref="ServiceBusChannelOptions.DefaultReplyQueueName"/>).
/// </summary>
/// <remarks>
/// <para>
/// The adapter maintains an in-memory pending-reply index keyed by <see cref="ChannelAddress"/>
/// (the conversation key). This allows the outbound path to recover the original
/// <c>replyTo</c> and <c>correlationId</c> values even when the gateway does not propagate
/// inbound metadata to <see cref="OutboundMessage.Metadata"/>.
/// </para>
/// <para>
/// For managed-identity or custom credential scenarios, register your own
/// <see cref="IServiceBusAdapterClientFactory"/> in DI before calling
/// <see cref="ServiceBusServiceCollectionExtensions.AddBotNexusServiceBusChannel"/>.
/// </para>
/// </remarks>
public sealed class ServiceBusChannelAdapter : ChannelAdapterBase
{
    // Metadata keys stored in InboundMessage.Metadata for use by the outbound path.
    internal const string MetaReplyTo = "servicebus.replyTo";
    internal const string MetaCorrelationId = "servicebus.correlationId";
    internal const string MetaConversationId = "servicebus.conversationId";
    internal const string MetaAgentId = "servicebus.agentId";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ServiceBusChannelAdapter> _logger;
    private readonly ServiceBusChannelOptions _options;

    // Optional factory injected at construction time; null → create real factory in OnStartAsync.
    private readonly IServiceBusAdapterClientFactory? _injectedFactory;

    private IServiceBusAdapterClientFactory? _activeFactory;
    private ServiceBusProcessor? _processor;

    // Senders are cached per queue name so we don't create a new sender on every reply.
    private readonly ConcurrentDictionary<string, IServiceBusSenderWrapper> _senders =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-conversation state stored when an inbound message arrives, consumed by SendAsync.
    // Keyed by ChannelAddress.Value (conversationId or senderId).
    private readonly ConcurrentDictionary<string, PendingReplyContext> _pendingReplies =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises the adapter. Pass a <paramref name="clientFactory"/> in tests to avoid
    /// real Azure connections; leave it <c>null</c> in production (a factory is created from
    /// <see cref="ServiceBusChannelOptions.ConnectionString"/> on first start).
    /// </summary>
    public ServiceBusChannelAdapter(
        ILogger<ServiceBusChannelAdapter> logger,
        IOptions<ServiceBusChannelOptions> optionsAccessor,
        IServiceBusAdapterClientFactory? clientFactory = null)
        : base(logger)
    {
        _logger = logger;
        _options = optionsAccessor.Value;
        _injectedFactory = clientFactory;
        AllowList = [.. _options.AllowedSenderIds];
    }

    /// <inheritdoc/>
    public override ChannelKey ChannelType => ChannelKey.From("servicebus");

    /// <inheritdoc/>
    public override string DisplayName => "Azure Service Bus";

    /// <inheritdoc/>
    public override bool SupportsStreaming => false;

    /// <inheritdoc/>
    public override bool SupportsSteering => false;

    /// <inheritdoc/>
    public override bool SupportsFollowUp => false;

    /// <inheritdoc/>
    public override bool SupportsThinkingDisplay => false;

    /// <inheritdoc/>
    public override bool SupportsToolDisplay => false;

    /// <inheritdoc/>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        _activeFactory = _injectedFactory ?? CreateDefaultFactory();

        var processorOptions = new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = _options.MaxConcurrentCalls,
            // Manual completion — we complete after successful dispatch, abandon on error.
            AutoCompleteMessages = false,
        };

        _processor = _activeFactory.CreateProcessor(_options.InboundQueueName, processorOptions);
        _processor.ProcessMessageAsync += OnProcessMessageAsync;
        _processor.ProcessErrorAsync += OnProcessErrorAsync;

        await _processor.StartProcessingAsync(cancellationToken);

        _logger.LogInformation(
            "{DisplayName} adapter started; listening on queue '{QueueName}'",
            DisplayName,
            _options.InboundQueueName);
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            _processor.ProcessMessageAsync -= OnProcessMessageAsync;
            _processor.ProcessErrorAsync -= OnProcessErrorAsync;

            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
            _processor = null;
        }

        foreach (var sender in _senders.Values)
            await sender.DisposeAsync();

        _senders.Clear();
        _pendingReplies.Clear();

        if (_activeFactory is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        _activeFactory = null;

        _logger.LogInformation("{DisplayName} adapter stopped", DisplayName);
    }

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var replyQueue = ResolveReplyQueue(message);
        var (correlationId, conversationId) = ResolveReplyContext(message);

        var envelope = new ServiceBusOutboundEnvelope
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            AgentId = GetMetadataString(message.Metadata, MetaAgentId),
            ConversationId = conversationId ?? message.ChannelAddress.Value,
            SessionId = message.SessionId,
            Content = message.Content,
            Timestamp = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = envelope.MessageId,
        };

        if (envelope.CorrelationId is not null)
            sbMessage.CorrelationId = envelope.CorrelationId;

        // Mirror key fields as application properties for server-side filtering.
        if (envelope.AgentId is not null)
            sbMessage.ApplicationProperties["agentId"] = envelope.AgentId;
        if (envelope.ConversationId is not null)
            sbMessage.ApplicationProperties["conversationId"] = envelope.ConversationId;
        if (envelope.SessionId is not null)
            sbMessage.ApplicationProperties["sessionId"] = envelope.SessionId;

        var sender = GetOrCreateSender(replyQueue);
        await sender.SendMessageAsync(sbMessage, cancellationToken);

        // Release the pending-reply context once the reply has been sent.
        _pendingReplies.TryRemove(message.ChannelAddress.Value, out _);

        _logger.LogDebug(
            "{DisplayName} reply sent to queue '{ReplyQueue}' (correlationId={CorrelationId})",
            DisplayName,
            replyQueue,
            envelope.CorrelationId);
    }

    /// <summary>
    /// Deserialises a raw Service Bus message body and dispatches it to the gateway pipeline.
    /// Exposed as <c>internal</c> so unit tests can invoke the inbound path directly using
    /// <see cref="Azure.Messaging.ServiceBus.ServiceBusModelFactory"/> messages, without
    /// needing a live processor or real Azure connection.
    /// </summary>
    internal async Task HandleMessageBodyAsync(
        string body,
        IReadOnlyDictionary<string, object>? applicationProperties,
        CancellationToken cancellationToken)
    {
        ServiceBusInboundEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<ServiceBusInboundEnvelope>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{DisplayName} failed to deserialise inbound message; message will be abandoned", DisplayName);
            return;
        }

        if (envelope is null)
        {
            _logger.LogWarning("{DisplayName} received null envelope after deserialisation; message will be abandoned", DisplayName);
            return;
        }

        var senderId = envelope.SenderId
            ?? GetApplicationProperty(applicationProperties, "senderId")
            ?? "unknown";

        var conversationId = envelope.ConversationId
            ?? GetApplicationProperty(applicationProperties, "conversationId");

        // Use conversationId as the channel address when available; fall back to senderId.
        // ChannelAddress is the session-routing key and must be stable across a conversation.
        var channelAddress = ChannelAddress.From(conversationId ?? senderId);

        var replyTo = envelope.ReplyTo
            ?? GetApplicationProperty(applicationProperties, "replyTo");

        var correlationId = envelope.CorrelationId
            ?? GetApplicationProperty(applicationProperties, "correlationId");

        // Store routing context so SendAsync can recover replyTo/correlationId even when
        // the gateway does not propagate InboundMessage.Metadata to OutboundMessage.Metadata.
        _pendingReplies[channelAddress.Value] = new PendingReplyContext(replyTo, correlationId, conversationId);

        var metadata = new Dictionary<string, object?>
        {
            [MetaReplyTo] = replyTo,
            [MetaCorrelationId] = correlationId,
            [MetaConversationId] = conversationId,
            [MetaAgentId] = envelope.AgentId ?? GetApplicationProperty(applicationProperties, "agentId"),
        };

        // Merge caller-supplied metadata, without overwriting the keys set above.
        if (envelope.Metadata is not null)
        {
            foreach (var kvp in envelope.Metadata)
                metadata.TryAdd(kvp.Key, kvp.Value);
        }

        var inbound = new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            ChannelAddress = channelAddress,
            Content = envelope.Content,
            TargetAgentId = envelope.AgentId ?? GetApplicationProperty(applicationProperties, "agentId"),
            SessionId = envelope.SessionId ?? GetApplicationProperty(applicationProperties, "sessionId"),
            ConversationId = conversationId,
            Timestamp = envelope.Timestamp ?? DateTimeOffset.UtcNow,
            Metadata = metadata,
        };

        await DispatchInboundAsync(inbound, cancellationToken);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task OnProcessMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            await HandleMessageBodyAsync(
                args.Message.Body.ToString(),
                args.Message.ApplicationProperties,
                args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is in progress — abandon so the message is retried on the next startup.
            await args.AbandonMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "{DisplayName} unhandled error processing Service Bus message; message will be abandoned",
                DisplayName);

            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task OnProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "{DisplayName} Service Bus processor error (source={ErrorSource}, entity={EntityPath})",
            DisplayName,
            args.ErrorSource,
            args.EntityPath);

        return Task.CompletedTask;
    }

    private IServiceBusAdapterClientFactory CreateDefaultFactory()
    {
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
            return new DefaultServiceBusAdapterClientFactory(new ServiceBusClient(_options.ConnectionString));

        throw new InvalidOperationException(
            $"{nameof(ServiceBusChannelOptions.ConnectionString)} must be set in '{nameof(ServiceBusChannelOptions)}'. " +
            $"For managed-identity authentication, inject a custom {nameof(IServiceBusAdapterClientFactory)} via DI.");
    }

    private string ResolveReplyQueue(OutboundMessage message)
    {
        // 1. Metadata key (populated when the caller manually constructs OutboundMessage in tests)
        if (GetMetadataString(message.Metadata, MetaReplyTo) is { Length: > 0 } metaQueue)
            return metaQueue;

        // 2. Pending-reply context (populated by HandleMessageBodyAsync in the live path)
        if (_pendingReplies.TryGetValue(message.ChannelAddress.Value, out var ctx)
            && ctx.ReplyTo is { Length: > 0 } pendingQueue)
            return pendingQueue;

        return _options.DefaultReplyQueueName;
    }

    private (string? CorrelationId, string? ConversationId) ResolveReplyContext(OutboundMessage message)
    {
        // Prefer values from pending-reply context (live gateway path), fall back to metadata.
        if (_pendingReplies.TryGetValue(message.ChannelAddress.Value, out var ctx))
            return (ctx.CorrelationId, ctx.ConversationId);

        return (
            GetMetadataString(message.Metadata, MetaCorrelationId),
            GetMetadataString(message.Metadata, MetaConversationId));
    }

    private IServiceBusSenderWrapper GetOrCreateSender(string queueName)
    {
        if (_activeFactory is null)
            throw new InvalidOperationException("Channel adapter has not been started. Call StartAsync before SendAsync.");

        return _senders.GetOrAdd(queueName, q => _activeFactory.CreateSender(q));
    }

    private static string? GetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
        => metadata.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static string? GetApplicationProperty(IReadOnlyDictionary<string, object>? props, string key)
        => props is not null && props.TryGetValue(key, out var val) ? val?.ToString() : null;

    /// <summary>Routing context preserved between inbound receipt and outbound reply.</summary>
    private sealed record PendingReplyContext(string? ReplyTo, string? CorrelationId, string? ConversationId);
}
