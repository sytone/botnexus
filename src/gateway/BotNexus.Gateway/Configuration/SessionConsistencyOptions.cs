using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Timing, thresholds, and opt-out controls for the session/conversation
/// consistency monitor (<c>SessionConsistencyChecker</c> and its hosted driver).
/// Bound from <c>gateway:sessionConsistency</c>.
/// </summary>
/// <remarks>
/// The monitor detects and safely repairs persisted lifecycle discrepancies -
/// conversations whose <c>ActiveSessionId</c> points at a stale/terminated cron
/// session while a live channel session exists, dangling pointers, and cron
/// sessions left <c>Active</c> long past completion (issue #2046). Repairs go
/// through the session/conversation store APIs; there is no raw SQL mutation.
/// </remarks>
public sealed class SessionConsistencyOptions
{
    /// <summary>
    /// Master switch. When <c>false</c> the hosted driver does not run at all and
    /// no checks or repairs occur. Defaults to <c>true</c>.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Enables the background session consistency monitor and auto-heal path.",
        GroupName = "Session consistency",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "session-consistency", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c> the monitor reports discrepancies but performs no mutations
    /// (report-only). Operators can validate detection before enabling repair.
    /// Defaults to <c>false</c> (repairs enabled).
    /// </summary>
    [Display(
        Name = "Dry run",
        Description = "Report discrepancies without repairing them. Use to validate detection before enabling auto-heal.",
        GroupName = "Session consistency",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "session-consistency", Order = 1)]
    public bool DryRun { get; set; }

    /// <summary>How often the periodic consistency check runs.</summary>
    [Display(
        Name = "Check interval",
        Description = "How often the session consistency monitor scans for discrepancies.",
        GroupName = "Session consistency",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-consistency", Order = 2)]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Delay after host startup before the first check runs, so agent
    /// registration, store hydration, and interrupted-turn recovery settle first
    /// and the initial pass does not race those subsystems.
    /// </summary>
    [Display(
        Name = "Startup delay",
        Description = "Delay after host startup before the first consistency check, allowing registration and recovery to settle.",
        GroupName = "Session consistency",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-consistency", Order = 3)]
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Conservative age (based on <c>UpdatedAt</c>) after which a cron session still
    /// marked <c>Active</c> - with no live turn registered - is treated as a leaked
    /// active session and sealed. Set generously to avoid terminalizing a genuinely
    /// long-running job.
    /// </summary>
    [Display(
        Name = "Stale active cron threshold",
        Description = "Age after which a cron session still marked Active with no live turn is sealed.",
        GroupName = "Session consistency",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-consistency", Order = 4)]
    public TimeSpan StaleActiveCronThreshold { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Upper bound on conversations examined in a single pass, keeping each run
    /// bounded on large stores. A value &lt;= 0 disables the cap.
    /// </summary>
    [Display(
        Name = "Max conversations per run",
        Description = "Upper bound on conversations examined per consistency pass. Non-positive disables the cap.",
        GroupName = "Session consistency",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-consistency", Order = 5)]
    public int MaxConversationsPerRun { get; set; } = 5000;
}
