namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Bounded exponential-backoff policy for channel adapter startup (#2447).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately bounded. The failure this fixes is a start path that retried <em>zero</em>
/// times; the opposite failure - retrying forever - is a real, separately-tracked defect
/// (#2386) and would be no improvement. After <see cref="MaxAttempts"/> the caller gives up
/// and logs a terminal message naming the channel.
/// </para>
/// </remarks>
public sealed class ChannelStartRetryPolicy
{
    /// <summary>Default total number of start attempts, including the first.</summary>
    public const int DefaultMaxAttempts = 4;

    /// <summary>Default delay before the first retry; doubles each subsequent attempt.</summary>
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Default ceiling applied to the computed backoff delay.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStartRetryPolicy"/> class.
    /// </summary>
    /// <param name="maxAttempts">Total attempts including the first. Values below 1 are clamped to 1.</param>
    /// <param name="baseDelay">Delay before the first retry. Negative values are clamped to zero.</param>
    /// <param name="maxDelay">Ceiling for the computed delay.</param>
    public ChannelStartRetryPolicy(
        int? maxAttempts = null,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
    {
        MaxAttempts = Math.Max(1, maxAttempts ?? DefaultMaxAttempts);
        BaseDelay = baseDelay is { } b ? (b > TimeSpan.Zero ? b : TimeSpan.Zero) : DefaultBaseDelay;
        MaxDelay = maxDelay is { } m && m > TimeSpan.Zero ? m : DefaultMaxDelay;
    }

    /// <summary>Total number of start attempts, including the first.</summary>
    public int MaxAttempts { get; }

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>Upper bound applied to any computed delay.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Computes the backoff delay to apply before the retry that follows
    /// <paramref name="completedAttempts"/> failed attempts.
    /// </summary>
    /// <param name="completedAttempts">Number of attempts already made (1 after the first failure).</param>
    /// <returns>The delay to wait, capped at <see cref="MaxDelay"/>.</returns>
    public TimeSpan ComputeDelay(int completedAttempts)
    {
        if (BaseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var exponent = Math.Max(0, completedAttempts - 1);

        // Cap the exponent before the shift so a large attempt count cannot overflow.
        var multiplier = exponent >= 16 ? 1 << 16 : 1 << exponent;
        var ticks = BaseDelay.Ticks * (long)multiplier;

        return ticks >= MaxDelay.Ticks || ticks < 0 ? MaxDelay : TimeSpan.FromTicks(ticks);
    }
}
