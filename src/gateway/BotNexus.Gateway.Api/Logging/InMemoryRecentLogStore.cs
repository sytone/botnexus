namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Represents in memory recent log store.
/// </summary>
public sealed class InMemoryRecentLogStore(int capacity = 1000) : IRecentLogStore
{
    private readonly Queue<RecentLogEntry> _entries = new();
    private readonly Lock _sync = new();
    private readonly int _capacity = Math.Max(capacity, 100);

    /// <summary>
    /// Executes add.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Add(RecentLogEntry entry)
    {
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
                _entries.Dequeue();
        }
    }

    /// <summary>
    /// Executes get recent.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <returns>The get recent result.</returns>
    public IReadOnlyList<RecentLogEntry> GetRecent(int limit)
    {
        var count = Math.Clamp(limit, 1, 500);
        lock (_sync)
        {
            return _entries
                .Reverse()
                .Take(count)
                .ToArray();
        }
    }
}
