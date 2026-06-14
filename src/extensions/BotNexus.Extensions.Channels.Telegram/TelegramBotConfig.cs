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
    /// Gets or sets the secret token used to authenticate inbound webhook requests.
    /// </summary>
    /// <remarks>
    /// When webhook mode is active, this value is registered with Telegram via
    /// <c>setWebhook</c>'s <c>secret_token</c> parameter; Telegram then echoes it back in the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header on every update POST. The receiver rejects
    /// any request whose header does not match (constant-time comparison), which is the only thing
    /// preventing an attacker who learns the public webhook URL from injecting forged updates.
    /// When left null/empty the adapter generates a cryptographically strong token at startup, so
    /// webhook mode is never silently unauthenticated. Allowed characters per the Bot API:
    /// <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>_</c> and <c>-</c>, length 1–256.
    /// </remarks>
    public string? WebhookSecretToken { get; set; }

    /// <summary>
    /// Gets the allow-list of Telegram chat IDs that can interact with this bot.
    /// An empty list allows all chats.
    /// </summary>
    public ICollection<long> AllowedChatIds { get; } = [];

    /// <summary>
    /// Gets the allow-list of Telegram user IDs that can send messages to this bot.
    /// Filters message.from.id. An empty list allows any user (subject to AllowedChatIds).
    /// For a personal bot, set this to your Telegram user ID.
    /// </summary>
    public ICollection<long> AllowedUserIds { get; } = [];

    /// <summary>
    /// When true, edited messages are processed the same as new messages.
    /// Defaults to false \u2014 edited messages are ignored to prevent replay.
    /// </summary>
    public bool ProcessEditedMessages { get; set; } = false;

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
