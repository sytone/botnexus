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
    public bool AutoArchiveEnabled { get; set; }

    /// <summary>
    /// Number of days of inactivity after which a conversation is auto-archived.
    /// A value of zero or negative is treated as disabled. Default is 30 days.
    /// </summary>
    public int AutoArchiveAfterDays { get; set; } = 30;

    /// <summary>
    /// How often the retention service scans for conversations to archive.
    /// Defaults to once per hour.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
