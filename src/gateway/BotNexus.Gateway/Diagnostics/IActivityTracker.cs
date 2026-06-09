namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Tracks the last time the gateway processed meaningful work (message dispatch,
/// tool execution, provider response, etc.). Used by <see cref="LivenessWatchdogService"/>
/// to detect process hangs where the gateway is alive but unresponsive.
/// </summary>
public interface IActivityTracker
{
    /// <summary>
    /// Records that activity just occurred. Call from any hot path (dispatch, tool start/end, provider response).
    /// Thread-safe and allocation-free.
    /// </summary>
    void RecordActivity();

    /// <summary>
    /// Gets the duration since the last recorded activity.
    /// </summary>
    TimeSpan TimeSinceLastActivity { get; }

    /// <summary>
    /// Gets the UTC timestamp of the last recorded activity.
    /// </summary>
    DateTimeOffset LastActivityUtc { get; }
}
