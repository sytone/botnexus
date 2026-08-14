using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>Background session cleanup timing and retention windows.</summary>
public sealed class SessionCleanupOptions
{
    /// <summary>How often the cleanup service scans for expired sessions.</summary>
    [Display(
        Name = "Check interval",
        Description = "How often the session cleanup service scans for expired sessions.",
        GroupName = "Session cleanup",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-cleanup", Order = 0)]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Time-to-live after which an idle session becomes eligible for cleanup.</summary>
    [Display(
        Name = "Session TTL",
        Description = "Time-to-live after which an idle session becomes eligible for cleanup.",
        GroupName = "Session cleanup",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-cleanup", Order = 1)]
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Optional retention window for closed sessions before they are pruned.</summary>
    [Display(
        Name = "Closed session retention",
        Description = "Optional retention window for closed sessions before they are pruned. Empty keeps closed sessions indefinitely.",
        GroupName = "Session cleanup",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-cleanup", Order = 2)]
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
    [Display(
        Name = "Cron noop retention",
        Description = "Retention window for near-empty cron noop-wake sessions before they are pruned. Empty or non-positive disables pruning.",
        GroupName = "Session cleanup",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-cleanup", Order = 3)]
    public TimeSpan? CronNoopRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Optional total disk budget, in bytes, for an agent's sessions directory (issue #2848).
    /// <para>
    /// <b>A null, zero, or negative value disables the budget entirely</b> and nothing is ever
    /// evicted by the size path. This contract is deliberate and load-bearing: upstream OpenClaw
    /// parsed <c>0</c> as a literal zero-byte budget, so enforce mode deleted <em>every</em>
    /// session artifact (openclaw#119422). Treating <c>&lt;= 0</c> as "disabled" is the fix, and
    /// BotNexus adopts it from the outset rather than rediscovering it in production.
    /// </para>
    /// <para>Defaults to <c>null</c>, so the out-of-the-box sweep behaves exactly as before.</para>
    /// </summary>
    [Display(
        Name = "Max disk bytes",
        Description = "Optional total disk budget in bytes for an agent's sessions directory. Empty, zero, or negative disables the budget and evicts nothing.",
        GroupName = "Session cleanup",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "session-cleanup", Order = 4)]
    public long? MaxDiskBytes { get; set; }

    /// <summary>
    /// Target footprint, in bytes, that enforce-mode eviction reclaims down to before it stops.
    /// Defaults to 80% of <see cref="MaxDiskBytes"/> when unset, so eviction reclaims headroom
    /// instead of re-triggering on every cycle at exactly the budget line. Values outside
    /// <c>(0, MaxDiskBytes]</c> fall back to the 80% default.
    /// </summary>
    [Display(
        Name = "High water bytes",
        Description = "Target size to reclaim down to when evicting. Empty defaults to 80% of the max disk bytes.",
        GroupName = "Session cleanup",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "session-cleanup", Order = 5)]
    public long? HighWaterBytes { get; set; }

    /// <summary>
    /// Whether exceeding the budget only logs pressure (<see cref="SessionDiskBudgetMode.Warn"/>,
    /// the default) or actually evicts sessions (<see cref="SessionDiskBudgetMode.Enforce"/>).
    /// Warn is the default so enabling a budget can never delete data as a surprise side effect of
    /// setting one number.
    /// </summary>
    [Display(
        Name = "Disk budget mode",
        Description = "Warn logs disk pressure only; Enforce evicts oldest-first down to the high-water mark.",
        GroupName = "Session cleanup",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "session-cleanup", Order = 6)]
    public SessionDiskBudgetMode DiskBudgetMode { get; set; } = SessionDiskBudgetMode.Warn;

    /// <summary>
    /// Returns the effective budget, or <c>null</c> when the budget is disabled. Centralises the
    /// <c>&lt;= 0 means disabled</c> contract so no call site can reintroduce openclaw#119422 by
    /// comparing against a raw zero.
    /// </summary>
    public long? ResolveMaxDiskBytes() =>
        MaxDiskBytes is > 0 ? MaxDiskBytes.Value : null;

    /// <summary>
    /// Returns the effective high-water mark for a resolved <paramref name="maxDiskBytes"/>.
    /// </summary>
    public long ResolveHighWaterBytes(long maxDiskBytes)
    {
        if (HighWaterBytes is > 0 && HighWaterBytes.Value <= maxDiskBytes)
            return HighWaterBytes.Value;

        var eightyPercent = (long)(maxDiskBytes * 0.8d);
        return eightyPercent > 0 ? eightyPercent : maxDiskBytes;
    }
}

/// <summary>How the session-directory disk budget reacts to exceeding <see cref="SessionCleanupOptions.MaxDiskBytes"/>.</summary>
public enum SessionDiskBudgetMode
{
    /// <summary>Log a warning describing the pressure and delete nothing. The default.</summary>
    Warn = 0,

    /// <summary>Evict oldest-first until the footprint is at or below the high-water mark.</summary>
    Enforce = 1,
}
