using System.Collections.Concurrent;
using System.Threading.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Default <see cref="IInboundMessageOrchestrator"/> that owns the per-session
/// FIFO queue, applies bounded backpressure, and delegates per-message processing
/// to an <see cref="IInboundMessageProcessor"/>. Migrated from the queue plumbing
/// formerly embedded in <c>GatewayHost</c> so every transport — channel adapters,
/// SignalR hubs, REST controllers — can share the same serialisation guarantee.
/// </summary>
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
    public DefaultInboundMessageOrchestrator(
        IInboundMessageProcessor processor,
        ILogger<DefaultInboundMessageOrchestrator> logger,
        IChannelManager? channelManager = null,
        int queueCapacity = DefaultQueueCapacity)
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
    /// Derives the per-session queue key. When the inbound message carries a
    /// <see cref="InboundMessageRoutingHints.RequestedSessionId"/> we use that
    /// directly so the same logical session always lands on the same queue
    /// regardless of channel address. Otherwise we fall back to channel-type
    /// + channel-address — matching the legacy <c>GatewayHost.GetQueueKey</c>.
    /// </summary>
    private static string GetQueueKey(InboundMessage message)
    {
        var hints = InboundMessageRoutingHints.FromMessage(message);
        return hints.RequestedSessionId is { } sid
            ? sid.Value
            : $"{message.ChannelType}:{message.ChannelAddress}";
    }

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
