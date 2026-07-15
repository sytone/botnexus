using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

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
    [Display(
        Name = "Bot token",
        Description = "Bot token for a single-bot deployment. Ignored when Bots is non-empty. Sensitive: stored and shown masked.",
        GroupName = "Telegram",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "telegram", Order = 0, Secret = true)]
    public string? BotToken { get; set; }

    /// <summary>
    /// Agent ID for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// </summary>
    [Display(
        Name = "Agent ID",
        Description = "Agent ID for a single-bot deployment. Ignored when Bots is non-empty.",
        GroupName = "Telegram",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "telegram", Order = 1)]
    public string? AgentId { get; set; }

    /// <summary>
    /// Webhook URL for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
    /// </summary>
    [Display(
        Name = "Webhook URL",
        Description = "Webhook URL for a single-bot deployment. Ignored when Bots is non-empty.",
        GroupName = "Telegram",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "telegram", Order = 2)]
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Secret token authenticating inbound webhook requests for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty. When null/empty the adapter generates a
    /// cryptographically strong token at startup so webhook mode is never unauthenticated.
    /// </summary>
    [Display(
        Name = "Webhook secret token",
        Description = "Secret token authenticating inbound webhook requests for a single-bot deployment. Sensitive: stored and shown masked.",
        GroupName = "Telegram",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "telegram", Order = 3, Secret = true)]
    public string? WebhookSecretToken { get; set; }

    /// <summary>
    /// Allow-list of chat IDs for a single-bot deployment.
    /// Ignored when <see cref="Bots"/> is non-empty.
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
    /// Defaults to false 2014 edited messages are ignored to prevent replay.
    /// </summary>
    [Display(
        Name = "Process edited messages",
        Description = "When on, edited messages are processed the same as new messages. Off by default to prevent replay.",
        GroupName = "Telegram",
        Order = 4)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram", Order = 4)]
    public bool ProcessEditedMessages { get; set; } = false;

    /// <summary>
    /// Long polling timeout in seconds. Clamped to a minimum of 1.
    /// </summary>
    [Display(
        Name = "Polling timeout (seconds)",
        Description = "Long polling timeout in seconds. Clamped to a minimum of 1.",
        GroupName = "Telegram",
        Order = 5)]
    [DefaultValue(30)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram", Order = 5)]
    public int PollingTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Flush interval in milliseconds for buffered streaming deltas.
    /// </summary>
    [Display(
        Name = "Streaming buffer (ms)",
        Description = "Flush interval, in milliseconds, for buffered streaming deltas.",
        GroupName = "Telegram",
        Order = 6)]
    [DefaultValue(500)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram", Order = 6)]
    public int StreamingBufferMs { get; set; } = 500;

    /// <summary>
    /// Maximum Telegram message length before payload splitting.
    /// Conservative default (4000) stays safely below the 4096-byte Telegram limit.
    /// </summary>
    [Display(
        Name = "Max message length",
        Description = "Maximum Telegram message length before payload splitting. Default 4000 stays below the 4096 limit.",
        GroupName = "Telegram",
        Order = 7)]
    [DefaultValue(4000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram", Order = 7)]
    public int MaxMessageLength { get; set; } = 4000;

    /// <summary>
    /// When true (default), outbound messages are sent as Telegram Rich Messages (Bot API 10.1+)
    /// using Rich Markdown, with automatic fallback to MarkdownV2 then plain text on rejection.
    /// Set to false to force the legacy MarkdownV2 path. Mirrored into the synthesised default bot.
    /// </summary>
    [Display(
        Name = "Rich messages",
        Description = "When on (default), outbound messages use Telegram Rich Messages with automatic fallback. Mirrored into the synthesised default bot.",
        GroupName = "Telegram",
        Order = 8)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram", Order = 8)]
    public bool RichMessages { get; set; } = true;

    /// <summary>
    /// When true (default), tool executions are surfaced to the chat as compact standalone status
    /// messages as the agent runs, so the user can see which tools are being called. Set to false to
    /// suppress them. Mirrored into the synthesised default bot.
    /// </summary>
    [Display(
        Name = "Show tool activity",
        Description = "When on (default), tool executions are surfaced to the chat as compact standalone status messages. Mirrored into the synthesised default bot.",
        GroupName = "Telegram",
        Order = 9)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "telegram", Order = 9)]
    public bool ShowToolActivity { get; set; } = true;

    /// <summary>
    /// Maximum Rich Message length before payload splitting. Rich Messages allow up to 32768
    /// characters; the default (32000) stays safely below that. Only used when
    /// <see cref="RichMessages"/> is enabled.
    /// </summary>
    [Display(
        Name = "Max rich message length",
        Description = "Maximum Rich Message length before payload splitting. Default 32000 stays below the 32768 limit. Used only when Rich Messages is enabled.",
        GroupName = "Telegram",
        Order = 10)]
    [DefaultValue(32000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram", Order = 10)]
    public int MaxRichMessageLength { get; set; } = 32000;

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
    [Display(
        Name = "Error cooldown (ms)",
        Description = "Minimum time, in milliseconds, between error replies to the same chat. Prevents error spam during outages.",
        GroupName = "Telegram",
        Order = 11)]
    [DefaultValue(60_000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "telegram", Order = 11)]
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

        // Single-bot fallback - synthesise a "default" bot entry
        var single = new TelegramBotConfig
        {
            BotToken = BotToken,
            AgentId = AgentId,
            WebhookUrl = WebhookUrl,
            WebhookSecretToken = WebhookSecretToken,
            PollingTimeoutSeconds = PollingTimeoutSeconds,
            StreamingBufferMs = StreamingBufferMs,
            MaxMessageLength = MaxMessageLength,
            RichMessages = RichMessages,
            ShowToolActivity = ShowToolActivity,
            MaxRichMessageLength = MaxRichMessageLength,
            ErrorCooldownMs = ErrorCooldownMs,
            ProcessEditedMessages = ProcessEditedMessages
        };
        foreach (var id in AllowedChatIds)
            single.AllowedChatIds.Add(id);
        foreach (var id in AllowedUserIds)
            single.AllowedUserIds.Add(id);

        return new Dictionary<string, TelegramBotConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = single
        };
    }
}
