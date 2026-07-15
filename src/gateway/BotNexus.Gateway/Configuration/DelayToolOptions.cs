using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>Bounds for the built-in <c>delay</c>/wait tool.</summary>
public sealed class DelayToolOptions
{
    /// <summary>Maximum delay, in seconds, an agent may request from the delay tool.</summary>
    [Display(
        Name = "Max delay (seconds)",
        Description = "Maximum delay, in seconds, an agent may request from the delay/wait tool. Longer requests are clamped to this ceiling.",
        GroupName = "Delay tool",
        Order = 0)]
    [DefaultValue(1800)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "delay-tool", Order = 0)]
    public int MaxDelaySeconds { get; set; } = 1800; // 30 minutes

    /// <summary>Default delay, in seconds, used when a delay request omits a duration.</summary>
    [Display(
        Name = "Default delay (seconds)",
        Description = "Default delay, in seconds, applied when a delay/wait request does not specify a duration.",
        GroupName = "Delay tool",
        Order = 1)]
    [DefaultValue(60)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "delay-tool", Order = 1)]
    public int DefaultDelaySeconds { get; set; } = 60;
}
