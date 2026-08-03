using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// World-level policy for the age-based automatic sweep of completed sub-agent workspace
/// directories that accumulate under the persistent agents root
/// (<c>&lt;BotNexus home&gt;/agents/&lt;parent&gt;--subagent--&lt;archetype&gt;--&lt;guid&gt;</c>).
/// <para>
/// This sweep is the automatic, age-based complement to the manual, registration-based
/// <c>doctor</c> reconciliation (issue #2039): it only ever considers directories whose name
/// contains the <c>--subagent--</c> marker, so top-level registered agent workspaces are never
/// touched. It is also complementary to the temp-root pruning of #1942, which only covers the
/// OS-temp sub-agent workspace root.
/// </para>
/// </summary>
public sealed class SubAgentWorkspaceSweepOptions
{
    /// <summary>
    /// Whether the automatic age-based sweep is enabled. The issue asks for automatic cleanup, so
    /// this defaults to <c>true</c> with conservative TTL / grace values.
    /// </summary>
    [Display(
        Name = "Enable sub-agent workspace sweep",
        Description = "Whether the automatic age-based sweep of completed sub-agent workspace directories is enabled.",
        GroupName = "Sub-agent workspace sweep",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "subagent-workspace-sweep", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Idle time-to-live: a sub-agent workspace directory whose last-write time is older than this
    /// many hours becomes eligible for removal. Zero or negative disables removal. Default 24 hours.
    /// </summary>
    [Display(
        Name = "Retention (hours)",
        Description = "Idle hours after which a completed sub-agent workspace directory is removed. Zero or negative disables removal.",
        GroupName = "Sub-agent workspace sweep",
        Order = 1)]
    [DefaultValue(24)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "subagent-workspace-sweep", Order = 1)]
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Safety grace window: a directory modified within this many minutes is always skipped so a
    /// live / in-flight worker is never yanked, even if <see cref="RetentionHours"/> would otherwise
    /// permit removal. Default 60 minutes.
    /// </summary>
    [Display(
        Name = "Grace window (minutes)",
        Description = "A sub-agent workspace directory modified within this many minutes is always skipped, protecting live workers.",
        GroupName = "Sub-agent workspace sweep",
        Order = 2)]
    [DefaultValue(60)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "subagent-workspace-sweep", Order = 2)]
    public int GraceMinutes { get; set; } = 60;

    /// <summary>
    /// How often the sweep service scans the agents root. Defaults to once per hour. The sweep also
    /// runs once shortly after gateway startup.
    /// </summary>
    [Display(
        Name = "Check interval",
        Description = "How often the sub-agent workspace sweep scans the agents root.",
        GroupName = "Sub-agent workspace sweep",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "subagent-workspace-sweep", Order = 3)]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Idle retention as a <see cref="TimeSpan"/>; non-positive when disabled.</summary>
    public TimeSpan Retention => RetentionHours > 0 ? TimeSpan.FromHours(RetentionHours) : TimeSpan.Zero;

    /// <summary>Grace window as a <see cref="TimeSpan"/>; clamped to non-negative.</summary>
    public TimeSpan Grace => GraceMinutes > 0 ? TimeSpan.FromMinutes(GraceMinutes) : TimeSpan.Zero;
}
