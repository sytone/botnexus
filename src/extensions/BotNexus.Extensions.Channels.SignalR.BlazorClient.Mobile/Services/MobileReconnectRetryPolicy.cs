using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>
/// SignalR <see cref="IRetryPolicy"/> for the mobile hub path that widens the client's
/// automatic-reconnect budget well beyond the framework default (#1840).
/// </summary>
/// <remarks>
/// The stock <c>WithAutomaticReconnect()</c> retries only ~5 times at 3s (~15s total) and then
/// gives up, raising <c>Closed</c>. On the mobile hub path a PWA tunnelled through netbird can be
/// backgrounded far longer than that, so the default budget turns a transient background drop into
/// a terminal disconnect. This policy delegates to <see cref="MobileReconnectBackoff"/> -- a pure,
/// unit-tested exponential schedule (2s, 4s, 8s, 16s, then capped at 30s) -- and, crucially, never
/// returns <see langword="null"/>: it keeps retrying indefinitely so a returning app self-heals
/// instead of surfacing a dead-end error bar.
/// </remarks>
public sealed class MobileReconnectRetryPolicy : IRetryPolicy
{
    /// <inheritdoc />
    /// <remarks>
    /// Returns a non-null delay for every attempt so reconnection continues forever. The delay is a
    /// pure function of <see cref="RetryContext.PreviousRetryCount"/>, mirroring the backoff schedule
    /// used by the reconnect overlay so the two stay in lock-step.
    /// </remarks>
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);

        // PreviousRetryCount is zero-based on the first retry, matching MobileReconnectBackoff's
        // zero-based attempt index. Casting the long down is safe: the schedule caps at 30s long
        // before the count could overflow int.
        var attempt = retryContext.PreviousRetryCount > int.MaxValue
            ? int.MaxValue
            : (int)retryContext.PreviousRetryCount;
        return MobileReconnectBackoff.GetDelay(attempt);
    }
}
