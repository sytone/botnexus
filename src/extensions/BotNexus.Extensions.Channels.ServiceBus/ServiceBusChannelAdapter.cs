using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Configuration;
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
public sealed class ServiceBusChannelAdapter : ChannelAdapterBase, IStreamEventChannelAdapter
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
    private readonly LateBoundChannelOptions<ServiceBusChannelOptions> _optionsHolder;

    // Read at point of use so a runtime config.json edit is reflected without a gateway restart (#2010).
    private ServiceBusChannelOptions _options => _optionsHolder.Current;

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

    // Accumulators are keyed by the channel-native request identity, never conversation address,
    // so two concurrent streams in one conversation cannot share text or sequence numbers.
    private readonly ConcurrentDictionary<string, PendingStreamState> _pendingStreams =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Configuration section this adapter binds its options from when it is loaded as a
    /// dynamic extension after the initial DI options pass. Follows the
    /// <c>channels:&lt;channelType&gt;</c> convention shared by the Telegram and Agent 365
    /// channel extensions.
    /// </summary>
    internal const string ConfigSection = "channels:servicebus";

    /// <summary>
    /// Initialises the adapter. Pass a <paramref name="clientFactory"/> in tests to avoid
    /// real Azure connections; leave it <c>null</c> in production (a factory is created from
    /// <see cref="ServiceBusChannelOptions.ConnectionString"/> on first start).
    /// </summary>
    public ServiceBusChannelAdapter(
        ILogger<ServiceBusChannelAdapter> logger,
        IOptions<ServiceBusChannelOptions> optionsAccessor,
        IServiceBusAdapterClientFactory? clientFactory = null,
        IConfiguration? configuration = null)
        : base(logger)
    {
        _logger = logger;
        _optionsHolder = new LateBoundChannelOptions<ServiceBusChannelOptions>(
            () => ResolveOptions(optionsAccessor, configuration),
            configuration);
        _injectedFactory = clientFactory;
        AllowList = [.. _options.AllowedSenderIds];
    }

    /// <summary>
    /// Resolves the effective options. This channel extension is loaded dynamically, after the
    /// host has already run its <see cref="IOptions{T}"/> binding pass, so
    /// <paramref name="optionsAccessor"/> comes back empty in the live gateway. When that
    /// happens we bind directly from <see cref="IConfiguration"/> under <see cref="ConfigSection"/>,
    /// mirroring the Telegram and Agent 365 adapters. Tests that inject options via DI keep
    /// working because the bound value is only used when no auth material is present.
    /// </summary>
    internal static ServiceBusChannelOptions ResolveOptions(
        IOptions<ServiceBusChannelOptions> optionsAccessor,
        IConfiguration? configuration)
    {
        var opts = optionsAccessor.Value;
        var hasAuth =
            !string.IsNullOrWhiteSpace(opts.ConnectionString) ||
            !string.IsNullOrWhiteSpace(opts.FullyQualifiedNamespace);
        if (!hasAuth && configuration is not null)
        {
            var bound = new ServiceBusChannelOptions();
            configuration.GetSection(ConfigSection).Bind(bound);
            return bound;
        }
        return opts;
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
        _pendingStreams.Clear();

        if (_activeFactory is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        _activeFactory = null;

        _logger.LogInformation("{DisplayName} adapter stopped", DisplayName);
    }

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var pending = ResolvePendingReplyContext(message.ChannelRequestId, message.ChannelAddress);
        var pendingCtx = pending.Context;
        var replyQueue = ResolveReplyQueue(message.Metadata, pendingCtx);
        var (correlationId, conversationId) = ResolveReplyContext(message.Metadata, pendingCtx);

        var envelope = new ServiceBusOutboundEnvelope
        {
            CorrelationId = correlationId,
            AgentId = GetMetadataString(message.Metadata, MetaAgentId),
            ConversationId = conversationId ?? message.ConversationId ?? message.ChannelAddress.Value,
            SessionId = message.SessionId,
            Content = message.Content,
            Type = "done",
            Sequence = 0,
            IsFinal = true,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await SendEnvelopeAsync(replyQueue, envelope, pendingCtx, cancellationToken);
        CommitPendingReply(pending.RequestKey);
    }

    /// <inheritdoc/>
    public override Task SendStreamDeltaAsync(
        ChannelStreamTarget target,
        string delta,
        CancellationToken cancellationToken = default)
        => SendStreamEventAsync(
            target,
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = delta },
            cancellationToken);

    /// <inheritdoc/>
    public async Task SendStreamEventAsync(
        ChannelStreamTarget target,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        if (streamEvent.Type is not (AgentStreamEventType.ContentDelta or AgentStreamEventType.RunEnded))
            return;

        if (string.IsNullOrWhiteSpace(target.ChannelRequestId))
            throw new InvalidOperationException("A Service Bus stream requires a channel request identity.");

        var requestKey = target.ChannelRequestId;
        if (!_pendingReplies.TryGetValue(requestKey, out var pendingCtx))
        {
            // A repeated terminal event after successful cleanup is harmless. Any other event
            // without context is unsafe because it could be routed to another request's queue.
            if (streamEvent.Type == AgentStreamEventType.RunEnded)
                return;
            throw new InvalidOperationException($"No pending Service Bus reply context exists for request '{requestKey}'.");
        }

        var state = _pendingStreams.GetOrAdd(requestKey, _ => new PendingStreamState());
        await state.SendGate.WaitAsync(cancellationToken);
        try
        {
            if (state.Completed)
                return;

            var replyQueue = ResolveReplyQueue(
                new Dictionary<string, object?>(),
                pendingCtx);
            if (streamEvent.Type == AgentStreamEventType.ContentDelta)
            {
                if (streamEvent.ContentDelta is null)
                    return;

                var envelope = CreateStreamEnvelope(
                    target,
                    streamEvent,
                    pendingCtx,
                    "delta",
                    state.NextSequence,
                    streamEvent.ContentDelta,
                    isFinal: false);
                await SendEnvelopeAsync(replyQueue, envelope, pendingCtx, cancellationToken);
                state.Content.Append(streamEvent.ContentDelta);
                state.NextSequence++;
                return;
            }

            var finalEnvelope = CreateStreamEnvelope(
                target,
                streamEvent,
                pendingCtx,
                "done",
                state.NextSequence,
                state.Content.ToString(),
                isFinal: true);
            await SendEnvelopeAsync(replyQueue, finalEnvelope, pendingCtx, cancellationToken);
            state.Completed = true;
            CommitPendingReply(requestKey);
            _pendingStreams.TryRemove(requestKey, out _);
        }
        finally
        {
            state.SendGate.Release();
        }
    }

    private static ServiceBusOutboundEnvelope CreateStreamEnvelope(
        ChannelStreamTarget target,
        AgentStreamEvent streamEvent,
        PendingReplyContext pendingCtx,
        string type,
        long sequence,
        string content,
        bool isFinal)
        => new()
        {
            CorrelationId = pendingCtx.CorrelationId,
            AgentId = streamEvent.AgentId?.Value,
            ConversationId = pendingCtx.ConversationId ?? target.ConversationId.Value,
            SessionId = streamEvent.SessionId?.Value ?? target.SessionId.Value,
            Content = content,
            Type = type,
            Sequence = sequence,
            IsFinal = isFinal,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private async Task SendEnvelopeAsync(
        string replyQueue,
        ServiceBusOutboundEnvelope envelope,
        PendingReplyContext? pendingCtx,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = envelope.MessageId,
        };

        if (envelope.CorrelationId is not null)
            sbMessage.CorrelationId = envelope.CorrelationId;

        if (pendingCtx is not null)
        {
            foreach (var property in pendingCtx.ApplicationProperties)
                sbMessage.ApplicationProperties[property.Key] = property.Value;
        }

        if (envelope.AgentId is not null)
            sbMessage.ApplicationProperties["agentId"] = envelope.AgentId;
        if (envelope.ConversationId is not null)
            sbMessage.ApplicationProperties["conversationId"] = envelope.ConversationId;
        if (envelope.SessionId is not null)
            sbMessage.ApplicationProperties["sessionId"] = envelope.SessionId;
        sbMessage.ApplicationProperties["type"] = envelope.Type;
        sbMessage.ApplicationProperties["sequence"] = envelope.Sequence;
        sbMessage.ApplicationProperties["isFinal"] = envelope.IsFinal;

        var sender = GetOrCreateSender(replyQueue);
        await sender.SendMessageAsync(sbMessage, cancellationToken);

        _logger.LogDebug(
            "{DisplayName} reply sent to queue '{ReplyQueue}' (correlationId={CorrelationId}, type={Type}, sequence={Sequence})",
            DisplayName,
            replyQueue,
            envelope.CorrelationId,
            envelope.Type,
            envelope.Sequence);
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
        var preservedApplicationProperties = applicationProperties is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : applicationProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        var pendingContext = new PendingReplyContext(
            replyTo,
            correlationId,
            conversationId,
            preservedApplicationProperties);
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
            StreamResponse = envelope.StreamResponse == true,
            ChannelRequestId = requestKey,
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
        switch (ResolveAuthMode(_options))
        {
            case ServiceBusAuthMode.ConnectionString:
                // Connection string takes precedence when present (simple / local-auth deployments).
                return new DefaultServiceBusAdapterClientFactory(new ServiceBusClient(_options.ConnectionString));

            case ServiceBusAuthMode.ManagedIdentity:
                // Managed-identity auth against the fully-qualified namespace.
                // This is the keyless path required by namespaces with disableLocalAuth = true.
                return new DefaultServiceBusAdapterClientFactory(
                    new ServiceBusClient(_options.FullyQualifiedNamespace, new DefaultAzureCredential()));

            default:
                throw new InvalidOperationException(
                    $"Either '{nameof(ServiceBusChannelOptions.ConnectionString)}' or " +
                    $"'{nameof(ServiceBusChannelOptions.FullyQualifiedNamespace)}' must be set in " +
                    $"'{nameof(ServiceBusChannelOptions)}'. Set a connection string for local-auth, or a " +
                    $"fully-qualified namespace for managed-identity authentication.");
        }
    }

    /// <summary>
    /// Selects the authentication mode from the options. Connection string wins when present;
    /// otherwise a fully-qualified namespace enables managed identity. Exposed as <c>internal</c>
    /// so the selection logic can be unit-tested without constructing a real Azure client.
    /// </summary>
    internal static ServiceBusAuthMode ResolveAuthMode(ServiceBusChannelOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            return ServiceBusAuthMode.ConnectionString;

        if (!string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
            return ServiceBusAuthMode.ManagedIdentity;

        return ServiceBusAuthMode.None;
    }

    private (string? RequestKey, PendingReplyContext? Context) ResolvePendingReplyContext(
        string? explicitRequestKey,
        ChannelAddress channelAddress)
    {
        if (!string.IsNullOrWhiteSpace(explicitRequestKey)
            && _pendingReplies.TryGetValue(explicitRequestKey, out var explicitContext))
        {
            return (explicitRequestKey, explicitContext);
        }

        if (_pendingQueue.TryGetValue(channelAddress.Value, out var queue))
        {
            while (queue.TryPeek(out var oldestKey))
            {
                if (_pendingReplies.TryGetValue(oldestKey, out var fallbackContext))
                    return (oldestKey, fallbackContext);

                // Successful sends remove the context first. Discard its stale FIFO key only
                // when a later lookup observes that removal, so a failed send remains retryable.
                queue.TryDequeue(out _);
            }
        }

        return (null, null);
    }

    private void CommitPendingReply(string? requestKey)
    {
        if (!string.IsNullOrWhiteSpace(requestKey))
            _pendingReplies.TryRemove(requestKey, out _);
    }

    private string ResolveReplyQueue(
        IReadOnlyDictionary<string, object?> metadata,
        PendingReplyContext? pendingCtx)
    {
        if (GetMetadataString(metadata, MetaReplyTo) is { Length: > 0 } metaQueue)
            return metaQueue;

        if (pendingCtx?.ReplyTo is { Length: > 0 } pendingQueue)
            return pendingQueue;

        return _options.DefaultReplyQueueName;
    }

    private static (string? CorrelationId, string? ConversationId) ResolveReplyContext(
        IReadOnlyDictionary<string, object?> metadata,
        PendingReplyContext? pendingCtx)
    {
        if (pendingCtx is not null)
            return (pendingCtx.CorrelationId, pendingCtx.ConversationId);

        return (
            GetMetadataString(metadata, MetaCorrelationId),
            GetMetadataString(metadata, MetaConversationId));
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

    /// <summary>Routing context preserved between inbound receipt and successful terminal delivery.</summary>
    private sealed record PendingReplyContext(
        string? ReplyTo,
        string? CorrelationId,
        string? ConversationId,
        IReadOnlyDictionary<string, object> ApplicationProperties);

    private sealed class PendingStreamState
    {
        public StringBuilder Content { get; } = new();
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public long NextSequence { get; set; }
        public bool Completed { get; set; }
    }
}
