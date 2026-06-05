namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Per-agent override for conversation auto-archive retention policy.
/// When set on an <see cref="AgentDescriptor"/>, these values take precedence over the
/// world-level <c>gateway.conversations.autoArchive*</c> settings.
/// </summary>
public sealed record AgentConversationRetentionConfig
{
    /// <summary>
    /// Whether auto-archive is enabled for this agent's conversations.
    /// Set to <c>false</c> to disable auto-archive for this agent regardless of the world default.
    /// </summary>
    public bool AutoArchiveEnabled { get; init; } = true;

    /// <summary>
    /// Number of days of inactivity after which this agent's conversations are auto-archived.
    /// Overrides the world-level default when set. A value of zero or negative disables auto-archive for this agent.
    /// </summary>
    public int? AutoArchiveAfterDays { get; init; }
}
