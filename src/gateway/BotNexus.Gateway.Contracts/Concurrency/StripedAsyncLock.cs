namespace BotNexus.Gateway.Abstractions.Concurrency;

/// <summary>
/// A fixed-size pool of mutual-exclusion locks ("stripes") keyed by an arbitrary
/// key. A key is hashed to one of <see cref="StripeCount"/> stripes, so callers
/// holding locks for two distinct keys that map to the same stripe will serialize,
/// but the number of underlying <see cref="SemaphoreSlim"/> instances is bounded by
/// construction and never grows with the number of distinct keys.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the "one <see cref="SemaphoreSlim"/> per distinct key in a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>" pattern, which leaks one sync
/// primitive per key seen for the process lifetime on long-running daemons
/// (sessions, conversations, cron/sub-agent ids accumulating over days). Because no
/// per-key entry is ever created or removed, there is also no window in which an
/// exception/cancellation can strand a per-key lock: the stripe is always released
/// in the <see cref="IDisposable"/> returned from <see cref="AcquireAsync"/>.
/// </para>
/// <para>
/// Stripe collisions cause occasional false contention between unrelated keys. With
/// a default of 256 stripes this is negligible for the gateway's access patterns
/// (independent sessions/conversations rarely write concurrently), and it trades a
/// tiny, bounded amount of contention for the elimination of an unbounded leak.
/// </para>
/// </remarks>
public sealed class StripedAsyncLock
{
    /// <summary>The default number of stripes when none is specified.</summary>
    public const int DefaultStripeCount = 256;

    private readonly SemaphoreSlim[] _stripes;

    /// <summary>
    /// Creates a striped lock with <paramref name="stripeCount"/> stripes.
    /// </summary>
    /// <param name="stripeCount">
    /// The fixed number of underlying locks. Must be positive. Defaults to
    /// <see cref="DefaultStripeCount"/>.
    /// </param>
    public StripedAsyncLock(int stripeCount = DefaultStripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        _stripes = new SemaphoreSlim[stripeCount];
        for (var i = 0; i < stripeCount; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>The fixed number of stripes in this pool.</summary>
    public int StripeCount => _stripes.Length;

    /// <summary>
    /// Acquires the stripe for <paramref name="key"/>, awaiting if another caller
    /// currently holds the same stripe. Dispose the returned handle (e.g. with
    /// <c>using</c>) to release the stripe. Release is guaranteed even on exception
    /// because it happens in the handle's <see cref="IDisposable.Dispose"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type. <see cref="object.GetHashCode"/> is used.</typeparam>
    /// <param name="key">The key whose stripe to acquire. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A handle that releases the stripe when disposed.</returns>
    public async Task<IDisposable> AcquireAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        var stripe = GetStripe(key);
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(stripe);
    }

    /// <summary>
    /// Returns the <see cref="SemaphoreSlim"/> a key maps to. Exposed for tests that
    /// need to assert two keys land on the same or different stripes.
    /// </summary>
    internal SemaphoreSlim GetStripe<TKey>(TKey key)
        where TKey : notnull
    {
        // Non-negative, well-distributed index. & with (length-1) is only valid for
        // power-of-two lengths, so use a modulo on the absolute hash to support any
        // stripe count. int.MinValue is special-cased because Math.Abs(int.MinValue)
        // throws.
        var hash = key.GetHashCode();
        var index = (hash == int.MinValue ? 0 : Math.Abs(hash)) % _stripes.Length;
        return _stripes[index];
    }

    private sealed class Releaser(SemaphoreSlim stripe) : IDisposable
    {
        private SemaphoreSlim? _stripe = stripe;

        public void Dispose()
        {
            // Guard against double-dispose releasing the semaphore twice.
            var s = Interlocked.Exchange(ref _stripe, null);
            s?.Release();
        }
    }
}
