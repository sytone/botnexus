using System.Collections.Concurrent;
using System.Threading.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Default <see cref="IInboundMessageOrchestrator"/> that owns the per-isolation-unit
/// FIFO queue, applies bounded backpressure, and delegates per-message processing
/// to an <see cref="IInboundMessageProcessor"/>. Migrated from the queue plumbing
/// formerly embedded in <c>GatewayHost</c> so every transport — channel adapters,
/// SignalR hubs, REST controllers — can share the same serialisation guarantee.
/// </summary>
/// <remarks>
/// The unit of isolation is defined by <see cref="InboundIsolationKey"/>: the canonical
/// conversation when the delivery names one, otherwise the session, otherwise the
/// channel composite (#2123). Everything mapping to one key runs strictly FIFO; distinct
/// keys run in parallel.
/// </para>
/// <para>
/// <b>Queue capacity, and its relationship to the agent's steering queue (#3028 AC7).</b> Two bounded
/// queues sit on an inbound message's path and they bound different things:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="DefaultQueueCapacity"/> (64) bounds the <em>gateway</em> per-isolation-unit FIFO of
/// messages waiting for their own turn. Overflow is reported as
/// <see cref="InboundDispatchStatus.Busy"/> with user-visible <see cref="BusyMessage"/> feedback.
/// It is configurable per host via the constructor's <c>queueCapacity</c> parameter.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>PendingMessageQueue.Capacity</c> in <c>BotNexus.Agent.Core</c> bounds the <em>agent's</em>
/// steering/follow-up queue: messages injected into a turn ALREADY running, drained at the loop's
/// steering drain points according to <c>QueueMode</c>. Zero (the default) means unbounded; overflow
/// throws <c>PendingMessageQueueFullException</c> so the accepting boundary can report the refusal.
/// </description>
/// </item>
/// </list>
/// <para>
/// They are independent by design and neither is a fallback for the other: a message counts against
/// exactly one of them, decided by <see cref="IInboundDeliveryResolver"/>. Queue-bound messages never
/// consume steering capacity, and steered messages never consume gateway queue capacity — so a busy
/// gateway queue cannot block a steer, and a saturated steering queue cannot block normal traffic.
/// </para>
/// </remarks>
public sealed class DefaultInboundMessageOrchestrator : IInboundMessageOrchestrator, IChannelDispatcher, IAsyncDisposable
{
    /// <summary>Default bounded-channel capacity for per-session queues.</summary>
    public const int DefaultQueueCapacity = 64;

    /// <summary>
    /// Message text returned to the originating channel when its per-session
    /// queue is full and a new inbound message has been dropped. Preserves the
    /// legacy <c>GatewayHost</c> behaviour so end users see a clear retry hint.
    /// </summary>
    public const string BusyMessage = "Session is busy processing messages. Please retry shortly.";

