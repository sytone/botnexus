using System.Collections.Concurrent;
using System.Threading.Channels;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// The single generic fan-out point for channel-neutral conversation events (issue #2085).
/// <para>
/// It knows only that some set of <see cref="IConversationEventSink"/> instances is registered.
/// It contains no channel name, no adapter type, and no routing policy - which extension cares
/// about which conversation is the extension's decision, made from the event's binding snapshot.
/// </para>
/// <para>
/// Semantics it guarantees:
/// <list type="bullet">
/// <item><description><b>Ordering.</b> Events for one conversation are offered to sinks in publication
/// order, because each conversation gets its own single-consumer queue and pump. Different
/// conversations proceed independently and are deliberately unordered relative to each other.</description></item>
/// <item><description><b>Failure isolation.</b> A sink that throws is logged and skipped; the remaining
/// sinks for that same event still receive it, and the conversation's pump continues.</description></item>
/// <item><description><b>Backpressure.</b> Each conversation queue is bounded. When full, publication is
/// refused rather than blocking the caller or evicting history, so the agent token callback is
/// never gated on extension throughput.</description></item>
/// <item><description><b>Cancellation.</b> Each sink invocation gets a token bounded by the configured
/// per-sink timeout and by publisher shutdown; a hung extension is abandoned, not awaited.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ConversationEventPublisher : IConversationEventPublisher, IAsyncDisposable
{
    private readonly IReadOnlyList<IConversationEventSink> _sinks;
    private readonly ConversationEventPublisherOptions _options;
    private readonly ILogger<ConversationEventPublisher> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    // One ordered pump per conversation: this is what makes per-conversation ordering a
    // structural property rather than a timing accident. Keyed by ConversationId so
    // unrelated conversations never queue behind each other.
    private readonly ConcurrentDictionary<ConversationId, ConversationPump> _pumps = new();

    private int _disposed;

    /// <summary>
    /// Creates a publisher over the registered sinks.
    /// </summary>
    /// <param name="sinks">
    /// Every channel extension that opted into conversation events. An empty set is valid and
    /// makes publication a no-op, which is the expected state before migration slices land.
    /// </param>
    /// <param name="options">Backpressure and timeout policy; defaults are used when null.</param>
    /// <param name="logger">Diagnostics sink for isolated sink faults and shed events.</param>
    public ConversationEventPublisher(
        IEnumerable<IConversationEventSink> sinks,
        ConversationEventPublisherOptions? options = null,
        ILogger<ConversationEventPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks.ToArray();
        _options = options ?? new ConversationEventPublisherOptions();
        _logger = logger ?? NullLogger<ConversationEventPublisher>.Instance;
    }

    /// <inheritdoc />
    public ValueTask<bool> PublishAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationEvent);

        if (Volatile.Read(ref _disposed) != 0 || _shutdown.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        // No sinks means nothing to order and nothing to buffer - skip pump creation entirely
        // so the pre-migration steady state costs nothing on the hot path.
        if (_sinks.Count == 0)
        {
            return ValueTask.FromResult(true);
        }

        var pump = _pumps.GetOrAdd(conversationEvent.ConversationId, _ => new ConversationPump(this));
        return ValueTask.FromResult(pump.TryEnqueue(conversationEvent));
    }

    /// <inheritdoc />
    public async Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot then await: pumps added after this point belong to a later publication and
        // are outside the "everything accepted before this call" contract.
        foreach (var pump in _pumps.Values.ToArray())
        {
            await pump.WaitForDrainAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops accepting events and lets in-flight pumps finish, so shutdown does not tear a
    /// conversation's ordered sequence in half.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var pump in _pumps.Values.ToArray())
        {
            pump.Complete();
        }

        foreach (var pump in _pumps.Values.ToArray())
        {
            await pump.WaitForCompletionAsync().ConfigureAwait(false);
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    /// <summary>
    /// Offers one event to every sink in registration order, isolating failures. Runs on the
    /// conversation's pump, so sinks never see two events for a conversation concurrently.
    /// </summary>
    private async Task DispatchAsync(ConversationEvent conversationEvent)
    {
        foreach (var sink in _sinks)
        {
            using var perSink = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            perSink.CancelAfter(_options.SinkTimeout);

            try
            {
                await sink.OnConversationEventAsync(conversationEvent, perSink.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Deliberately swallowed: one broken extension must not deny every other
                // extension the fact, nor kill the conversation's pump.
                _logger.LogWarning(
                    ex,
                    "Conversation event sink {SinkType} failed for conversation {ConversationId}; continuing with remaining sinks.",
                    sink.GetType().FullName,
                    conversationEvent.ConversationId.Value);
            }
        }
    }

    /// <summary>
    /// A single conversation's bounded queue plus its serial consumer loop. Separated out so
    /// ordering and backpressure are per-conversation rather than global.
    /// </summary>
    private sealed class ConversationPump
    {
        private readonly Channel<ConversationEvent> _queue;
        private readonly Task _consumer;
        private readonly ConversationEventPublisher _owner;

        // Counts events accepted but not yet fully dispatched. Drain waiting keys off this
        // rather than off channel emptiness, because an event dequeued but still in flight
        // through the sinks has not yet been observed.
        private int _pending;

        public ConversationPump(ConversationEventPublisher owner)
        {
            _owner = owner;
            _queue = Channel.CreateBounded<ConversationEvent>(new BoundedChannelOptions(owner._options.PerConversationCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // Wait-mode makes TryWrite REFUSE (return false) when the buffer is full rather
                // than silently accepting and discarding. The publisher never blocks on the writer,
                // so the wait half is unreachable: the observable behaviour is shed-the-newest,
                // which keeps a contiguous prefix and stays diagnosable.
                FullMode = BoundedChannelFullMode.Wait,
            });

            _consumer = Task.Run(ConsumeAsync);
        }

        public bool TryEnqueue(ConversationEvent conversationEvent)
        {
            Interlocked.Increment(ref _pending);

            if (_queue.Writer.TryWrite(conversationEvent))
            {
                return true;
            }

            ReleasePending();
            _owner._logger.LogWarning(
                "Conversation event shed for {ConversationId}: per-conversation buffer of {Capacity} is full.",
                conversationEvent.ConversationId.Value,
                _owner._options.PerConversationCapacity);
            return false;
        }

        public async Task WaitForDrainAsync(CancellationToken cancellationToken)
        {
            // Poll the outstanding count rather than gate on a completion source: the count is
            // the only thing that is simultaneously correct for "queued", "dequeued but still
            // inside a sink", and "a fresh burst arrived while we were waiting".
            while (Volatile.Read(ref _pending) > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        public void Complete() => _queue.Writer.TryComplete();

        public Task WaitForCompletionAsync() => _consumer;

        private async Task ConsumeAsync()
        {
            await foreach (var conversationEvent in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _owner.DispatchAsync(conversationEvent).ConfigureAwait(false);
                }
                finally
                {
                    ReleasePending();
                }
            }
        }

        private void ReleasePending() => Interlocked.Decrement(ref _pending);
    }
}
