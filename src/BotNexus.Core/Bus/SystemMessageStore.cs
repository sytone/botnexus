using System.Collections.Concurrent;
using BotNexus.Core.Models;

namespace BotNexus.Core.Bus;

/// <summary>
/// In-memory store for recent system messages.
/// Maintains a circular buffer of the last N system messages for retrieval via API.
/// </summary>
public sealed class SystemMessageStore
{
    private const int MaxMessages = 100;
    private readonly ConcurrentQueue<SystemMessage> _messages = new();
    private readonly object _lock = new();

    /// <summary>Adds a system message to the store.</summary>
    public void Add(SystemMessage message)
    {
        lock (_lock)
        {
            _messages.Enqueue(message);
            
            // Trim to max size
            while (_messages.Count > MaxMessages)
                _messages.TryDequeue(out _);
        }
    }

    /// <summary>Gets all recent system messages (most recent first).</summary>
    public IReadOnlyList<SystemMessage> GetRecent(int count = 50)
    {
        lock (_lock)
        {
            return _messages
                .OrderByDescending(m => m.CreatedAt)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>Gets system messages since a specific timestamp.</summary>
    public IReadOnlyList<SystemMessage> GetSince(DateTimeOffset since)
    {
        lock (_lock)
        {
            return _messages
                .Where(m => m.CreatedAt >= since)
                .OrderByDescending(m => m.CreatedAt)
                .ToList();
        }
    }
}
