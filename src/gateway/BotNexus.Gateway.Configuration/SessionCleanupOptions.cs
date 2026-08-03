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
}
