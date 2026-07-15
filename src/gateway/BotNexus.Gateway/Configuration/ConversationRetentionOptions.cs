using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// World-level (gateway-default) retention policy for conversations.
/// Per-agent overrides are configured in <see cref="BotNexus.Gateway.Abstractions.Models.AgentConversationRetentionConfig"/>.
/// </summary>
public sealed class ConversationRetentionOptions
{
    /// <summary>
    /// Whether auto-archive is enabled at the world level. Defaults to <c>false</c> (opt-in).
    /// </summary>
    [Display(
        Name = "Enable auto-archive",
        Description = "Whether conversation auto-archive is enabled at the world level. Opt-in; defaults to off.",
        GroupName = "Conversation retention",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "conversation-retention", Order = 0)]
    public bool AutoArchiveEnabled { get; set; }

    /// <summary>
    /// Number of days of inactivity after which a conversation is auto-archived.
    /// A value of zero or negative is treated as disabled. Default is 30 days.
    /// </summary>
    [Display(
        Name = "Auto-archive after (days)",
        Description = "Days of inactivity after which a conversation is auto-archived. Zero or negative disables archiving.",
        GroupName = "Conversation retention",
        Order = 1)]
    [DefaultValue(30)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "conversation-retention", Order = 1)]
    public int AutoArchiveAfterDays { get; set; } = 30;

    /// <summary>
    /// How often the retention service scans for conversations to archive.
    /// Defaults to once per hour.
    /// </summary>
    [Display(
        Name = "Check interval",
        Description = "How often the retention service scans for conversations to archive.",
        GroupName = "Conversation retention",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "conversation-retention", Order = 2)]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
