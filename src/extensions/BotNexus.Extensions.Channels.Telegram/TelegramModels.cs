using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.Telegram;

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

    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; init; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
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
