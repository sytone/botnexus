namespace BotNexus.Gateway.Configuration;

public sealed class SessionCleanupOptions
{
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan? ClosedSessionRetention { get; set; }

    /// <summary>
    /// Retention window for near-empty cron "noop wake" sessions. A cron session is treated as a
    /// noop when it has at most two persisted messages (a wake plus an optional NO_REPLY) &mdash;
    /// these accumulate rapidly from scheduled wakes that produce no user-visible work.
    /// <para>
    /// When set to a positive value, cron noop sessions whose <c>UpdatedAt</c> is older than this
    /// window are persisted-then-pruned by <see cref="SessionCleanupService"/>. This does not
    /// change wake or persist behaviour; it only deletes stale near-empty cron sessions after the
    /// fact. Defaults to 7 days and is user-configurable via
    /// <c>gateway:sessionCleanup:cronNoopRetention</c>. Set to <c>null</c> or a non-positive value
    /// to disable pruning entirely.
    /// </para>
    /// </summary>
    public TimeSpan? CronNoopRetention { get; set; } = TimeSpan.FromDays(7);
}
