namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Lock-free activity tracker using <see cref="Interlocked"/> for thread-safe timestamp updates.
/// </summary>
public sealed class ActivityTracker : IActivityTracker
{
    private readonly TimeProvider _timeProvider;
    private long _lastActivityTicks;

    /// <summary>
    /// Creates an activity tracker using the system clock in production and an injectable clock for
    /// deterministic elapsed-time verification.
    /// </summary>
    public ActivityTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivityTicks = _timeProvider.GetUtcNow().Ticks;
    }

    /// <inheritdoc />
    public void RecordActivity()
        => Interlocked.Exchange(ref _lastActivityTicks, _timeProvider.GetUtcNow().Ticks);

    /// <inheritdoc />
    public TimeSpan TimeSinceLastActivity
        => _timeProvider.GetUtcNow() - LastActivityUtc;

    /// <inheritdoc />
    public DateTimeOffset LastActivityUtc
        => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
}
