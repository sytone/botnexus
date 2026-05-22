namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configuration for the webhook ingress feature (POST /api/webhooks/message).
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>Whether webhook ingress is enabled. Defaults to false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Pre-shared API keys for webhook authentication.</summary>
    public List<WebhookKeyConfig> Keys { get; set; } = [];
}

/// <summary>A single webhook key configuration entry.</summary>
public sealed class WebhookKeyConfig
{
    /// <summary>Unique ID for this key (for logging and management).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The secret key value. Must be provided by caller in X-BotNexus-Webhook-Key header.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Optional list of agent IDs this key is authorized to trigger.
    /// When null or empty, the key is authorized for all agents.
    /// </summary>
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>
/// Serialization DTO for webhook config within PlatformConfig / GatewaySettingsConfig.
/// Mirrors <see cref="WebhookOptions"/>.
/// </summary>
public sealed class WebhookConfig
{
    /// <summary>Whether webhook ingress is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Pre-shared API keys for webhook authentication.</summary>
    public List<WebhookKeyConfig> Keys { get; set; } = [];
}
