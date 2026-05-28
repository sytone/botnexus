using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
/// The adapter maintains an in-memory pending-reply index keyed by a per-dispatch request key
/// (the inbound Service Bus message ID, or a generated GUID when absent). This prevents reply
/// context from being overwritten when <see cref="ServiceBusChannelOptions.MaxConcurrentCalls"/>
/// is greater than one and two inbound messages for the same conversation are in-flight
/// simultaneously.
/// </para>
/// <para>
/// A secondary per-conversation FIFO queue maps each <see cref="ChannelAddress"/> to its
/// pending request keys. When the gateway does not propagate <c>InboundMessage.Metadata</c>
/// to <c>OutboundMessage.Metadata</c>, <see cref="SendAsync"/> dequeues the oldest context
/// for that conversation address. When the outbound message does carry
/// <see cref="MetaRequestKey"/>, the lookup is exact and order-independent.
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

    /// <summary>
    /// Per-dispatch unique key threaded through <c>InboundMessage.Metadata</c> so that
    /// <see cref="SendAsync"/> can retrieve the exact <see cref="PendingReplyContext"/> for
    /// this request when the outbound message carries it. Preferred over the FIFO fallback
    /// when two in-flight requests share the same conversation address.
    /// </summary>
    internal const string MetaRequestKey = "servicebus.requestKey";

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

    // Pending-reply contexts keyed by per-dispatch request key (SB messageId or generated GUID).
    // Using a unique key per dispatch prevents a second in-flight message for the same
    // conversation from overwriting the first entry when MaxConcurrentCalls > 1.
    private readonly ConcurrentDictionary<string, PendingReplyContext> _pendingReplies =
        new(StringComparer.Ordinal);

    // Secondary index: conversation address → FIFO queue of request keys.
    // Used by SendAsync as a fallback when OutboundMessage.Metadata does not carry
    // MetaRequestKey (the live gateway path does not propagate InboundMessage.Metadata).
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingQueue =
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
        _pendingQueue.Clear();

        if (_activeFactory is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        _activeFactory = null;

        _logger.LogInformation("{DisplayName} adapter stopped", DisplayName);
    }

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var pendingCtx = DequeuePendingReplyContext(message);
        var replyQueue = ResolveReplyQueue(message, pendingCtx);
        var (correlationId, conversationId) = ResolveReplyContext(message, pendingCtx);

        var envelope = new ServiceBusOutboundEnvelope
        {
            // MessageId is initialised to Guid.NewGuid() by the property initializer.
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
    /// <param name="body">Raw JSON message body.</param>
    /// <param name="applicationProperties">Optional Service Bus application properties used as
    /// fallbacks when envelope fields are absent.</param>
    /// <param name="messageId">The Service Bus message identifier, used as the per-dispatch
    /// request key. When <c>null</c>, the envelope's own <c>messageId</c> field is tried first,
    /// then a GUID is generated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task HandleMessageBodyAsync(
        string body,
        IReadOnlyDictionary<string, object>? applicationProperties,
        string? messageId,
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

        // Generate a per-dispatch request key.  Using the SB messageId (or envelope messageId)
        // means two concurrent inbound messages for the same conversation get distinct keys,
        // so the second arrival cannot overwrite the first entry in _pendingReplies.
        var requestKey = messageId ?? envelope.MessageId ?? Guid.NewGuid().ToString();

        // Store routing context keyed by request key.
        // TryAdd returns false when this requestKey is already present, which happens when
        // Service Bus redelivers an abandoned message with the same MessageId. On retry we
        // update the context (replyTo/correlationId may have changed) but must NOT add a
        // second entry to _pendingQueue — the original entry is already there. A duplicate
        // would leave a stale key in _pendingQueue after the first successful reply removes
        // the context, which would cause the next FIFO-fallback lookup to pop the stale key,
        // fail TryRemove, and misroute that reply to the default queue.
        var pendingContext = new PendingReplyContext(replyTo, correlationId, conversationId);
        if (_pendingReplies.TryAdd(requestKey, pendingContext))
        {
            // First arrival: register in the per-conversation FIFO queue for SendAsync fallback.
            _pendingQueue
                .GetOrAdd(channelAddress.Value, _ => new ConcurrentQueue<string>())
                .Enqueue(requestKey);
        }
        else
        {
            // Retry/redelivery: overwrite context only, do not add a duplicate FIFO entry.
            _pendingReplies[requestKey] = pendingContext;
        }

        var metadata = new Dictionary<string, object?>
        {
            [MetaRequestKey] = requestKey,
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
            Sender = CitizenId.Of(UserId.From(senderId)),
            ChannelAddress = channelAddress,
            Content = envelope.Content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                targetAgentId: envelope.AgentId ?? GetApplicationProperty(applicationProperties, "agentId"),
                sessionId: envelope.SessionId ?? GetApplicationProperty(applicationProperties, "sessionId"),
                conversationId: conversationId),
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
                args.Message.MessageId,
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

    /// <summary>
    /// Resolves and atomically removes the pending reply context for the given outbound message.
    /// Prefers an explicit <see cref="MetaRequestKey"/> in <paramref name="message"/> metadata;
    /// falls back to dequeuing the oldest context for the conversation address.
    /// </summary>
    private PendingReplyContext? DequeuePendingReplyContext(OutboundMessage message)
    {
        // Explicit key: present when the outbound message carries MetaRequestKey (e.g., tests,
        // or a future gateway path that propagates inbound metadata).
        if (GetMetadataString(message.Metadata, MetaRequestKey) is { Length: > 0 } requestKey
            && _pendingReplies.TryRemove(requestKey, out var explicit_ctx))
        {
            return explicit_ctx;
        }

        // Fallback: dequeue the oldest request key for this conversation.
        // Relies on the gateway serialising per-session so FIFO order matches reply order.
        if (_pendingQueue.TryGetValue(message.ChannelAddress.Value, out var queue)
            && queue.TryDequeue(out var oldestKey)
            && _pendingReplies.TryRemove(oldestKey, out var fallback_ctx))
        {
            return fallback_ctx;
        }

        return null;
    }

    private string ResolveReplyQueue(OutboundMessage message, PendingReplyContext? pendingCtx)
    {
        // 1. Metadata key (populated when the caller manually constructs OutboundMessage in tests)
        if (GetMetadataString(message.Metadata, MetaReplyTo) is { Length: > 0 } metaQueue)
            return metaQueue;

        // 2. Pending-reply context (populated by HandleMessageBodyAsync in the live path)
        if (pendingCtx?.ReplyTo is { Length: > 0 } pendingQueue)
            return pendingQueue;

        return _options.DefaultReplyQueueName;
    }

    private (string? CorrelationId, string? ConversationId) ResolveReplyContext(OutboundMessage message, PendingReplyContext? pendingCtx)
    {
        // Prefer values from pending-reply context (live gateway path), fall back to metadata.
        if (pendingCtx is not null)
            return (pendingCtx.CorrelationId, pendingCtx.ConversationId);

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