    private readonly IInboundMessageProcessor _processor;
    private readonly ILogger<DefaultInboundMessageOrchestrator> _logger;
    private readonly IChannelManager? _channelManager;
    private readonly IInboundDeliveryResolver? _deliveryResolver;
    private readonly IInboundSteerDeliverer? _steerDeliverer;
    private readonly int _queueCapacity;
    private readonly ConcurrentDictionary<string, SessionQueueState> _sessionQueues =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates an orchestrator that uses the supplied processor to handle each
    /// dequeued message. When <paramref name="channelManager"/> is supplied the
    /// orchestrator also sends a busy-feedback <see cref="OutboundMessage"/> to
    /// the originating channel on queue-full. Queue capacity defaults to
    /// <see cref="DefaultQueueCapacity"/>; pass a smaller value in tests to
    /// assert backpressure behaviour.
    /// </summary>
    /// <remarks>
    /// <paramref name="deliveryResolver"/> and <paramref name="steerDeliverer"/> are the #3028 seam:
    /// when BOTH are supplied the orchestrator can route a message to a running turn instead of the
    /// FIFO queue. When either is absent the orchestrator queues unconditionally, which is exactly
    /// its pre-#3028 behaviour — so the many direct-construction call sites in tests and hosts keep
    /// working unchanged, and the steering path is opt-in at composition time rather than implicit.
    /// </remarks>
    public DefaultInboundMessageOrchestrator(
        IInboundMessageProcessor processor,
        ILogger<DefaultInboundMessageOrchestrator> logger,
        IChannelManager? channelManager = null,
        int queueCapacity = DefaultQueueCapacity,
        IInboundDeliveryResolver? deliveryResolver = null,
        IInboundSteerDeliverer? steerDeliverer = null)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(logger);
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity,
                "Queue capacity must be positive.");
        }
        _processor = processor;
        _logger = logger;
        _channelManager = channelManager;
        _queueCapacity = queueCapacity;
        _deliveryResolver = deliveryResolver;
        _steerDeliverer = steerDeliverer;
    }

    /// <summary>
    /// Adapter implementation of the legacy <see cref="IChannelDispatcher"/>
    /// contract. Channel adapters call this; behaviour is identical to
    /// <see cref="AcceptAsync"/> but the aggregate result is discarded — the
    /// legacy contract returns <see cref="Task"/>.
    /// </summary>
    public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        => AcceptAsync(message, cancellationToken);

    /// <inheritdoc />
    public bool Post(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Sender.IsValid)
        {
            throw new ArgumentException(
                $"InboundMessage.Sender must be a valid CitizenId; got default(CitizenId). " +
                $"Channel '{message.ChannelType}' producer must populate it (see #526).",
                nameof(message));
        }

        var queueKey = GetQueueKey(message);
        var queueState = _sessionQueues.GetOrAdd(queueKey, CreateSessionQueueState);
        var queueItem = new QueuedInboundMessage(message);
        return queueState.Queue.Writer.TryWrite(queueItem);
    }

    /// <summary>
    /// Consults the #3028 delivery seam and, when it resolves to a live-turn mechanism, injects the
    /// message into the running turn instead of queueing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <see langword="false"/> — meaning "carry on and queue" — in every case the steer did
    /// not happen: no seam wired, intent was <c>Auto</c>/<c>Queue</c>, no turn running, or the
    /// deliverer reported it could not inject. That last case matters: the turn can end between the
    /// resolver's check and the injection, and a message must never be dropped because it lost that
    /// race. Falling through to the queue is the safe direction to be wrong in.
    /// </para>
    /// <para>
    /// A deliverer that THROWS is also treated as a non-delivery rather than propagated. Steering is
    /// an optimisation over queueing; a broken steer path must degrade to the historical behaviour,
    /// not fail an inbound message that would otherwise have been delivered fine.
    /// </para>
    /// </remarks>
    private async Task<bool> TrySteerAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (_deliveryResolver is null || _steerDeliverer is null)
        {
            return false;
        }

        InboundDeliveryDecision decision;
        try
        {
            decision = await _deliveryResolver.ResolveAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Inbound delivery resolution failed for channel '{ChannelType}'; falling back to queue.",
                message.ChannelType);
            return false;
        }

        if (decision.Resolved is not (InboundDeliveryMode.Steer or InboundDeliveryMode.Interrupt))
        {
            if (decision.FellBackToQueue)
            {
                _logger.LogInformation(
                    "Inbound message requested {Requested} but no turn was running; queued instead.",
                    decision.Requested);
            }
            return false;
        }

        try
        {
            var delivered = await _steerDeliverer.TryDeliverAsync(message, decision, cancellationToken);
            if (!delivered)
            {
                _logger.LogInformation(
                    "Inbound {Requested} could not be injected into a running turn; queued instead.",
                    decision.Requested);
            }
            return delivered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Inbound {Requested} injection failed for channel '{ChannelType}'; falling back to queue.",
                decision.Requested, message.ChannelType);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<InboundDispatchResult> AcceptAsync(
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        // CitizenId is a struct, so `required` can't catch `default`. Every channel
        // producer must populate Sender with a valid typed citizen (#526). Migrated
        // verbatim from GatewayHost.DispatchAsync — same contract.
        if (!message.Sender.IsValid)
        {
            throw new ArgumentException(
                $"InboundMessage.Sender must be a valid CitizenId; got default(CitizenId). " +
                $"Channel '{message.ChannelType}' producer must populate it (see #526).",
                nameof(message));
        }

        // #3028: the steer/queue decision is made HERE, server-side, before anything touches the
        // FIFO queue. The resolver reads the caller's stated intent and the server-owned evidence
        // (is a turn actually running?) and collapses them to one mechanism. Absent the seam this
        // is a no-op and the message queues exactly as it always did.
        var steered = await TrySteerAsync(message, cancellationToken);
        if (steered)
        {
            return InboundDispatchResult.Steered();
        }

        var queueKey = GetQueueKey(message);
        var queueState = _sessionQueues.GetOrAdd(queueKey, CreateSessionQueueState);
        var queueItem = new QueuedInboundMessage(message);

        if (!queueState.Queue.Writer.TryWrite(queueItem))
        {
            await SendBusyFeedbackAsync(message, cancellationToken);
            return InboundDispatchResult.Busy();
        }
        try
        {
            return await queueItem.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's token was cancelled. Per the legacy GatewayHost behaviour
            // we let the processor finish in the background on a detached token — do
            // not surface the inner exception to a now-disconnected caller.
            throw;
        }
    }

    /// <summary>
    /// Derives the queue key by delegating to <see cref="InboundIsolationKey"/>, the
    /// single explicit definition of the inbound unit of isolation.
    /// </summary>
    /// <remarks>
    /// This method deliberately holds no policy of its own. Before #2123 the rule was
    /// inlined here as <c>RequestedSessionId ?? channelType:channelAddress</c>, which
    /// left the isolation unit implicit and, for webhooks, wrong: it keyed on the
    /// registration id, so two registrations pinned to one conversation ran on separate
    /// queues and raced that conversation's <c>active_session_id</c>. The rule and its
    /// rationale now live in one documented, directly-tested place.
    /// </remarks>
    private static string GetQueueKey(InboundMessage message)
        => InboundIsolationKey.ForMessage(message).Value;

    /// <summary>
    /// Sends a busy-feedback message back through the originating channel
    /// when the per-session queue refused a new message. Best-effort: if no
    /// channel manager is wired or the adapter cannot be resolved we simply
    /// return Busy without any user-visible feedback.
    /// </summary>
    private async Task SendBusyFeedbackAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (_channelManager is null)
        {
            return;
        }

        var channel = _channelManager.Get(message.ChannelType);
        if (channel is null)
        {
            return;
        }

        var hints = InboundMessageRoutingHints.FromMessage(message);
        try
        {
            await channel.SendAsync(new OutboundMessage
            {
                ChannelType = message.ChannelType,
                ChannelAddress = message.ChannelAddress,
                Content = BusyMessage,
                SessionId = hints.RequestedSessionId?.Value
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to send busy-feedback for channel '{ChannelType}'", message.ChannelType);
        }
    }

    private SessionQueueState CreateSessionQueueState(string queueKey)
    {
        var queue = Channel.CreateBounded<QueuedInboundMessage>(new BoundedChannelOptions(_queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var workerTask = ProcessSessionQueueAsync(queueKey, queue.Reader);
        return new SessionQueueState(queue, workerTask);
    }

    private async Task ProcessSessionQueueAsync(
        string queueKey,
        ChannelReader<QueuedInboundMessage> queueReader)
    {
        try
        {
            await foreach (var item in queueReader.ReadAllAsync())
            {
                bool shouldCloseQueue = false;
                try
                {
                    // Use a detached token for processor work so client disconnect
                    // doesn't kill in-progress agent execution. The processor itself
                    // owns whether to honour its own cooperative-cancellation hooks.
                    var outcome = await _processor.ProcessAsync(item.Message, CancellationToken.None);
                    shouldCloseQueue = outcome.ShouldClosePerSessionQueue;

                    var status = outcome.Dispatches.Count == 0
                        ? InboundDispatchStatus.NoRoute
                        : InboundDispatchStatus.Accepted;
                    item.Completion.TrySetResult(new InboundDispatchResult(status, outcome.Dispatches));
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing queued inbound message for queue '{QueueKey}'",
                        queueKey);
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    if (shouldCloseQueue && _sessionQueues.TryRemove(queueKey, out var state))
                    {
                        state.Queue.Writer.TryComplete();
                    }
                }
            }
        }
        finally
        {
            _sessionQueues.TryRemove(queueKey, out _);
        }
    }

    /// <summary>
    /// Drains all per-session queue workers — completes their writers and awaits
    /// in-flight processing to finish. Hosts call this on shutdown so background
    /// work is not abandoned mid-message.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var state in _sessionQueues.Values)
        {
            state.Queue.Writer.TryComplete();
        }

        var workers = _sessionQueues.Values.Select(state => state.WorkerTask).ToArray();
        if (workers.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "One or more inbound orchestrator workers completed with errors during shutdown.");
        }
    }

    private sealed class SessionQueueState(Channel<QueuedInboundMessage> queue, Task workerTask)
    {
        public Channel<QueuedInboundMessage> Queue { get; } = queue;

        public Task WorkerTask { get; } = workerTask;
    }

    private sealed class QueuedInboundMessage(InboundMessage message)
    {
        public InboundMessage Message { get; } = message;

        public TaskCompletionSource<InboundDispatchResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
