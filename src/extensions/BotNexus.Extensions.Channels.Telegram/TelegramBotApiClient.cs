using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Thin HTTP client for the Telegram Bot API.
/// One instance per bot token. Uses HTML parse_mode for outbound messages.
/// </summary>
public sealed class TelegramBotApiClient(
    HttpClient httpClient,
    string botToken,
    ILogger logger)
{
    private const int MaxRateLimitRetries = 3;

    // Telegram only sends relevant update types — excludes noisy ones like inline_query.
    private static readonly string[] AllowedUpdateTypes =
    [
        "message",
        "edited_message",
        "channel_post",
        "edited_channel_post",
        "message_reaction"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly string _botToken = botToken;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Sends a text message to a chat, optionally into a forum topic thread.
    /// HTML parse_mode is used. If Telegram rejects the HTML (400 Bad Request),
    /// the message is retried as plain text without any parse_mode.
    /// </summary>
    /// <remarks>
    /// General topic (threadId == 1) is a special case: Telegram rejects
    /// <c>sendMessage</c> when <c>message_thread_id=1</c> is sent explicitly.
    /// The field is omitted for threadId == 1.
    /// </remarks>
    public Task<TelegramMessage> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
        => SendMessageAsync(chatId, text, messageThreadId: null, cancellationToken);

    /// <summary>
    /// Sends a text message, optionally into a forum topic thread.
    /// </summary>
    public async Task<TelegramMessage> SendMessageAsync(long chatId, string text, int? messageThreadId, CancellationToken cancellationToken = default)
    {
        // General topic (threadId == 1) must NOT include message_thread_id — Telegram rejects it.
        // For other thread IDs, include message_thread_id as normal.
        var isGeneralTopic = messageThreadId is 1;
        var effectiveThreadId = isGeneralTopic ? null : messageThreadId;

        // Try HTML first; fall back to plain text if Telegram rejects it (400).
        try
        {
            var htmlPayload = effectiveThreadId.HasValue
                ? (object)new { chat_id = chatId, text, parse_mode = "HTML", message_thread_id = effectiveThreadId.Value }
                : new { chat_id = chatId, text, parse_mode = "HTML" };

            return await PostForResultAsync<TelegramMessage>("sendMessage", htmlPayload, cancellationToken, allowHtmlFallback: true);
        }
        catch (TelegramHtmlParseException)
        {
            // HTML was rejected — retry as plain text
            _logger.LogWarning("Telegram rejected HTML for sendMessage to chat {ChatId}; retrying as plain text", chatId);
            var plainPayload = effectiveThreadId.HasValue
                ? (object)new { chat_id = chatId, text, message_thread_id = effectiveThreadId.Value }
                : new { chat_id = chatId, text };

            return await PostForResultAsync<TelegramMessage>("sendMessage", plainPayload, cancellationToken, allowHtmlFallback: false);
        }
    }

    /// <summary>
    /// Edits an existing message's text. Uses HTML parse_mode.
    /// </summary>
    public Task<TelegramMessage> EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken = default)
        => PostForResultAsync<TelegramMessage>(
            "editMessageText",
            new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML" },
            cancellationToken,
            allowHtmlFallback: false);

    /// <summary>
    /// Long-polls for new updates from the Telegram Bot API.
    /// Specifies <c>allowed_updates</c> so Telegram only sends relevant update types.
    /// </summary>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset,
        int timeout,
        CancellationToken cancellationToken = default)
    {
        var updates = await PostForResultAsync<List<TelegramUpdate>>(
            "getUpdates",
            new
            {
                offset,
                timeout,
                allowed_updates = AllowedUpdateTypes
            },
            cancellationToken,
            allowHtmlFallback: false);

        return updates;
    }

    /// <summary>
    /// Registers a webhook URL with Telegram.
    /// </summary>
    public Task SetWebhookAsync(string url, CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "setWebhook",
            new { url },
            cancellationToken,
            allowHtmlFallback: false);

    /// <summary>
    /// Removes any previously registered webhook, reverting to long polling mode.
    /// </summary>
    public Task DeleteWebhookAsync(CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "deleteWebhook",
            new { drop_pending_updates = false },
            cancellationToken,
            allowHtmlFallback: false);

    private async Task<T> PostForResultAsync<T>(string methodName, object payload, CancellationToken cancellationToken, bool allowHtmlFallback)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            throw new InvalidOperationException("Telegram BotToken is required.");

        var endpoint = $"https://api.telegram.org/bot{_botToken}/{methodName}";
        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaxRateLimitRetries)
                    throw new HttpRequestException($"Telegram API rate limit exceeded for {methodName}: {body}");

                var retryAfter = ExtractRetryAfterSeconds(body) ?? 1;
                _logger.LogWarning(
                    "Telegram API rate limited on {MethodName}; retrying in {RetryAfterSeconds}s (attempt {Attempt}/{MaxAttempts})",
                    methodName,
                    retryAfter,
                    attempt + 1,
                    MaxRateLimitRetries + 1);
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
                continue;
            }

            // 400 on sendMessage with HTML parse_mode means malformed HTML — signal fallback.
            if (response.StatusCode == HttpStatusCode.BadRequest && allowHtmlFallback)
                throw new TelegramHtmlParseException($"Telegram rejected HTML for {methodName}: {body}");

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Telegram API call '{methodName}' failed ({(int)response.StatusCode}): {body}");

            var apiResponse = JsonSerializer.Deserialize<TelegramApiResponse<T>>(body, JsonOptions)
                ?? throw new InvalidOperationException($"Telegram API '{methodName}' returned an invalid response payload.");

            if (apiResponse.Ok && apiResponse.Result is not null)
                return apiResponse.Result;

            if (apiResponse.ErrorCode == 429)
            {
                if (attempt == MaxRateLimitRetries)
                    throw new HttpRequestException($"Telegram API rate limit exceeded for {methodName}: {apiResponse.Description}");

                var retryAfter = apiResponse.Parameters?.RetryAfter ?? 1;
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), cancellationToken);
                continue;
            }

            throw new InvalidOperationException(
                $"Telegram API call '{methodName}' failed: {apiResponse.Description ?? "Unknown Telegram error."}");
        }

        throw new InvalidOperationException($"Telegram API call '{methodName}' exhausted retries without a result.");
    }

    private static int? ExtractRetryAfterSeconds(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            var response = JsonSerializer.Deserialize<TelegramApiResponse<JsonElement>>(responseBody, JsonOptions);
            return response?.Parameters?.RetryAfter;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Thrown internally when Telegram returns a 400 for an HTML-formatted message,
/// signalling that <see cref="TelegramBotApiClient"/> should retry as plain text.
/// </summary>
internal sealed class TelegramHtmlParseException(string message) : Exception(message);
