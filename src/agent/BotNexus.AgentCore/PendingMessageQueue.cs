using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;

namespace BotNexus.AgentCore;

/// <summary>
/// Thread-safe queue for steering and follow-up messages.
/// Supports two drain modes: All (drain everything) or OneAtATime (drain one per call).
/// </summary>
internal sealed class PendingMessageQueue
{
    private readonly object _lock = new();
    private readonly List<AgentMessage> _messages = [];

    public PendingMessageQueue(QueueMode mode) => Mode = mode;

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

    public void Enqueue(AgentMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
    }

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

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }
}
