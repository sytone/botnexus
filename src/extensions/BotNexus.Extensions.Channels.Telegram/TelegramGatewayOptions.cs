namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Top-level gateway configuration for the Telegram channel extension.
/// Supports both single-bot (convenience) and multi-bot configurations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multi-bot (recommended):</b> populate <see cref="Bots"/> with one entry per
/// Telegram bot token. Each entry maps a named bot to a BotNexus agent.
/// </para>
/// <para>
/// <b>Single-bot (convenience):</b> set <see cref="BotToken"/> and optionally
/// <see cref="AgentId"/> at the top level. When <see cref="Bots"/> is empty and
/// <see cref="BotToken"/> is set, the adapter synthesises a single bot entry named
/// <c>default</c> from the top-level fields.
/// </para>
/// </remarks>
public sealed class TelegramGatewayOptions
{
    // ── Single-bot convenience fields ─────────────────────────────────────────

    /// <summary>
    /// Bot token for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// </summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Agent ID for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Webhook URL for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Allow-list of chat IDs for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// An empty list allows all chats.
    /// </summary>
    public ICollection<long> AllowedChatIds { get; } = [];

    /// <summary>
    /// Long polling timeout in seconds. Clamped to a minimum of 1.
    /// </summary>
    public int PollingTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Flush interval in milliseconds for buffered streaming deltas.
    /// </summary>
    public int StreamingBufferMs { get; set; } = 500;

    /// <summary>
    /// Maximum Telegram message length before payload splitting.
    /// Conservative default (4000) stays safely below the 4096-byte Telegram limit.
    /// </summary>
    public int MaxMessageLength { get; set; } = 4000;

    // ── Multi-bot configuration ───────────────────────────────────────────────

    /// <summary>
    /// Named bot configurations. Each key is a logical bot name used for
    /// logging and HTTP client naming; each value holds the token and agent
    /// binding for that bot.
    /// When this dictionary is non-empty it takes precedence over the
    /// single-bot convenience fields above.
    /// </summary>
    public Dictionary<string, TelegramBotConfig> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Error reply controls ──────────────────────────────────────────────────

    /// <summary>
    /// Minimum time in milliseconds between error replies to the same chat.
    /// Prevents error spam to users during outages or repeated failures.
    /// </summary>
    public int ErrorCooldownMs { get; set; } = 60_000;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the effective list of bot configurations to activate.
    /// Uses <see cref="Bots"/> when populated; otherwise synthesises a single
    /// <c>default</c> entry from the single-bot convenience fields.
    /// </summary>
    internal IReadOnlyDictionary<string, TelegramBotConfig> ResolveActiveBots()
    {
        if (Bots.Count > 0)
            return Bots;

        // Single-bot fallback — synthesise a "default" bot entry
        var single = new TelegramBotConfig
        {
            BotToken = BotToken,
            AgentId = AgentId,
            WebhookUrl = WebhookUrl,
            PollingTimeoutSeconds = PollingTimeoutSeconds,
            StreamingBufferMs = StreamingBufferMs,
            MaxMessageLength = MaxMessageLength,
            ErrorCooldownMs = ErrorCooldownMs
        };
        foreach (var id in AllowedChatIds)
            single.AllowedChatIds.Add(id);

        return new Dictionary<string, TelegramBotConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = single
        };
    }
}
