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
