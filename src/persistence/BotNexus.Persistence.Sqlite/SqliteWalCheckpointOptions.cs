namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Options for <see cref="SqliteWalCheckpointHostedService"/> (#1438). Bound by the gateway from
/// <c>gateway.walCheckpointIntervalMinutes</c>; kept in this project (rather than referencing a
/// gateway config type) so the persistence layer stays free of any dependency on the gateway.
/// </summary>
public sealed class SqliteWalCheckpointOptions
{
    /// <summary>Default periodic checkpoint interval, mirroring OpenClaw's 30-minute sweep.</summary>
    public const int DefaultIntervalMinutes = 30;

    /// <summary>
    /// Interval, in minutes, between periodic <c>PASSIVE</c> WAL checkpoints. Must be positive.
    /// Defaults to <see cref="DefaultIntervalMinutes"/> (30).
    /// </summary>
    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>
    /// Resolves the interval as a <see cref="TimeSpan"/>, clamping any non-positive configured
    /// value back to the 30-minute default so a misconfiguration cannot produce a hot loop.
    /// </summary>
    public TimeSpan Interval =>
        TimeSpan.FromMinutes(IntervalMinutes > 0 ? IntervalMinutes : DefaultIntervalMinutes);
}
