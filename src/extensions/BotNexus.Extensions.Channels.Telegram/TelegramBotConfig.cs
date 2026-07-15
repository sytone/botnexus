using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Per-bot configuration for a single Telegram bot token / agent binding.
/// </summary>
public sealed class TelegramBotConfig
{
    /// <summary>
    /// Gets or sets the Telegram bot token used to authenticate Bot API calls.
    /// </summary>
    [Display(
        Name = "Bot token",
        Description = "Telegram bot token used to authenticate Bot API calls. Sensitive: stored and shown masked.",
        GroupName = "Telegram bot",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "telegram-bot", Order = 0, Secret = true)]
    public string? BotToken { get; set; }

    /// <summary>
    /// Gets or sets the BotNexus agent ID this bot routes to.
    /// Every message received by this bot will target this agent.
    /// </summary>
    [Display(
        Name = "Agent ID",
        Description = "The BotNexus agent ID this bot routes to. Every message received by this bot targets this agent.",
        GroupName = "Telegram bot",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "telegram-bot", Order = 1)]
    public string? AgentId { get; set; }

    /// <summary>
    /// Gets or sets the webhook URL when running in webhook mode.
    /// When null or empty, the adapter uses long polling.
    /// </summary>
    [Display(
        Name = "Webhook URL",
        Description = "Webhook URL when running in webhook mode. When empty, the adapter uses long polling.",
        GroupName = "Telegram bot",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "telegram-bot", Order = 2)]
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
    /// <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>_</c> and <c>-</c>, length 1-256.
    /// </remarks>
    [Display(
        Name = "Webhook secret token",
        Description = "Secret token used to authenticate inbound webhook requests. Sensitive: stored and shown masked. When empty, a strong token is generated at startup.",
        GroupName = "Telegram bot",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "telegram-bot", Order = 3, Secret = true)]
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
    [Display(
        Name = "Process edited messages",
        Description = "When on, edited messages are processed the same as new messages. Off by default to prevent replay.",
        GroupName = "Telegram bot",
        Order = 4)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram-bot", Order = 4)]
    public bool ProcessEditedMessages { get; set; } = false;

    /// <summary>
    /// Gets or sets the long polling timeout in seconds.
    /// </summary>
    [Display(
        Name = "Polling timeout (seconds)",
        Description = "Long polling timeout in seconds.",
        GroupName = "Telegram bot",
        Order = 5)]
    [DefaultValue(30)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram-bot", Order = 5)]
    public int PollingTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the flush interval in milliseconds for buffered streaming deltas.
    /// </summary>
    [Display(
        Name = "Streaming buffer (ms)",
        Description = "Flush interval, in milliseconds, for buffered streaming deltas.",
        GroupName = "Telegram bot",
        Order = 6)]
    [DefaultValue(500)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram-bot", Order = 6)]
    public int StreamingBufferMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum Telegram message length before payload splitting.
    /// Conservative default (4000) stays safely below the 4096-byte Telegram limit.
    /// </summary>
    [Display(
        Name = "Max message length",
        Description = "Maximum Telegram message length before payload splitting. Default 4000 stays below the 4096 limit.",
        GroupName = "Telegram bot",
        Order = 7)]
    [DefaultValue(4000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram-bot", Order = 7)]
    public int MaxMessageLength { get; set; } = 4000;

    /// <summary>
    /// When true (default), outbound messages are sent as Telegram Rich Messages (Bot API 10.1+)
    /// using Rich Markdown, which natively renders tables, headings, lists, blockquotes, and more.
    /// If Telegram rejects a rich send (e.g. an older client), the adapter automatically falls back
    /// to the legacy MarkdownV2 path, then plain text - so content is never dropped. Set to false to
    /// force the legacy MarkdownV2 path for all messages.
    /// </summary>
    [Display(
        Name = "Rich messages",
        Description = "When on (default), outbound messages use Telegram Rich Messages with automatic fallback to MarkdownV2 then plain text.",
        GroupName = "Telegram bot",
        Order = 8)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram-bot", Order = 8)]
    public bool RichMessages { get; set; } = true;

    /// <summary>
    /// When true (default), tool executions are surfaced to the chat as compact standalone status
    /// messages (e.g. "\U0001F4C4 read done") as the agent runs, so the user can see which tools are
    /// being called. Each tool start/end is delivered as its own message rather than being mixed into
    /// the assistant's streamed reply -- this is deliberate: the streaming content buffer is reset at
    /// every message boundary, so an inlined tool note is wiped by the follow-up assistant message
    /// before it is ever sent. Set to false to suppress tool-activity messages and show only the
    /// agent's replies. The glyph for each tool comes from the cross-channel
    /// <see cref="BotNexus.Domain.Gateway.Models.ToolGlyphs"/> map so the icon is identical on every
    /// channel.
    /// </summary>
    [Display(
        Name = "Show tool activity",
        Description = "When on (default), tool executions are surfaced to the chat as compact standalone status messages as the agent runs.",
        GroupName = "Telegram bot",
        Order = 9)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram-bot", Order = 9)]
    public bool ShowToolActivity { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum Rich Message length before payload splitting. Rich Messages allow up
    /// to 32768 characters (vs 4096 for plain), so the default (32000) stays safely below that limit.
    /// Only used when <see cref="RichMessages"/> is enabled; the legacy fallback path uses
    /// <see cref="MaxMessageLength"/>.
    /// </summary>
    [Display(
        Name = "Max rich message length",
        Description = "Maximum Rich Message length before payload splitting. Default 32000 stays below the 32768 limit. Used only when Rich Messages is enabled.",
        GroupName = "Telegram bot",
        Order = 10)]
    [DefaultValue(32000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram-bot", Order = 10)]
    public int MaxRichMessageLength { get; set; } = 32000;

    /// <summary>
    /// Gets or sets the minimum time in milliseconds between error replies to this bot's chats.
    /// Prevents error spam to users during outages or repeated failures.
    /// </summary>
    [Display(
        Name = "Error cooldown (ms)",
        Description = "Minimum time, in milliseconds, between error replies to this bot's chats. Prevents error spam during outages.",
        GroupName = "Telegram bot",
        Order = 11)]
    [DefaultValue(60_000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram-bot", Order = 11)]
    public int ErrorCooldownMs { get; set; } = 60_000;
}
