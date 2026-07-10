namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>
/// Exponential-backoff schedule for the mobile auto-retrying reconnect overlay (#1839).
/// </summary>
/// <remarks>
/// Blazor's default WASM reconnect budget gives up after ~5 retries x 3s (~15s) and then paints
/// the raw <c>#blazor-error-ui</c> banner. On iOS a standalone PWA can be suspended for far longer
/// than that before the user returns, so trusting the default budget turns a transient background
/// drop into a dead-end error bar. This schedule widens the retry window: it starts at 2s, doubles
/// each attempt, and caps at 30s, then holds at the cap indefinitely so a returning backgrounded
/// app keeps trying and self-heals rather than surfacing a terminal error.
///
/// The schedule is a pure function of the attempt index so it can be unit tested without timers,
/// and the overlay drives it forward one tick at a time while the hub remains unreachable.
/// </remarks>
public static class MobileReconnectBackoff
{
    // 2s first retry keeps the returning-app case snappy without hammering the gateway.
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The ceiling for a single retry interval. Attempts beyond the doubling range hold here so the
    /// overlay keeps retrying forever at a steady, low-frequency cadence instead of giving up.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the delay to wait before reconnect attempt number <paramref name="attempt"/>
    /// (zero-based): 2s, 4s, 8s, 16s, then capped at <see cref="MaxDelay"/> (30s) for every
    /// subsequent attempt. Never returns a terminal/zero delay -- retries continue indefinitely.
    /// </summary>
    /// <param name="attempt">The zero-based attempt index. Must be non-negative.</param>
    /// <returns>The backoff delay for that attempt, capped at <see cref="MaxDelay"/>.</returns>
    public static TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // Cap the exponent before shifting so large attempt indices cannot overflow; once the
        // computed delay reaches the cap, everything past it clamps to MaxDelay anyway.
        var seconds = BaseDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt, 30));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
