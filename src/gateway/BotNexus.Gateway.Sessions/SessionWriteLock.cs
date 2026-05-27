using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// In-memory <see cref="ISessionWriteLock"/> implementation backed by
/// per-session <see cref="SemaphoreSlim"/> instances stored in a
/// refcounted dictionary. Dictionary bookkeeping is serialized through a
/// single gate object — concurrent acquires across different session ids
/// run fully in parallel because the gate is released before
/// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> is awaited.
/// </summary>
/// <remarks>
/// The refcounted design ensures the slot is removed from the dictionary
/// as soon as the last in-flight acquire completes its release. Cancelling
/// a pending <c>WaitAsync</c> still decrements the refcount, so a cancelled
/// caller never leaks a permanent dictionary entry. See <see cref="ISessionWriteLock"/>
/// for the scope/reentrancy contract.
/// </remarks>
public sealed class SessionWriteLock : ISessionWriteLock, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, LockSlot> _locks = new();
    private bool _disposed;

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LockSlot slot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_locks.TryGetValue(sessionId, out var existing))
            {
                existing = new LockSlot();
                _locks[sessionId] = existing;
            }
            existing.RefCount++;
            slot = existing;
        }

        try
        {
            await slot.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The wait was cancelled or faulted before we got the semaphore.
            // We already took a refcount in the gate above and must give it
            // back, otherwise the slot is permanently pinned in the dictionary.
            DecrementRefCount(sessionId, slot);
            throw;
        }

        return new Releaser(this, sessionId, slot);
    }

    private void Release(SessionId sessionId, LockSlot slot)
    {
        // Order matters: release the semaphore BEFORE manipulating the
        // dictionary so a waiter parked on this slot's WaitAsync can resume
        // promptly. The refcount it holds keeps the slot alive across the
        // window between our refcount decrement and the waiter's eventual
        // release — there is no race that frees a slot under a live waiter.
        slot.Semaphore.Release();
        DecrementRefCount(sessionId, slot);
    }

    private void DecrementRefCount(SessionId sessionId, LockSlot slot)
    {
        lock (_gate)
        {
            slot.RefCount--;
            if (slot.RefCount == 0 && _locks.TryGetValue(sessionId, out var stored) && ReferenceEquals(stored, slot))
            {
                _locks.Remove(sessionId);
                slot.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// Disposes every outstanding semaphore. Callers must not be holding any
    /// leases at this point; in practice the lock is registered as a
    /// singleton and disposed only on host shutdown.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var slot in _locks.Values)
                slot.Semaphore.Dispose();
            _locks.Clear();
        }
    }

    /// <summary>
    /// Exposed for tests: returns the current number of dictionary entries.
    /// In production this is observable only via the singleton's internal
    /// state and is not part of the public contract.
    /// </summary>
    internal int OutstandingSlotCount
    {
        get
        {
            lock (_gate)
                return _locks.Count;
        }
    }

    /// <summary>
    /// Exposed for tests: returns the current refcount of the slot for
    /// <paramref name="sessionId"/>, or 0 if no slot exists. This is the
    /// "how many callers currently hold OR are waiting for this session's
    /// lock" probe — used by concurrency tests to deterministically detect
    /// that a waiter has entered <c>WaitAsync</c> (refcount == 2) without
    /// relying on a timing-based <c>Task.Delay</c>.
    /// </summary>
    internal int RefCountFor(SessionId sessionId)
    {
        lock (_gate)
            return _locks.TryGetValue(sessionId, out var slot) ? slot.RefCount : 0;
    }

    private sealed class LockSlot
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    private sealed class Releaser(SessionWriteLock owner, SessionId sessionId, LockSlot slot) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(sessionId, slot);
            return ValueTask.CompletedTask;
        }
    }
}
