namespace BotNexus.Gateway.Channels.Startup;

/// <summary>
/// Outcome of recording a single failure against a <see cref="ChannelLoopCircuitBreaker"/>.
/// </summary>
/// <param name="Kind">How the failure was classified.</param>
/// <param name="RetryDelay">How long the loop should wait before its next attempt. Always
/// <see cref="TimeSpan.Zero"/> when <paramref name="ShouldStop"/> is <see langword="true"/>.</param>
/// <param name="ShouldStop">Whether the loop must exit rather than retry.</param>
/// <param name="CircuitOpened">Whether this failure is the one that opened the circuit. Used by
/// callers to emit exactly one ERR line per degradation rather than one per attempt.</param>
public readonly record struct ChannelLoopFailureResponse(
    ChannelFailureKind Kind,
    TimeSpan RetryDelay,
    bool ShouldStop,
    bool CircuitOpened);

/// <summary>
/// Bounds a long-lived channel receive/polling loop so a non-transient fault cannot become a
/// retry storm (#2386).
/// </summary>
/// <remarks>
/// <para>
/// The defect this exists to prevent: both the Service Bus receive path and the Telegram polling
/// loop caught every exception, logged it at ERR and retried immediately. A revoked Azure grant
/// (AADSTS50173) and a Telegram HTTP 409 "terminated by other getUpdates request" - neither of
/// which any amount of retrying can clear - produced 9,452 ERR lines in 24 hours while the
/// transports were silently dead.
/// </para>
/// <para>
/// Classification is delegated to <see cref="ChannelFailureClassifier"/>, the same seam the
/// adapter <em>start</em> retry (#2447) uses, so there is exactly one definition of
/// transient-vs-terminal in the platform. Unrecognised failures are terminal: this fails closed,
/// because a silent infinite retry is the failure mode being removed.
/// </para>
/// <para>Instances are not thread-safe; each loop owns its own breaker.</para>
/// </remarks>
public sealed class ChannelLoopCircuitBreaker
{
    /// <summary>Delay applied before the first retry of a transient fault; doubles thereafter.</summary>
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling for the computed backoff delay.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private int _consecutiveTransientFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelLoopCircuitBreaker"/> class.
    /// </summary>
    /// <param name="loopDescription">Human-readable identity of the loop (for example
    /// <c>"telegram bot 'farnsworth' polling loop"</c>). Surfaced in the single degraded-loop
    /// error line so an operator can tell which transport went dark.</param>
    /// <param name="baseDelay">Delay before the first transient retry. Defaults to 2 seconds.</param>
    /// <param name="maxDelay">Ceiling for the backoff. Defaults to 30 seconds.</param>
    public ChannelLoopCircuitBreaker(
        string loopDescription,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopDescription);

        LoopDescription = loopDescription;
        _baseDelay = baseDelay is { } b && b > TimeSpan.Zero ? b : DefaultBaseDelay;
        _maxDelay = maxDelay is { } m && m > TimeSpan.Zero ? m : DefaultMaxDelay;
    }

    /// <summary>Human-readable identity of the loop this breaker guards.</summary>
    public string LoopDescription { get; }

    /// <summary>Whether a terminal failure has parked this loop.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Number of consecutive transient failures since the last success.</summary>
    public int ConsecutiveTransientFailures => _consecutiveTransientFailures;

    /// <summary>
    /// Clears the transient backoff schedule. Call after any successful loop iteration so a
    /// recovered transport returns to full polling speed instead of staying at the 30s ceiling.
    /// </summary>
    public void RecordSuccess() => _consecutiveTransientFailures = 0;

    /// <summary>
    /// Classifies <paramref name="exception"/> and returns what the loop should do next.
    /// </summary>
    /// <param name="exception">The failure raised by the loop iteration.</param>
    /// <returns>The retry delay, or an instruction to stop when the circuit trips.</returns>
    /// <remarks>
    /// A terminal failure opens the circuit permanently for this breaker instance. The
    /// <see cref="ChannelLoopFailureResponse.CircuitOpened"/> flag is <see langword="true"/> only
    /// on the transition, which is what makes "log once, do not spin" expressible by the caller.
    /// </remarks>
    public ChannelLoopFailureResponse RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var kind = ChannelFailureClassifier.Classify(exception);

        if (kind == ChannelFailureKind.Transient)
        {
            _consecutiveTransientFailures++;
            return new ChannelLoopFailureResponse(
                ChannelFailureKind.Transient,
                ComputeDelay(_consecutiveTransientFailures),
                ShouldStop: false,
                CircuitOpened: false);
        }

        var opened = !IsOpen;
        IsOpen = true;

        return new ChannelLoopFailureResponse(
            ChannelFailureKind.Terminal,
            TimeSpan.Zero,
            ShouldStop: true,
            CircuitOpened: opened);
    }

    /// <summary>
    /// Computes the bounded exponential backoff for the given number of consecutive failures.
    /// </summary>
    private TimeSpan ComputeDelay(int consecutiveFailures)
    {
        var exponent = Math.Max(0, consecutiveFailures - 1);

        // Clamp the exponent BEFORE shifting: an uncapped loop reaches shift widths that wrap
        // int and would hand back a zero or negative delay - i.e. the hot loop we are removing.
        var multiplier = exponent >= 16 ? 1 << 16 : 1 << exponent;
        var ticks = _baseDelay.Ticks * (long)multiplier;

        return ticks >= _maxDelay.Ticks || ticks < 0 ? _maxDelay : TimeSpan.FromTicks(ticks);
    }
}
