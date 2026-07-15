using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>Session pre-warming and multi-session subscription behavior.</summary>
public sealed class SessionWarmupOptions
{
    /// <summary>Whether session pre-warming is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether session pre-warming and multi-session subscription is enabled.",
        GroupName = "Session warmup",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "session-warmup", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of sessions pre-warmed per agent.</summary>
    [Display(
        Name = "Max sessions per agent",
        Description = "Maximum number of sessions pre-warmed per agent.",
        GroupName = "Session warmup",
        Order = 1)]
    [DefaultValue(10)]
    [Range(0, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "session-warmup", Order = 1)]
    public int MaxSessionsPerAgent { get; set; } = 10;

    /// <summary>Retention window, in hours, for warmed sessions.</summary>
    [Display(
        Name = "Retention window (hours)",
        Description = "Retention window, in hours, within which recently-active sessions are eligible for pre-warming.",
        GroupName = "Session warmup",
        Order = 2)]
    [DefaultValue(24)]
    [Range(0, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "session-warmup", Order = 2)]
    public int RetentionWindowHours { get; set; } = 24;

    /// <summary>Whether continuation sessions from the same channel are collapsed during warmup.</summary>
    [Display(
        Name = "Collapse channel continuations",
        Description = "Whether continuation sessions from the same channel are collapsed into one during pre-warming.",
        GroupName = "Session warmup",
        Order = 3)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "session-warmup", Order = 3)]
    public bool CollapseChannelContinuations { get; set; } = true;

    /// <summary>Convenience accessor projecting <see cref="RetentionWindowHours"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RetentionWindow
    {
        get => TimeSpan.FromHours(RetentionWindowHours);
        set => RetentionWindowHours = Math.Max(0, (int)Math.Ceiling(value.TotalHours));
    }
}
