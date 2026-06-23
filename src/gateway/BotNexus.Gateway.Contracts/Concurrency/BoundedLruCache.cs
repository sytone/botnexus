namespace BotNexus.Gateway.Abstractions.Concurrency;

/// <summary>
/// A thread-safe, size-bounded least-recently-used (LRU) cache. When the number of
/// entries would exceed <see cref="Capacity"/>, the least-recently-used entry is
/// evicted. Both reads (<see cref="TryGet"/>) and writes (<see cref="Set"/>) mark an
/// entry as most-recently-used.
/// </summary>
/// <remarks>
/// <para>
/// This replaces an unbounded <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// used as a read-through cache, which on a long-running daemon retains every value
/// ever touched for the process lifetime (e.g. every session's full materialized
/// history). A bounded cache keeps the hot set in memory while cold entries fall
/// through to the backing store on the next read, restoring the memory profile a
/// disk-backed store is supposed to provide.
/// </para>
/// <para>
/// All operations take a single lock. The cache is intended for the gateway store
/// hot path where the per-entry critical section is a dictionary + linked-list
/// splice (O(1)); contention is low because writes for a given key are already
/// serialized upstream by a per-key/striped write lock.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();
    private readonly Lock _gate = new();

    /// <summary>Creates a cache bounded to <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">Maximum entries to retain. Must be positive.</param>
    /// <param name="comparer">Optional key comparer (e.g. ordinal for strings).</param>
    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    /// <summary>The maximum number of entries retained.</summary>
    public int Capacity => _capacity;

    /// <summary>The current number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to read a cached value. On a hit the entry becomes most-recently-used.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Inserts or updates an entry and marks it most-recently-used, evicting the
    /// least-recently-used entry if the cache is over capacity.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, value));
            _map[key] = node;
            _lru.AddFirst(node);

            if (_map.Count > _capacity)
            {
                var lruNode = _lru.Last;
                if (lruNode is not null)
                {
                    _lru.RemoveLast();
                    _map.Remove(lruNode.Value.Key);
                }
            }
        }
    }

    /// <summary>Removes an entry if present. Returns <c>true</c> when one was removed.</summary>
    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _map.Remove(key);
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
