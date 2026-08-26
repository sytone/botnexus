using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents soul agent config.
/// </summary>
public sealed class SoulAgentConfig
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Whether soul journalling is enabled for this agent, giving it a daily note and reflection cycle.",
        GroupName = "Soul",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "soul", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the timezone.
    /// </summary>
    [Display(
        Name = "Timezone",
        Description = "IANA timezone used to decide when the agent's day starts and ends (for example Europe/London).",
        GroupName = "Soul",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "soul", Order = 1)]
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Gets or sets the day boundary.
    /// </summary>
    [Display(
        Name = "Day boundary",
        Description = "Local time of day at which one journal day rolls over to the next, in HH:mm format.",
        GroupName = "Soul",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "soul", Order = 2)]
    public string DayBoundary { get; set; } = "00:00";

    /// <summary>
    /// Gets or sets the reflection on seal.
    /// </summary>
    [Display(
        Name = "Reflection on seal",
        Description = "Whether the agent writes a reflection when a day's journal is sealed.",
        GroupName = "Soul",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "soul", Order = 3)]
    public bool ReflectionOnSeal { get; set; }

    /// <summary>
    /// Gets or sets the reflection prompt.
    /// </summary>
    [Display(
        Name = "Reflection prompt",
        Description = "Custom prompt used to generate the end-of-day reflection. When unset, the built-in prompt is used.",
        GroupName = "Soul",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "soul", Order = 4)]
    public string? ReflectionPrompt { get; set; }
}
