namespace BotNexus.Agent.Providers.Core.Resilience;

/// <summary>
/// Shared bounded-jitter helper for provider retry backoff (#3035).
/// <para>
/// Both provider retry paths - the agent loop's transient-failure backoff and the transport-level
/// <see cref="TransientHttpRetryHandler"/> - previously computed a <em>purely deterministic</em> delay.
/// Every agent on an instance shares one provider endpoint, so a single upstream 429/503 throttles
/// them all at the same instant, they all sleep for byte-identical intervals, and they all wake and
/// re-hit the provider in lockstep. That converts a transient blip into a sustained self-inflicted
/// outage: the classic thundering herd.
/// </para>
/// <para>
/// The remedy is to spread the wake-ups over a window rather than to change the backoff curve. Jitter
/// is therefore <strong>additive and one-sided</strong>: <c>delay = base + base * factor * random</c>
/// with <c>random</c> in <c>[0,1]</c>. A one-sided term can never shorten a delay below the value the
/// caller asked for, so adding jitter cannot make a retry storm <em>more</em> aggressive - the only
/// failure mode it could introduce is being slightly slower, never hotter.
/// </para>
/// <para>
/// The randomness is supplied as an injectable <c>Func&lt;double&gt;</c> seam rather than being read
/// from <see cref="Random.Shared"/> inline. Inline randomness would make the delay untestable: a test
/// could only assert a range, which cannot distinguish "jitter applied" from "jitter accidentally
/// removed" for small factors. With the seam a test pins the source to <c>0</c> and asserts the exact
/// pre-existing sequence (proving no behaviour change at the deterministic bound), then pins it to
/// <c>1</c> and asserts the strict upper bound.
/// </para>
/// </summary>
public static class RetryJitter
{
    /// <summary>
    /// Default jitter factor: the delay may be stretched by up to 25%. Matches the upstream OpenCode
    /// value (<c>RETRY_JITTER_FACTOR</c>, commit <c>c7898683</c>). Large enough to desynchronise a herd
    /// of concurrent retriers, small enough that the effective backoff curve is unchanged in shape.
    /// </summary>
    public const double DefaultJitterFactor = 0.25;

    /// <summary>
    /// The process-wide randomness source used when a caller does not inject one.
    /// <see cref="Random.Shared"/> is thread-safe, so concurrent retriers each draw an independent value.
    /// </summary>
    public static double DefaultRandomSource() => Random.Shared.NextDouble();

    /// <summary>
    /// Applies bounded additive jitter to a base delay.
    /// </summary>
    /// <param name="baseDelay">The un-jittered delay. Non-positive values are returned unchanged.</param>
    /// <param name="random">
    /// A value in <c>[0,1]</c> from the injected randomness source. Values outside the range are clamped
    /// so a misbehaving source can never produce a negative or unbounded delay.
    /// </param>
    /// <param name="jitterFactor">The maximum proportional stretch. Non-positive disables jitter.</param>
    /// <returns><c>baseDelay * (1 + jitterFactor * random)</c>.</returns>
    public static TimeSpan Apply(TimeSpan baseDelay, double random, double jitterFactor = DefaultJitterFactor)
        => TimeSpan.FromMilliseconds(ApplyMs(baseDelay.TotalMilliseconds, random, jitterFactor));

    /// <summary>
    /// Millisecond overload of <see cref="Apply(TimeSpan, double, double)"/> for callers that already
    /// track their backoff as a millisecond scalar.
    /// </summary>
    /// <param name="baseDelayMs">The un-jittered delay in milliseconds. Non-positive is returned unchanged.</param>
    /// <param name="random">A value in <c>[0,1]</c>; clamped defensively.</param>
    /// <param name="jitterFactor">The maximum proportional stretch. Non-positive disables jitter.</param>
    public static double ApplyMs(double baseDelayMs, double random, double jitterFactor = DefaultJitterFactor)
    {
        if (baseDelayMs <= 0 || jitterFactor <= 0)
        {
            return baseDelayMs;
        }

        // A NaN source would poison the arithmetic silently; treat it as "no jitter".
        if (double.IsNaN(random))
        {
            return baseDelayMs;
        }

        var bounded = Math.Clamp(random, 0d, 1d);
        return baseDelayMs * (1d + (jitterFactor * bounded));
    }
}
