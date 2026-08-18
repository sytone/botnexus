using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core;

/// <summary>
/// Thread-safe queue for steering and follow-up messages.
/// Supports two drain modes: All (drain everything) or OneAtATime (drain one per call).
/// </summary>
/// <remarks>
/// Used internally by Agent to manage steering and follow-up message injection.
/// Thread-safe for concurrent Enqueue/Drain/Clear operations.
/// </remarks>
internal sealed class PendingMessageQueue
{
    private readonly object _lock = new();
    private readonly List<AgentMessage> _messages = [];

    public PendingMessageQueue(QueueMode mode) => Mode = mode;

    /// <summary>
    /// Gets or sets the mode.
    /// </summary>
    public QueueMode Mode { get; set; }

    /// <summary>
    /// Maximum number of undrained messages this queue will hold. Zero (the default)
    /// means unbounded, preserving the historical behaviour for the steering queue.
    /// </summary>
    /// <remarks>
    /// A bounded queue exists so a runaway producer cannot grow the pending set without
    /// limit while a single long-running turn is in flight (#2438). Overflow is an
    /// explicit, observable rejection - <see cref="PendingMessageQueueFullException"/> -
    /// never a silent drop, so the caller can tell the sender their message was refused.
    ///
    /// <para>NOT THE SAME KNOB as the gateway's inbound queue capacity (#3028). This bounds messages
    /// injected into a turn ALREADY RUNNING; <c>DefaultInboundMessageOrchestrator.DefaultQueueCapacity</c>
    /// (64) bounds messages waiting for a turn of their own. A message counts against exactly one of
    /// them, chosen server-side by <c>IInboundDeliveryResolver</c>, so neither can block the other.
    /// The relationship is documented in one place: <c>docs/development/inbound-delivery-modes.md</c>.</para>
    /// </remarks>
    public int Capacity { get; set; }

    public bool HasItems
    {
        get
        {
            lock (_lock)
            {
                return _messages.Count > 0;
            }
        }
    }

    /// <summary>
    /// Executes enqueue.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <exception cref="PendingMessageQueueFullException">
    /// The queue is bounded (<see cref="Capacity"/> greater than zero) and already full.
    /// The message is NOT enqueued; rejecting loudly is deliberate so the boundary that
    /// accepted it can report the refusal rather than discard the message silently.
    /// </exception>
    public void Enqueue(AgentMessage message)
    {
        lock (_lock)
        {
            if (Capacity > 0 && _messages.Count >= Capacity)
            {
                throw new PendingMessageQueueFullException(Capacity);
            }

            _messages.Add(message);
        }
    }

    /// <summary>
    /// Executes drain.
    /// </summary>
    /// <returns>The drain result.</returns>
    public IReadOnlyList<AgentMessage> Drain()
    {
        lock (_lock)
        {
            if (_messages.Count == 0)
            {
                return [];
            }

            if (Mode == QueueMode.All)
            {
                var drained = _messages.ToList();
                _messages.Clear();
                return drained;
            }

            var first = _messages[0];
            _messages.RemoveAt(0);
            return [first];
        }
    }

    /// <summary>
    /// Atomically removes a specific pending message by reference identity.
    /// </summary>
    /// <param name="message">The exact instance previously passed to <see cref="Enqueue"/>.</param>
    /// <returns><c>true</c> when the instance was still pending and has been removed.</returns>
    /// <remarks>
    /// Used to take back a follow-up that was enqueued against a run which settled before the
    /// loop drained it (#2438). Reference identity is deliberate: a blanket drain would steal
    /// messages enqueued by other producers, so only the caller's own instance is reclaimed.
    /// </remarks>
    public bool TryRemove(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                if (ReferenceEquals(_messages[i], message))
                {
                    _messages.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Executes clear.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }
}
