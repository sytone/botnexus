namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Lock-free activity tracker using <see cref="Interlocked"/> for thread-safe timestamp updates.
/// </summary>
public sealed class ActivityTracker : IActivityTracker
{
    private long _lastActivityTicks = DateTimeOffset.UtcNow.Ticks;

    /// <inheritdoc />
    public void RecordActivity()
        => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.Ticks);

    /// <inheritdoc />
    public TimeSpan TimeSinceLastActivity
        => DateTimeOffset.UtcNow - LastActivityUtc;

    /// <inheritdoc />
    public DateTimeOffset LastActivityUtc
        => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
}
