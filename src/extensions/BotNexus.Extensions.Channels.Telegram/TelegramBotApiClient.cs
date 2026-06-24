using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Thin HTTP client for the Telegram Bot API.
/// One instance per bot token. Uses MarkdownV2 parse_mode for outbound messages.
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
    /// MarkdownV2 parse_mode is used. If Telegram rejects the MarkdownV2 (400 Bad Request),
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

        // Try MarkdownV2 first; fall back to plain text if Telegram rejects it (400).
        try
        {
            var markdownPayload = effectiveThreadId.HasValue
                ? (object)new { chat_id = chatId, text, parse_mode = "MarkdownV2", message_thread_id = effectiveThreadId.Value }
                : new { chat_id = chatId, text, parse_mode = "MarkdownV2" };

            return await PostForResultAsync<TelegramMessage>("sendMessage", markdownPayload, cancellationToken, allowMarkdownFallback: true);
        }
        catch (TelegramMarkdownParseException)
        {
            // MarkdownV2 was rejected — retry as plain text
            _logger.LogWarning("Telegram rejected MarkdownV2 for sendMessage to chat {ChatId}; retrying as plain text", chatId);
            var plainPayload = effectiveThreadId.HasValue
                ? (object)new { chat_id = chatId, text, message_thread_id = effectiveThreadId.Value }
                : new { chat_id = chatId, text };

            return await PostForResultAsync<TelegramMessage>("sendMessage", plainPayload, cancellationToken, allowMarkdownFallback: false);
        }
    }

    /// <summary>
    /// Edits an existing message's text. Uses MarkdownV2 parse_mode.
    /// </summary>
    /// <remarks>
    /// Telegram returns <c>400 Bad Request: message is not modified</c> when the new text is
    /// identical to what is already displayed. This is a benign condition that routinely occurs
    /// during streaming when a throttled or final flush re-sends unchanged content. It is treated
    /// as a no-op success — the message already holds the correct text — and the existing message
    /// is returned so the streaming pipeline continues without aborting the turn.
    /// </remarks>
    public async Task<TelegramMessage> EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await PostForResultAsync<TelegramMessage>(
                "editMessageText",
                new { chat_id = chatId, message_id = messageId, text, parse_mode = "MarkdownV2" },
                cancellationToken,
                allowMarkdownFallback: false);
        }
        catch (TelegramMessageNotModifiedException)
        {
            return new TelegramMessage { MessageId = messageId };
        }
    }

    /// <summary>
    /// Sends a Telegram Rich Message (Bot API 10.1+) using Rich Markdown, optionally into a forum
    /// topic thread. Rich Markdown natively supports tables, headings, lists, blockquotes, and
    /// more, so the markdown is sent nearly as-is (no MarkdownV2 escaping).
    /// </summary>
    /// <remarks>
    /// On a 400 Bad Request (e.g. the recipient's client predates Bot API 10.1, or the markdown is
    /// malformed) a <see cref="TelegramMarkdownParseException"/> is thrown so the caller can fall
    /// back to the legacy <see cref="SendMessageAsync(long, string, int?, CancellationToken)"/>
    /// (MarkdownV2 then plain text) path. General topic (threadId == 1) omits message_thread_id,
    /// matching <see cref="SendMessageAsync(long, string, int?, CancellationToken)"/>.
    /// </remarks>
    public Task<TelegramMessage> SendRichMessageAsync(
        long chatId,
        string markdown,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveThreadId = messageThreadId is 1 ? null : messageThreadId;
        var richMessage = new InputRichMessage { Markdown = markdown };

        var payload = effectiveThreadId.HasValue
            ? (object)new { chat_id = chatId, rich_message = richMessage, message_thread_id = effectiveThreadId.Value }
            : new { chat_id = chatId, rich_message = richMessage };

        return PostForResultAsync<TelegramMessage>("sendRichMessage", payload, cancellationToken, allowMarkdownFallback: true);
    }

    /// <summary>
    /// Streams a partial Rich Message draft (Bot API 10.1+) to a private chat while a reply is being
    /// generated. Drafts are ephemeral 30-second previews; reusing the same <paramref name="draftId"/>
    /// animates successive updates. The caller MUST finalize with
    /// <see cref="SendRichMessageAsync"/> to persist the message past the preview window.
    /// Returns the raw API boolean (true on success).
    /// </summary>
    /// <remarks>
    /// Per the Telegram spec this method targets a private chat only; group/forum streaming is not
    /// supported and should be handled by sending a single finalized
    /// <see cref="SendRichMessageAsync"/> instead. A 400 surfaces as
    /// <see cref="TelegramMarkdownParseException"/> so the caller can fall back.
    /// </remarks>
    public Task<bool> SendRichMessageDraftAsync(
        long chatId,
        long draftId,
        string markdown,
        int? messageThreadId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveThreadId = messageThreadId is 1 ? null : messageThreadId;
        var richMessage = new InputRichMessage { Markdown = markdown };

        var payload = effectiveThreadId.HasValue
            ? (object)new { chat_id = chatId, draft_id = draftId, rich_message = richMessage, message_thread_id = effectiveThreadId.Value }
            : new { chat_id = chatId, draft_id = draftId, rich_message = richMessage };

        return PostForResultAsync<bool>("sendRichMessageDraft", payload, cancellationToken, allowMarkdownFallback: true);
    }

    /// <summary>
    /// Edits an existing message to Rich Message content (Bot API 10.1+) using Rich Markdown.
    /// </summary>
    /// <remarks>
    /// As with <see cref="EditMessageTextAsync"/>, a <c>message is not modified</c> 400 is treated as
    /// a benign no-op success. Other 400s surface as <see cref="TelegramMarkdownParseException"/> so
    /// the caller can fall back to a plain edit.
    /// </remarks>
    public async Task<TelegramMessage> EditMessageRichAsync(
        long chatId,
        int messageId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var richMessage = new InputRichMessage { Markdown = markdown };
        try
        {
            return await PostForResultAsync<TelegramMessage>(
                "editMessageText",
                new { chat_id = chatId, message_id = messageId, rich_message = richMessage },
                cancellationToken,
                allowMarkdownFallback: true);
        }
        catch (TelegramMessageNotModifiedException)
        {
            return new TelegramMessage { MessageId = messageId };
        }
    }

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
            allowMarkdownFallback: false);

        return updates;
    }

    /// <summary>
    /// Registers a webhook URL with Telegram, including the secret token Telegram will echo back
    /// in the <c>X-Telegram-Bot-Api-Secret-Token</c> header on every update POST.
    /// </summary>
    /// <param name="url">Public HTTPS endpoint Telegram delivers updates to.</param>
    /// <param name="secretToken">
    /// 1–256 character token (<c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>_</c>, <c>-</c>) used to
    /// authenticate inbound requests. When null/empty no secret is registered (not recommended).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetWebhookAsync(string url, string? secretToken, CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "setWebhook",
            string.IsNullOrEmpty(secretToken)
                ? new { url, allowed_updates = AllowedUpdateTypes }
                : (object)new { url, secret_token = secretToken, allowed_updates = AllowedUpdateTypes },
            cancellationToken,
            allowMarkdownFallback: false);

    /// <summary>
    /// Removes any previously registered webhook, reverting to long polling mode.
    /// </summary>
    public Task DeleteWebhookAsync(CancellationToken cancellationToken = default)
        => PostForResultAsync<bool>(
            "deleteWebhook",
            new { drop_pending_updates = false },
            cancellationToken,
            allowMarkdownFallback: false);

    /// <summary>
    /// Retrieves file metadata for the given <paramref name="fileId"/> using the Telegram getFile API.
    /// The returned <see cref="TelegramFile.FilePath"/> can be passed to <see cref="DownloadFileAsync"/>.
    /// </summary>
    public Task<TelegramFile> GetFileAsync(string fileId, CancellationToken cancellationToken = default)
        => PostForResultAsync<TelegramFile>(
            "getFile",
            new { file_id = fileId },
            cancellationToken,
            allowMarkdownFallback: false);

    /// <summary>
    /// Downloads a file from the Telegram CDN using the relative path returned by <see cref="GetFileAsync"/>.
    /// Returns the raw binary content.
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.telegram.org/file/bot{_botToken}/{filePath}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Failed to download Telegram file '{filePath}': {(int)response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<T> PostForResultAsync<T>(string methodName, object payload, CancellationToken cancellationToken, bool allowMarkdownFallback)
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

            // 400 "message is not modified" is benign: the message already holds the requested
            // content (common during streaming when a flush re-sends unchanged text). Surface it
            // as a typed signal so callers can treat it as a no-op rather than a hard failure.
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                body.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                throw new TelegramMessageNotModifiedException($"Telegram reported no change for {methodName}: {body}");

            // 400 on sendMessage with MarkdownV2 parse_mode means malformed markdown — signal fallback.
            if (response.StatusCode == HttpStatusCode.BadRequest && allowMarkdownFallback)
                throw new TelegramMarkdownParseException($"Telegram rejected MarkdownV2 for {methodName}: {body}");

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
/// Thrown internally when Telegram returns a 400 for a MarkdownV2-formatted message,
/// signalling that <see cref="TelegramBotApiClient"/> should retry as plain text.
/// </summary>
internal sealed class TelegramMarkdownParseException(string message) : Exception(message);

/// <summary>
/// Thrown internally when Telegram returns a 400 "message is not modified" for an edit,
/// signalling that the existing message already holds the requested content. Callers treat
/// this as a benign no-op so streaming edits with unchanged text do not abort the turn.
/// </summary>
internal sealed class TelegramMessageNotModifiedException(string message) : Exception(message);
