namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The result of a liveness-verified hub resume (#1838).
/// </summary>
public enum HubResumeOutcome
{
    /// <summary>The liveness probe succeeded; the existing connection was kept.</summary>
    Alive,

    /// <summary>The liveness probe failed (zombie socket); the connection was torn down and rebuilt.</summary>
    Rebuilt,

    /// <summary>A resume was already in flight; this call was skipped by the reentrancy guard.</summary>
    Skipped,
}

/// <summary>
/// Coordinates the liveness-verified hub reset performed when a mobile PWA returns to the
/// foreground (#1838). iOS silently recycles background WebSockets, leaving
/// <c>HubConnectionState</c>/<c>readyState</c> reporting <c>Connected</c>/<c>OPEN</c> on a dead
/// socket ("zombie"). Trusting that state means Blazor never rebuilds the connection and
/// eventually exhausts its reconnect budget, surfacing <c>#blazor-error-ui</c>.
/// </summary>
/// <remarks>
/// The algorithm is deliberately transport-agnostic and delegate-driven so it can be unit
/// tested without a real <c>HubConnection</c>: callers supply a <paramref name="probe"/> (a
/// short round-trip), a <paramref name="rebuild"/> (stop/dispose + fresh negotiate + re-register
/// + re-subscribe), and a <paramref name="refresh"/> (the existing data-refresh + UI update path).
/// A single reentrancy guard ensures rapid visibility toggles cannot stack concurrent rebuilds.
/// </remarks>
public sealed class HubResumeCoordinator
{
    // 0 = idle, 1 = a resume is in flight. Guarded via Interlocked so rapid visibility
    // toggles collapse to a single active resume rather than stacking rebuilds.
    private int _inFlight;

    /// <summary>
    /// The maximum time the liveness probe may take before it is treated as a failed
    /// (zombie) connection. Kept short (~2-3s) so a dead socket is detected quickly on
    /// resume rather than waiting on TCP-level timeouts. Defaults to 2.5 seconds.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Runs the liveness-verified resume: probe first, and only tear down + rebuild when the
    /// probe fails. The refresh path runs on both branches so data/UI are updated regardless.
    /// </summary>
    /// <param name="probe">
    /// A lightweight round-trip against the live connection. Receives a cancellation token that
    /// fires after <see cref="ProbeTimeout"/>. Returns <c>true</c> when the connection is alive.
    /// Any thrown exception (including cancellation) is treated as a failed liveness check.
    /// </param>
    /// <param name="rebuild">
    /// Tears down the existing connection (stop/dispose) and rebuilds it with a fresh negotiate,
    /// re-registering handlers and restoring subscriptions. Invoked only when the probe fails.
    /// </param>
    /// <param name="refresh">The existing data-refresh + UI-update path, invoked on both branches.</param>
    /// <returns>
    /// <see cref="HubResumeOutcome.Alive"/> when the probe passed, <see cref="HubResumeOutcome.Rebuilt"/>
    /// when a rebuild occurred, or <see cref="HubResumeOutcome.Skipped"/> when another resume was
    /// already in flight.
    /// </returns>
    public async Task<HubResumeOutcome> ResumeAsync(
        Func<CancellationToken, Task<bool>> probe,
        Func<Task> rebuild,
        Func<Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(refresh);

        // Reentrancy guard: if a resume is already running, skip this one entirely.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return HubResumeOutcome.Skipped;

        try
        {
            var alive = await ProbeIsAliveAsync(probe);
            var outcome = HubResumeOutcome.Alive;

            if (!alive)
            {
                await rebuild();
                outcome = HubResumeOutcome.Rebuilt;
            }

            await refresh();
            return outcome;
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    // Runs the probe under a short timeout. Any failure mode -- returning false, throwing,
    // or exceeding ProbeTimeout -- is collapsed to "not alive" so the caller rebuilds rather
    // than trusting a possibly-zombie socket.
    private async Task<bool> ProbeIsAliveAsync(Func<CancellationToken, Task<bool>> probe)
    {
        using var cts = new CancellationTokenSource(ProbeTimeout);
        try
        {
            return await probe(cts.Token);
        }
        catch
        {
            return false;
        }
    }
}
