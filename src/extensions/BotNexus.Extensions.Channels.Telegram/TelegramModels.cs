using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Content of a Telegram Rich Message (Bot API 10.1+) to send via <c>sendRichMessage</c>,
/// <c>sendRichMessageDraft</c>, or <c>editMessageText</c>.
/// </summary>
/// <remarks>
/// Per the Telegram spec, exactly one of <see cref="Markdown"/> or <see cref="Html"/> must be set.
/// BotNexus only uses the <see cref="Markdown"/> field: LLM output is GitHub-Flavored-Markdown-ish,
/// which Rich Markdown accepts nearly as-is, so no MarkdownV2-style escaping is required.
/// Reference: https://core.telegram.org/bots/api#inputrichmessage
/// </remarks>
public sealed record InputRichMessage
{
    /// <summary>
    /// Rich message content described using Rich Markdown. Mutually exclusive with <see cref="Html"/>.
    /// </summary>
    [JsonPropertyName("markdown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Markdown { get; init; }

    /// <summary>
    /// Rich message content described using Rich HTML. Mutually exclusive with <see cref="Markdown"/>.
    /// Unused by BotNexus today but modelled for completeness.
    /// </summary>
    [JsonPropertyName("html")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Html { get; init; }

    /// <summary>Pass true to render the message right-to-left.</summary>
    [JsonPropertyName("is_rtl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsRtl { get; init; }

    /// <summary>
    /// Pass true to skip Telegram's automatic detection of entities (URLs, mentions, hashtags,
    /// phone numbers, etc.) in the text.
    /// </summary>
    [JsonPropertyName("skip_entity_detection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SkipEntityDetection { get; init; }
}

public sealed record TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("parameters")]
    public TelegramResponseParameters? Parameters { get; init; }
}

public sealed record TelegramResponseParameters
{
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; init; }
}

public sealed record TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }

    [JsonPropertyName("edited_message")]
    public TelegramMessage? EditedMessage { get; init; }

    [JsonPropertyName("channel_post")]
    public TelegramMessage? ChannelPost { get; init; }

    /// <summary>
    /// Inline-keyboard button tap (#2323). Null for every other update kind. Without this member the
    /// adapter could send buttons that were physically untappable: the tap arrives as its own update
    /// type, not as a message, so a keyboard rendered against an update model that cannot express a
    /// callback query is write-only.
    /// </summary>
    [JsonPropertyName("callback_query")]
    public TelegramCallbackQuery? CallbackQuery { get; init; }
}

/// <summary>
/// An incoming callback query from an inline-keyboard button tap.
/// Reference: https://core.telegram.org/bots/api#callbackquery
/// </summary>
/// <remarks>
/// A callback query is an inbound event attributable to a specific user, so it carries the same
/// authorization weight as a text message and is run through the identical chat/user allow-list
/// checks. The message member is the message the keyboard was attached to, which is what lets the
/// adapter edit the prompt in place once it resolves.
/// </remarks>
public sealed record TelegramCallbackQuery
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }

    /// <summary>Message carrying the inline keyboard that was tapped. Null for inline-mode messages.</summary>
    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }

    /// <summary>
    /// Opaque data attached to the tapped button, capped by Telegram at 64 bytes.
    /// See TelegramPromptKeyboard for the compact token format used to stay inside that.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

/// <summary>
/// An inline keyboard attached to a message via <c>reply_markup</c>.
/// Reference: https://core.telegram.org/bots/api#inlinekeyboardmarkup
/// </summary>
public sealed record InlineKeyboardMarkup
{
    /// <summary>Rows of buttons; each inner list is one displayed row.</summary>
    [JsonPropertyName("inline_keyboard")]
    public required IReadOnlyList<IReadOnlyList<InlineKeyboardButton>> InlineKeyboard { get; init; }
}

/// <summary>
/// One button on an <see cref="InlineKeyboardMarkup"/>.
/// Reference: https://core.telegram.org/bots/api#inlinekeyboardbutton
/// </summary>
public sealed record InlineKeyboardButton
{
    /// <summary>Label shown on the button. Rendered literally; no parse_mode applies to markup.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    /// Data delivered back in the callback query when tapped.
    /// Telegram rejects the whole send if this exceeds 64 bytes.
    /// </summary>
    [JsonPropertyName("callback_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackData { get; init; }
}

public sealed record TelegramMessage
{
    [JsonPropertyName("message_id")]
    public int MessageId { get; init; }

    /// <summary>Topic/thread id for forum-group messages. Null for regular chats and DMs.</summary>
    [JsonPropertyName("message_thread_id")]
    public int? MessageThreadId { get; init; }

    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; init; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Caption accompanying a photo or other media. Up to 1024 characters.
    /// Null when the message has no caption.
    /// </summary>
    [JsonPropertyName("caption")]
    public string? Caption { get; init; }

    /// <summary>
    /// Array of photo sizes for a photo message. Telegram provides multiple resolutions;
    /// the last element is always the largest. Null for non-photo messages.
    /// </summary>
    [JsonPropertyName("photo")]
    public TelegramPhotoSize[]? Photo { get; init; }
}

public sealed record TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

public sealed record TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

/// <summary>
/// Represents one resolution variant of a Telegram photo.
/// Telegram returns an array of these ordered by resolution (last = largest).
/// </summary>
public sealed record TelegramPhotoSize
{
    [JsonPropertyName("file_id")]
    public required string FileId { get; init; }

    [JsonPropertyName("file_unique_id")]
    public required string FileUniqueId { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>File size in bytes. May be absent for very small photos.</summary>
    [JsonPropertyName("file_size")]
    public int? FileSize { get; init; }
}

/// <summary>
/// Metadata returned by the Telegram getFile API endpoint.
/// </summary>
public sealed record TelegramFile
{
    [JsonPropertyName("file_id")]
    public required string FileId { get; init; }

    [JsonPropertyName("file_unique_id")]
    public required string FileUniqueId { get; init; }

    /// <summary>
    /// Relative path used to download the file via
    /// <c>https://api.telegram.org/file/bot{token}/{FilePath}</c>.
    /// </summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("file_size")]
    public int? FileSize { get; init; }
}
