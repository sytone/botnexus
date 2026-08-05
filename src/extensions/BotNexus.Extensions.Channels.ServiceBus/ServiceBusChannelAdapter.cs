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
using BotNexus.Gateway.Channels.Startup;
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

    /// <summary>
    /// Prefix minted by <see cref="ConversationId.Create"/> for INTERNAL BotNexus conversation ids.
    /// Such an id identifies a conversation aggregate inside the gateway; it is never an address
    /// any external transport can deliver to (#2815).
    /// </summary>
    internal const string InternalConversationIdPrefix = "c_";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ServiceBusChannelAdapter> _logger;
    private readonly LateBoundChannelOptions<ServiceBusChannelOptions> _optionsHolder;

    // Read at point of use so a runtime config.json edit is reflected without a gateway restart (#2010).
    private ServiceBusChannelOptions _options => _optionsHolder.Current;

    // Optional factory injected at construction time; null → create real factory in OnStartAsync.
    private readonly IServiceBusAdapterClientFactory? _injectedFactory;

    private IServiceBusAdapterClientFactory? _activeFactory;
    private ServiceBusProcessor? _processor;

    // #2386: bounds the receive loop so a terminal fault parks the processor instead of
    // re-erroring every few seconds for hours.
    private readonly ChannelLoopCircuitBreaker _receiveBreaker = new("Azure Service Bus receive loop");

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

    // #2525 AC5: request keys whose work has already been dispatched successfully. A redelivery
    // after a lock-lost completion carries the same broker MessageId, and the turn it represents
    // has already run, so it must not run again. Entries are only added after a successful
    // dispatch - a handler that threw was abandoned and genuinely does need to be retried.
    private readonly ConcurrentDictionary<string, byte> _dispatchedMessages =
        new(StringComparer.Ordinal);

    // FIFO of dispatched keys, used to evict the oldest entries so a long-lived adapter cannot
    // grow _dispatchedMessages without bound.
    private readonly ConcurrentQueue<string> _dispatchedOrder = new();

    // Retains roughly an hour of redelivery history at realistic inbound rates, which comfortably
    // outlives any lock-renewal window, while bounding memory.
    private const int MaxDispatchedMessages = 10_000;

    // #2815: agent ids observed on inbound envelopes. An agent id is a legitimate SESSION-ROUTING
    // key for internal channels, but it is never an external Service Bus destination - the Teams
    // relay is fail-closed and dead-letters any envelope whose conversationId is an agent name.
    // Recording the ids we have actually seen inbound lets the outbound validity clause recognise
    // one without taking a dependency on the gateway's agent registry.
    private readonly ConcurrentDictionary<string, byte> _knownAgentIds =
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
            // #2525: the SDK default renewal window is five minutes, which is shorter than many
            // agent turns. When it lapses the completion call fails with a lock-lost error and
            // Service Bus redelivers work that already succeeded.
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(_options.MaxAutoLockRenewalMinutes),
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
        _knownAgentIds.Clear();
        _dispatchedMessages.Clear();
        _dispatchedOrder.Clear();

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
        var (correlationId, inheritedConversationId) = ResolveReplyContext(message.Metadata, pendingCtx);

        // #2529 (security, cross-conversation delivery): the destination conversation is
        // never guessed. Precedence is strict and fail-closed:
        //   1. The message's OWN ConversationId - the producing session knows its destination.
        //   2. A conversation inherited from an EXACT pending reply context (ChannelRequestId
        //      matched a registered inbound request). This is legitimate reply correlation.
        //   3. A FIFO-borrowed conversation that disagrees with the channel address means an
        //      unrelated in-flight inbound request. Adopting it would deliver this content into
        //      someone else's conversation, so FAIL LOUDLY instead of guessing.
        //   4. Otherwise the channel address is the unambiguous destination.
        //   5. #2815 VALIDITY: whatever survived the rules above must actually be an EXTERNAL
        //      destination. An agent id or an internal 'c_' conversation id is a routing key, not
        //      a wire address, and an envelope carrying one is guaranteed to dead-letter.
        var conversationId = ResolveOutboundConversationId(
            message,
            inheritedConversationId,
            pending.IsExactMatch,
            hasBorrowedContext: pendingCtx is not null && !pending.IsExactMatch);

        var envelope = new ServiceBusOutboundEnvelope
        {
            CorrelationId = correlationId,
            AgentId = GetMetadataString(message.Metadata, MetaAgentId),
            ConversationId = conversationId,
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
    /// <remarks>
    /// Service Bus streaming correlates by request identity, so a target without a
    /// <see cref="ChannelStreamTarget.ChannelRequestId"/> can never be delivered. The guard in
    /// <see cref="SendStreamEventAsync"/> is unchanged; this simply lets a fan-out caller learn
    /// the precondition instead of discovering it as an exception.
    /// </remarks>
    public bool CanSendStreamEvent(ChannelStreamTarget target)
        => !string.IsNullOrWhiteSpace(target.ChannelRequestId);

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

        // #2815: remember the agent id this transport actually talks to, so the outbound validity
        // clause can recognise it if a gateway producer later hands it back as a channel address.
        var inboundAgentId = envelope.AgentId ?? GetApplicationProperty(applicationProperties, "agentId");
        if (!string.IsNullOrWhiteSpace(inboundAgentId))
            _knownAgentIds.TryAdd(inboundAgentId, 0);

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

        // #2525 AC5: a turn that outlives the message lock still succeeds, but the completion then
        // fails with a lock-lost error and the broker redelivers the same MessageId. The work has
        // already been done, so dispatching again would perform it twice. Only a key derived from
        // the broker's own MessageId is trustworthy - a generated GUID differs on every delivery.
        var isBrokerKeyed = !string.IsNullOrEmpty(messageId) || !string.IsNullOrEmpty(envelope.MessageId);
        if (isBrokerKeyed && _dispatchedMessages.ContainsKey(requestKey))
        {
            _logger.LogInformation(
                "{DisplayName} skipping redelivered Service Bus message {MessageId}; it was already processed on an earlier delivery",
                DisplayName,
                requestKey);
            return;
        }

        await DispatchInboundAsync(inbound, cancellationToken);

        // Recorded only after the dispatch returns. A handler that threw propagates out of this
        // method, is abandoned by the caller, and must still be retried on redelivery.
        if (isBrokerKeyed)
            MarkDispatched(requestKey);
    }

    /// <summary>
    /// Records a request key as already processed for redelivery suppression (#2525), evicting the
    /// oldest keys once the retention bound is reached.
    /// </summary>
    private void MarkDispatched(string requestKey)
    {
        if (!_dispatchedMessages.TryAdd(requestKey, 0))
            return;

        _dispatchedOrder.Enqueue(requestKey);

        while (_dispatchedOrder.Count > MaxDispatchedMessages && _dispatchedOrder.TryDequeue(out var oldest))
            _dispatchedMessages.TryRemove(oldest, out _);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private Task OnProcessMessageAsync(ProcessMessageEventArgs args)
        => ProcessMessageCoreAsync(
            ct => HandleMessageBodyAsync(
                args.Message.Body.ToString(),
                args.Message.ApplicationProperties,
                args.Message.MessageId,
                ct),
            ct => args.CompleteMessageAsync(args.Message, ct),
            () => args.AbandonMessageAsync(args.Message),
            args.Message.MessageId,
            args.CancellationToken);

    /// <summary>
    /// Testable core of the processor callback (#2525). The handler runs first and the message is
    /// only acknowledged afterwards, so delivery stays at-least-once: a mid-turn crash retries
    /// rather than silently losing the request. The distinction this method adds is between a
    /// <em>handler</em> failure — where the work did not happen and abandoning is correct — and an
    /// <em>acknowledgement</em> failure that occurs after the work already succeeded.
    /// </summary>
    internal async Task<MessageProcessingOutcome> ProcessMessageCoreAsync(
        Func<CancellationToken, Task> handleAsync,
        Func<CancellationToken, Task> completeAsync,
        Func<Task> abandonAsync,
        string? messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            await handleAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is in progress — abandon so the message is retried on the next startup.
            await abandonAsync();
            return MessageProcessingOutcome.AbandonedForShutdown;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "{DisplayName} unhandled error processing Service Bus message {MessageId}; message will be abandoned",
                DisplayName,
                messageId);

            await abandonAsync();
            return MessageProcessingOutcome.AbandonedAfterHandlerFailure;
        }

        // From here on the work has already been performed. Anything that fails is an
        // acknowledgement problem, not a processing problem, and must not be logged as one.
        try
        {
            await completeAsync(cancellationToken);
            return MessageProcessingOutcome.Completed;
        }
        catch (ServiceBusException ex) when (
            ex.Reason == ServiceBusFailureReason.MessageLockLost ||
            ex.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            // The lock expired while the turn was running. Abandoning is not attempted: with an
            // invalid lock that call fails too, and the broker redelivers the message regardless.
            // The honest statement is that the work succeeded and may be redelivered.
            _logger.LogWarning(
                ex,
                "{DisplayName} processed Service Bus message {MessageId} successfully but the lock expired before it could be acknowledged; the broker may redeliver this message and the work may run again",
                DisplayName,
                messageId);

            return MessageProcessingOutcome.CompleteFailedLockLost;
        }
        catch (Exception ex)
        {
            // A non-lock acknowledgement failure (transient/network). The lock may still be held,
            // so abandon deliberately to release it promptly rather than waiting for expiry.
            _logger.LogWarning(
                ex,
                "{DisplayName} processed Service Bus message {MessageId} successfully but could not acknowledge it; the message will be abandoned and may be redelivered",
                DisplayName,
                messageId);

            await abandonAsync();
            return MessageProcessingOutcome.CompleteFailedAbandoned;
        }
    }

    private Task OnProcessErrorAsync(ProcessErrorEventArgs args)
        => HandleProcessorErrorAsync(args.Exception, args.ErrorSource.ToString(), args.EntityPath);

    /// <summary>
    /// Bounds the Service Bus receive loop (#2386). The Azure SDK re-invokes this callback for
    /// every failed receive attempt, so a fault it cannot recover from - a revoked AAD grant
    /// (AADSTS50173) being the observed case - previously produced an unbounded ERR stream at
    /// ~13/min for six hours while inbound messages were silently not being received.
    /// </summary>
    /// <remarks>
    /// Terminal failures stop the processor and emit exactly one actionable ERR line. Transient
    /// failures are left to the SDK's own retry, logged at warning with a bounded backoff hint so
    /// they no longer dominate the error log. Unclassifiable failures are treated as terminal.
    /// </remarks>
    internal async Task HandleProcessorErrorAsync(Exception exception, string errorSource, string? entityPath)
    {
        var response = _receiveBreaker.RecordFailure(exception);

        if (!response.ShouldStop)
        {
            _logger.LogWarning(
                exception,
                "{DisplayName} Service Bus processor transient error (source={ErrorSource}, entity={EntityPath}, failure {FailureCount}); next attempt bounded to {RetryDelaySeconds}s",
                DisplayName,
                errorSource,
                entityPath,
                _receiveBreaker.ConsecutiveTransientFailures,
                response.RetryDelay.TotalSeconds);
            return;
        }

        if (response.CircuitOpened)
        {
            _logger.LogError(
                exception,
                "{DisplayName} receive loop on queue '{QueueName}' is DEGRADED and has been stopped: a non-transient failure was detected and will not clear by retrying (source={ErrorSource}). Inbound messages are NOT being received. Resolve the underlying fault - for a revoked Azure credential run 'az login --scope https://servicebus.azure.net/.default' - then restart the channel.",
                DisplayName,
                _options.InboundQueueName,
                errorSource);
        }

        var processor = _processor;
        if (processor is not null && !processor.IsClosed)
        {
            try
            {
                await processor.StopProcessingAsync(CancellationToken.None);
            }
            catch (Exception stopEx)
            {
                _logger.LogWarning(stopEx, "{DisplayName} failed to stop the degraded Service Bus processor", DisplayName);
            }
        }
    }

    /// <summary>Exposed for tests: whether the receive-loop circuit breaker has tripped.</summary>
    internal bool ReceiveCircuitIsOpen => _receiveBreaker.IsOpen;

    /// <summary>Exposed for tests: clears the transient backoff after a successful receive.</summary>
    internal void RecordReceiveSuccess() => _receiveBreaker.RecordSuccess();

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

    private (string? RequestKey, PendingReplyContext? Context, bool IsExactMatch) ResolvePendingReplyContext(
        string? explicitRequestKey,
        ChannelAddress channelAddress)
    {
        if (!string.IsNullOrWhiteSpace(explicitRequestKey)
            && _pendingReplies.TryGetValue(explicitRequestKey, out var explicitContext))
        {
            return (explicitRequestKey, explicitContext, true);
        }

        if (_pendingQueue.TryGetValue(channelAddress.Value, out var queue))
        {
            while (queue.TryPeek(out var oldestKey))
            {
                if (_pendingReplies.TryGetValue(oldestKey, out var fallbackContext))
                    return (oldestKey, fallbackContext, false);

                // Successful sends remove the context first. Discard its stale FIFO key only
                // when a later lookup observes that removal, so a failed send remains retryable.
                queue.TryDequeue(out _);
            }
        }

        return (null, null, false);
    }

    /// <summary>
    /// Resolves the conversation an outbound envelope is addressed to, fail-closed (#2529, #2815).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The producing session's own <see cref="OutboundMessage.ConversationId"/> always wins.
    /// An inherited conversation is only trusted when it came from an exact
    /// <c>ChannelRequestId</c> match, which is genuine reply correlation. A conversation
    /// borrowed from the FIFO-by-address fallback belongs to an unrelated in-flight request
    /// and must never be adopted; an ambiguous destination throws rather than risking
    /// delivery of content into a third party's conversation.
    /// </para>
    /// <para>
    /// #2815 adds a VALIDITY clause on top of that precedence, and the two must be read together.
    /// #2529 answers "WHICH conversation?"; #2815 answers "is that value an EXTERNAL destination
    /// at all?". <c>ChannelAddress</c> is overloaded in this platform: for internal channels it is
    /// a session-routing key, and gateway producers legitimately build one from an agent id or an
    /// internal <c>c_</c> conversation id. On an external transport neither can ever be delivered -
    /// the Teams relay runs with agent-fallback routing disabled (deliberately, because inferring a
    /// destination from an agent name previously delivered replies into the WRONG chat) and
    /// dead-letters the envelope. Emitting a message that is certain to dead-letter is not a
    /// successful send, so this refuses loudly instead. Do not "fix" a dead-letter report by
    /// deleting this clause or by re-enabling relay-side inference - that reintroduces exactly the
    /// cross-conversation delivery defect #2529 exists to prevent.
    /// </para>
    /// </remarks>
    private string ResolveOutboundConversationId(
        OutboundMessage message,
        string? inheritedConversationId,
        bool isExactPendingMatch,
        bool hasBorrowedContext)
    {
        var resolved = ResolveOutboundConversationIdCore(
            message,
            inheritedConversationId,
            isExactPendingMatch,
            hasBorrowedContext);

        // 5. #2815 validity: the resolved value must be an external wire destination.
        if (TryDescribeNonExternalDestination(resolved) is { } reason)
        {
            _logger.LogError(
                "{DisplayName} refusing to emit an outbound envelope for session '{SessionId}': the only "
                + "available destination '{ConversationId}' is {Reason}, not an external conversation "
                + "address. Such an envelope is guaranteed to dead-letter at the relay. Set "
                + "OutboundMessage.ConversationId to the originating external address, or thread the "
                + "inbound ChannelRequestId through so the pending reply context supplies it (see #2815).",
                DisplayName,
                message.SessionId,
                resolved,
                reason);

            throw new InvalidOperationException(
                $"Refusing to send a Service Bus message addressed to '{resolved}': it is {reason}, "
                + "not an external conversation address, so the message would certainly dead-letter. "
                + "Set OutboundMessage.ConversationId or ChannelRequestId to identify the originating "
                + "external destination (see issue #2815).");
        }

        return resolved;
    }

    /// <summary>
    /// Classifies a candidate destination that is NOT an external address, returning a short
    /// human-readable reason, or <c>null</c> when the value is an acceptable wire destination.
    /// </summary>
    private string? TryDescribeNonExternalDestination(string candidate)
    {
        if (candidate.StartsWith(InternalConversationIdPrefix, StringComparison.Ordinal))
            return "an internal BotNexus conversation id";

        if (_knownAgentIds.ContainsKey(candidate))
            return "a known agent id";

        return null;
    }

    private static string ResolveOutboundConversationIdCore(
        OutboundMessage message,
        string? inheritedConversationId,
        bool isExactPendingMatch,
        bool hasBorrowedContext)
    {
        // 1. The producing session's own destination always wins.
        if (message.ConversationId is { Length: > 0 } ownConversationId
            && !string.IsNullOrWhiteSpace(ownConversationId))
        {
            return ownConversationId;
        }

        // 2. Genuine reply correlation: an exact ChannelRequestId match.
        if (isExactPendingMatch
            && inheritedConversationId is { Length: > 0 } exactConversationId
            && !string.IsNullOrWhiteSpace(exactConversationId))
        {
            return exactConversationId;
        }

        // 3. A conversation was borrowed from the FIFO-by-address fallback (an unrelated
        //    in-flight inbound request) and it DISAGREES with this message's channel address.
        //    That is the #2529 leak condition: adopting it would deliver the content into the
        //    other request's conversation. Fail closed rather than guess.
        if (hasBorrowedContext
            && !string.IsNullOrWhiteSpace(inheritedConversationId)
            && !string.Equals(inheritedConversationId, message.ChannelAddress.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to send a Service Bus message with an ambiguous destination conversation. "
                + "The message carries no ConversationId and no matching ChannelRequestId, while an unrelated "
                + $"inbound request for conversation '{inheritedConversationId}' is pending on channel address "
                + $"'{message.ChannelAddress.Value}'. Set OutboundMessage.ConversationId or ChannelRequestId "
                + "to identify the intended destination (see issue #2529).");
        }

        // 4. The channel address is the unambiguous destination. Note this is NOT the old
        //    silent default: a borrowed conversation can no longer override it, and any
        //    disagreement has already thrown above.
        return message.ChannelAddress.Value;
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
