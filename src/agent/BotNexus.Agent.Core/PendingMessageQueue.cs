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
    public void Enqueue(AgentMessage message)
    {
        lock (_lock)
        {
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
