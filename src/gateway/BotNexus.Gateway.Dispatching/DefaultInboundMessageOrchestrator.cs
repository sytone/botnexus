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
    /// Default upper bound on how long <see cref="AcceptAsync"/> will wait for a queued message to
    /// reach the <em>front</em> of its per-isolation-unit queue before returning
    /// <see cref="InboundDispatchStatus.Stalled"/> and logging a warning (#3600).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This bounds the <b>pre-processing</b> wait only. Once the worker has handed a message to
    /// <see cref="IInboundMessageProcessor.ProcessAsync"/> the await becomes unbounded again, so an
    /// ordinary long agent turn - minutes of tool calls - is never truncated. The clock applies
    /// solely to messages sitting <em>behind</em> a head that is not moving, which is exactly the
    /// #3600 failure: a wedged head stranded every successor forever, with no exception, no status
    /// and no log line.
    /// </para>
    /// <para>
    /// The value is deliberately short. A successor that has not even started after this long is
    /// information the caller needs - "your message is queued behind something that is not
    /// progressing" - and reporting it is strictly better than the pre-#3600 behaviour of reporting
    /// nothing at all. <see cref="InboundDispatchStatus.Stalled"/> is not a drop: the message stays
    /// on the channel and is still processed when the head clears.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultQueueWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Message text returned to the originating channel when its per-session
    /// queue is full and a new inbound message has been dropped. Preserves the
    /// legacy <c>GatewayHost</c> behaviour so end users see a clear retry hint.
    /// </summary>
    public const string BusyMessage = "Session is busy processing messages. Please retry shortly.";

    /// <summary>
    /// Message text returned to the originating channel when a queued message has not started
    /// processing within the bounded window (#3600). Distinct from <see cref="BusyMessage"/> because
    /// the situations differ: busy means the message was refused, stalled means it was kept.
    /// </summary>
    public const string StalledMessage =
        "Your message is queued behind a turn that has not finished. It has not been lost and will " +
        "be processed when that turn completes.";

    private readonly IInboundMessageProcessor _processor;
    private readonly ILogger<DefaultInboundMessageOrchestrator> _logger;
    private readonly IChannelManager? _channelManager;
    private readonly IInboundDeliveryResolver? _deliveryResolver;
    private readonly IInboundSteerDeliverer? _steerDeliverer;
    private readonly int _queueCapacity;
    private readonly TimeSpan _queueWaitTimeout;
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
        IInboundSteerDeliverer? steerDeliverer = null,
        TimeSpan? queueWaitTimeout = null)
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
        if (queueWaitTimeout is { } supplied && supplied <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(queueWaitTimeout), supplied,
                "Queue wait timeout must be positive.");
        }
        _queueWaitTimeout = queueWaitTimeout ?? DefaultQueueWaitTimeout;
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
        var queueItem = new QueuedInboundMessage(message);
        return TryWriteToLiveQueue(queueKey, queueItem);
    }

    /// <summary>
    /// Writes an item onto the isolation unit's queue, replacing the queue first if the one in the
    /// dictionary is no longer being read (#3600).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queue worker removes its own dictionary entry when the session seals and again in the
    /// <c>finally</c> of its read loop. A caller that resolved the entry just before either removal,
    /// or a <c>GetOrAdd</c> that re-added a state whose writer had already been completed, would then
    /// write into a channel that nobody reads. <c>TryWrite</c> succeeds, the caller awaits a
    /// completion that can never be set, and the message is lost with no exception and no log - the
    /// orphaned-queue half of #3600.
    /// </para>
    /// <para>
    /// Guarding on the worker task closes it: a completed worker means the state is dead, so it is
    /// evicted and rebuilt rather than written to. The rebuild is logged at Warning because a healthy
    /// gateway should hit it rarely, and a rising rate is itself the signal that something upstream
    /// is tearing queues down unexpectedly.
    /// </para>
    /// </remarks>
    private bool TryWriteToLiveQueue(string queueKey, QueuedInboundMessage queueItem)
    {
        // Two attempts: the first may lose the race against a worker that is exiting right now, the
        // second is written to a state this call created or observed as live.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = _sessionQueues.GetOrAdd(queueKey, CreateSessionQueueState);
            if (!state.WorkerTask.IsCompleted && state.Queue.Writer.TryWrite(queueItem))
            {
                return true;
            }

            if (!state.WorkerTask.IsCompleted)
            {
                // A live worker that refused the write means the bounded queue is genuinely full.
                return false;
            }

            _logger.LogWarning(
                "Inbound queue for isolation unit '{QueueKey}' was resolved but its worker had already " +
                "completed; recreating the queue rather than writing into a channel nobody reads " +
                "(#3600). attempt={Attempt}",
                queueKey, attempt + 1);

            // Evict only the exact dead instance, so a queue another thread has already rebuilt is
            // never torn down underneath it.
            _ = ((ICollection<KeyValuePair<string, SessionQueueState>>)_sessionQueues)
                .Remove(new KeyValuePair<string, SessionQueueState>(queueKey, state));
            state.Queue.Writer.TryComplete();
        }

        return false;
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
        var queueItem = new QueuedInboundMessage(message);

        if (!TryWriteToLiveQueue(queueKey, queueItem))
        {
            await SendBusyFeedbackAsync(message, cancellationToken);
            return InboundDispatchResult.Busy();
        }

        // #3600: bound the wait for the message to reach the FRONT of the queue, and ONLY that wait.
        // Pre-fix, AcceptAsync awaited Completion with no upper bound at all, so a head-of-queue turn
        // that never returned stranded every later message on the same isolation unit: TryWrite kept
        // succeeding (capacity 64), nothing threw, nothing was logged, and the caller's await simply
        // never resolved. The message was unobservable between accept and processing.
        //
        // Once the worker has picked this item up (Started) the await deliberately becomes unbounded
        // again, so a legitimately long agent turn is never truncated. The clock therefore measures
        // exactly one thing: "is the queue ahead of me moving?".
        if (!await WaitForProcessingStartAsync(queueItem, queueKey, message, cancellationToken))
        {
            // Not a drop. The item is still on the channel and will be processed when the head
            // clears; we are only releasing the caller's await with an observable outcome.
            ObserveAbandonedCompletion(queueItem);
            await SendStallFeedbackAsync(message, cancellationToken);
            return InboundDispatchResult.Stalled();
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
    /// Waits, up to the configured bound, for the queue worker to actually pick this item up.
    /// Returns <see langword="true"/> when processing started (or already finished), and
    /// <see langword="false"/> when the bound elapsed first — in which case a warning naming the
    /// isolation key and the routing identifiers has been logged (#3600).
    /// </summary>
    /// <remarks>
    /// The <see cref="QueuedInboundMessage.Completion"/> task is included in the race because a very
    /// fast worker can complete an item before the caller ever observes <c>Started</c>; treating that
    /// as "not started" would report a stall for a message that was in fact fully processed.
    /// </remarks>
    private async Task<bool> WaitForProcessingStartAsync(
        QueuedInboundMessage queueItem,
        string queueKey,
        InboundMessage message,
        CancellationToken cancellationToken)
    {
        var started = queueItem.Started.Task;
        var completed = queueItem.Completion.Task;
        if (started.IsCompleted || completed.IsCompleted)
        {
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(_queueWaitTimeout, timeoutCts.Token);
        var winner = await Task.WhenAny(started, completed, delay);
        if (winner != delay)
        {
            // Cancel the timer so a long-running turn does not hold a pending delay for its duration.
            timeoutCts.Cancel();
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hints = InboundMessageRoutingHints.FromMessage(message);
        _logger.LogWarning(
            "Inbound message for isolation unit '{QueueKey}' has not started processing after " +
            "{TimeoutSeconds}s and is queued behind a turn that is not progressing; returning " +
            "{Status} (#3600). channel='{ChannelType}' address='{ChannelAddress}' " +
            "conversation='{ConversationId}' session='{SessionId}' agent='{AgentId}'. " +
            "The message remains queued and will be processed when the head of the queue clears.",
            queueKey,
            _queueWaitTimeout.TotalSeconds,
            InboundDispatchStatus.Stalled,
            message.ChannelType,
            message.ChannelAddress,
            hints.RequestedConversationId?.Value ?? "(none)",
            hints.RequestedSessionId?.Value ?? "(none)",
            hints.RequestedAgentId?.Value ?? "(none)");

        return false;
    }

    /// <summary>
    /// Attaches a no-op observer to a completion the caller has stopped awaiting, so a later
    /// <c>TrySetException</c> on it cannot surface as an <c>UnobservedTaskException</c> and crash an
    /// unrelated part of the process. Abandoning the await is the point of the #3600 bound; leaking
    /// an unobserved fault while doing it would trade one silent failure for another.
    /// </summary>
    private static void ObserveAbandonedCompletion(QueuedInboundMessage queueItem)
        => _ = queueItem.Completion.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Best-effort user-visible feedback for a stalled queue (#3600), reusing the busy-feedback seam.
    /// The production defect was not only that the message hung — it was that the user got no signal
    /// whatsoever and re-sent three times into a conversation that was already dead.
    /// </summary>
    private async Task SendStallFeedbackAsync(InboundMessage message, CancellationToken cancellationToken)
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
                Content = StalledMessage,
                SessionId = hints.RequestedSessionId?.Value
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to send stall-feedback for channel '{ChannelType}'", message.ChannelType);
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
                    // #3600: signal that this item has left the queue and is now in the processor's
                    // hands. AcceptAsync bounds only the wait for THIS signal; everything after it is
                    // a real turn and is awaited without a time limit.
                    item.Started.TrySetResult(true);

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

        /// <summary>
        /// Signalled by the queue worker at the instant this item is handed to the processor (#3600).
        /// It is the boundary between "waiting behind someone else" (bounded, and reported as
        /// <see cref="InboundDispatchStatus.Stalled"/> if it takes too long) and "my own turn is
        /// running" (unbounded, because agent turns legitimately take minutes).
        /// </summary>
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<InboundDispatchResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
