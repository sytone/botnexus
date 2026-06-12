namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Tracks the number of concurrently active agent loops for capacity monitoring.
/// </summary>
public interface IActiveLoopTracker
{
    /// <summary>
    /// Current number of active agent loops.
    /// </summary>
    int ActiveCount { get; }

    /// <summary>
    /// Peak concurrent loop count since startup.
    /// </summary>
    int PeakCount { get; }

    /// <summary>
    /// Total number of completed loops since startup.
    /// </summary>
    long TotalCompleted { get; }

    /// <summary>
    /// Records that an agent loop has started.
    /// </summary>
    void TrackStart();

    /// <summary>
    /// Records that an agent loop has ended.
    /// </summary>
    void TrackEnd();
}
