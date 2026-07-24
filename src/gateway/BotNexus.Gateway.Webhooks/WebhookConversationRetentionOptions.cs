namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Source-specific retention policy for automation-owned webhook conversations (issue #2125).
/// <para>
/// Webhook conversations are provenance-tagged (see <see cref="WebhookConversationProvenance"/>)
/// and age out on a faster, dedicated schedule than ordinary human conversations. This policy is
/// independent of both the world-level conversation auto-archive gate and webhook <em>run</em>
/// retention; the three settings govern different data with their own thresholds.
/// </para>
/// </summary>
public sealed class WebhookConversationRetentionOptions
{
    /// <summary>
    /// Whether webhook-specific conversation retention is enabled. Opt-in; defaults to <c>false</c>
    /// so existing deployments see no behaviour change until the policy is explicitly turned on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Days of inactivity after which the canonical conversation of a <em>deleted or disabled</em>
    /// registration becomes eligible for archival. Zero or negative disables this rule.
    /// Defaults to 7 days - shorter than ordinary human conversations, longer than orphan cleanup.
    /// </summary>
    public int DisabledRegistrationInactivityDays { get; set; } = 7;

    /// <summary>
    /// Days of inactivity after which a webhook conversation whose registration no longer exists
    /// <em>and</em> which is not the registration's canonical (pinned) conversation - i.e. a
    /// race/orphan conversation - becomes eligible for archival. Zero or negative disables this rule.
    /// Defaults to 1 day so abandoned orphans age out aggressively.
    /// </summary>
    public int OrphanInactivityDays { get; set; } = 1;

    /// <summary>
    /// How often the retention sweep runs. Defaults to once per hour.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
