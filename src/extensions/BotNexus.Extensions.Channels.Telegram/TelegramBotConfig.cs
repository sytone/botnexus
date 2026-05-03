namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Per-bot configuration for a single Telegram bot token / agent binding.
/// </summary>
public sealed class TelegramBotConfig
{
    /// <summary>
    /// Gets or sets the Telegram bot token used to authenticate Bot API calls.
    /// </summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Gets or sets the BotNexus agent ID this bot routes to.
    /// Every message received by this bot will target this agent.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Gets or sets the webhook URL when running in webhook mode.
    /// When null or empty, the adapter uses long polling.
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Gets the allow-list of Telegram chat IDs that can interact with this bot.
    /// An empty list allows all chats.
    /// </summary>
    public ICollection<long> AllowedChatIds { get; } = [];

    /// <summary>
    /// Gets or sets the long polling timeout in seconds.
    /// </summary>
    public int PollingTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the flush interval in milliseconds for buffered streaming deltas.
    /// </summary>
    public int StreamingBufferMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum Telegram message length before payload splitting.
    /// Conservative default (4000) stays safely below the 4096-byte Telegram limit.
    /// </summary>
    public int MaxMessageLength { get; set; } = 4000;

    /// <summary>
    /// Gets or sets the minimum time in milliseconds between error replies to this bot's chats.
    /// Prevents error spam to users during outages or repeated failures.
    /// </summary>
    public int ErrorCooldownMs { get; set; } = 60_000;
}
