namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Exponential-backoff schedule for the desktop portal's terminal-close reconnect loop (#2624).
/// </summary>
/// <remarks>
/// SignalR's stock <c>WithAutomaticReconnect()</c> budget (~5 retries x 3s) expires long before a
/// <c>botnexus update</c> finishes rebuilding and restarting the gateway, after which <c>Closed</c>
/// is raised and nothing re-dials. This schedule drives the re-dial loop that runs *after* that
/// budget is spent.
///
/// <para>
/// The shape mirrors the mobile schedule that already ships (2s, 4s, 8s, 16s, then held at 30s),
/// but the two are deliberately separate types: the mobile schedule lives in the Mobile assembly,
/// which Core cannot reference, and mobile's copy drives SignalR's <em>in-budget</em> automatic
/// reconnect rather than the terminal-close loop. Keeping the values aligned is intentional; the
/// duplication is a project-reference constraint, not an oversight.
/// </para>
///
/// <para>
/// The cap is load-bearing in both directions. Without it, an uncapped exponential means a gateway
/// that returns after 45 minutes goes unnoticed for a further long interval -- the reported symptom
/// in a new form. Without growth, a fixed short interval is a tight loop against a down gateway.
/// Worst-case detection latency is therefore exactly one cap interval, regardless of how long the
/// outage lasted.
/// </para>
/// </remarks>
public static class DesktopReconnectBackoff
{
    /// <summary>First re-dial delay. Short enough that a fast gateway bounce is barely noticed.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling for a single retry interval. Attempts past the doubling range hold here forever, so a
    /// multi-hour outage settles into infrequent polling instead of either hammering or giving up.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the delay to wait before re-dial attempt number <paramref name="attempt"/>
    /// (zero-based): 2s, 4s, 8s, 16s, then capped at <see cref="MaxDelay"/> for every subsequent
    /// attempt. Never returns a terminal value -- there is no attempt at which the caller is told
    /// to stop.
    /// </summary>
    /// <param name="attempt">The zero-based attempt index. Must be non-negative.</param>
    /// <returns>The backoff delay for that attempt, capped at <see cref="MaxDelay"/>.</returns>
    public static TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // Clamp the exponent before shifting so a long outage's large attempt index cannot overflow
        // the double; everything past the cap clamps to MaxDelay anyway.
        var seconds = BaseDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt, 30));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
