namespace BotNexus.Testing;

/// <summary>
/// Provides a clock that advances only when a test explicitly moves it. This type controls clock
/// reads only; tests that exercise timers need a scheduler-aware fake instead.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _utcTicks;

    /// <summary>Creates a clock at the supplied instant, normalized to UTC.</summary>
    public ManualTimeProvider(DateTimeOffset start)
    {
        _utcTicks = start.UtcTicks;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <summary>Moves the clock by the supplied duration.</summary>
    public void Advance(TimeSpan duration)
        => Interlocked.Add(ref _utcTicks, duration.Ticks);

    /// <summary>Moves the clock to an explicit instant.</summary>
    public void SetUtcNow(DateTimeOffset value)
        => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
}