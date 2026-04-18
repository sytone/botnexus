using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Telegram;

public sealed class TelegramBotApiClient(
    HttpClient httpClient,
    IOptions<TelegramOptions> optionsAccessor,
    ILogger<TelegramBotApiClient> logger)
{
    private const int MaxRateLimitRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly TelegramOptions _options = optionsAccessor.Value;
    private readonly ILogger<TelegramBotApiClient> _logger = logger;

    public Task<TelegramMessage> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
        => PostForResultAsync<TelegramMessage>(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text,
                parse_mode = "MarkdownV2"
            },
            cancellationToken);

    public Task<TelegramMessage> EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken = default)
        => PostForResultAsync<TelegramMessage>(
            "editMessageText",
            new
            {
                chat_id = chatId,
                message_id = messageId,
                text,
                parse_mode = "MarkdownV2"
            },
            cancellationToken);

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
                timeout
            },
            cancellationToken);

        return updates;
    }

    public Task SetWebhookAsync(string url, CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "setWebhook",
            new
            {
                url
            },
            cancellationToken);

    public Task DeleteWebhookAsync(CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "deleteWebhook",
            new
            {
                drop_pending_updates = false
            },
            cancellationToken);

    private async Task<T> PostForResultAsync<T>(string methodName, object payload, CancellationToken cancellationToken)
    {
        var token = _options.BotToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Telegram BotToken is required.");

        var endpoint = $"https://api.telegram.org/bot{token}/{methodName}";
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
