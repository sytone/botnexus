namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Lock-free implementation of <see cref="IActiveLoopTracker"/> using <see cref="Interlocked"/>.
/// </summary>
public sealed class ActiveLoopTracker : IActiveLoopTracker
{
    private int _activeCount;
    private int _peakCount;
    private long _totalCompleted;

    /// <inheritdoc />
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <inheritdoc />
    public int PeakCount => Volatile.Read(ref _peakCount);

    /// <inheritdoc />
    public long TotalCompleted => Interlocked.Read(ref _totalCompleted);

    /// <inheritdoc />
    public void TrackStart()
    {
        var current = Interlocked.Increment(ref _activeCount);
        // Update peak using compare-and-swap loop
        int peak;
        while (current > (peak = Volatile.Read(ref _peakCount)))
        {
            if (Interlocked.CompareExchange(ref _peakCount, current, peak) == peak)
                break;
        }
    }

    /// <inheritdoc />
    public void TrackEnd()
    {
        Interlocked.Decrement(ref _activeCount);
        Interlocked.Increment(ref _totalCompleted);
    }
}
