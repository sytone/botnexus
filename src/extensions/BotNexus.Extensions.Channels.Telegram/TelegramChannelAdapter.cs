using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Telegram Bot channel adapter.
/// Supports one or more configured bot tokens; each bot represents one BotNexus agent.
/// </summary>
public sealed class TelegramChannelAdapter(
    ILogger<TelegramChannelAdapter> logger,
    IOptions<TelegramGatewayOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    IConfiguration? configuration = null) : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private const int StreamingFlushThresholdChars = 100;

    private readonly ILogger<TelegramChannelAdapter> _logger = logger;
    private readonly TelegramGatewayOptions _options = ResolveOptions(optionsAccessor, configuration);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ConcurrentDictionary<string, BotRuntime> _bots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves Telegram options from <see cref="IOptions{T}"/> if populated, or falls back to
    /// binding from <see cref="IConfiguration"/> when the extension was loaded after the initial
    /// DI registration pass and <see cref="IOptions{T}"/> was never bound.
    /// </summary>
    private static TelegramGatewayOptions ResolveOptions(
        IOptions<TelegramGatewayOptions> optionsAccessor,
        IConfiguration? configuration)
    {
        var opts = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(opts.BotToken) && opts.Bots.Count == 0 && configuration is not null)
        {
            var bound = new TelegramGatewayOptions();
            configuration.GetSection("channels:telegram").Bind(bound);
            return bound;
        }

        return opts;
    }

    /// <summary>
    /// Telegram channel identifier used by BotNexus routing.
    /// </summary>
    public override ChannelKey ChannelType => ChannelKey.From("telegram");

    /// <summary>
    /// Human-readable name shown in logs and diagnostics.
    /// </summary>
    public override string DisplayName => "Telegram Bot";

    /// <summary>
    /// Telegram supports message streaming via send + edit.
    /// </summary>
    public override bool SupportsStreaming => true;

    /// <inheritdoc />
    public override bool SupportsSteering => false;

    /// <inheritdoc />
    public override bool SupportsFollowUp => false;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => true;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => true;

    /// <inheritdoc />
    public override bool SupportsInboundImages => true;

    /// <inheritdoc />
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        EnsureBotsInitialized();

        foreach (var runtime in _bots.Values)
        {
            if (!string.IsNullOrWhiteSpace(runtime.Config.WebhookUrl))
            {
                await runtime.ApiClient.SetWebhookAsync(runtime.Config.WebhookUrl, runtime.WebhookSecret, cancellationToken);
                _logger.LogInformation(
                    "{DisplayName} bot '{BotName}' configured webhook mode at {WebhookUrl} (secret-token authentication enabled)",
                    DisplayName,
                    runtime.BotName,
                    runtime.Config.WebhookUrl);
                continue;
            }

            await runtime.ApiClient.DeleteWebhookAsync(cancellationToken);

            runtime.PollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runtime.PollingTask = Task.Run(
                () => RunPollingLoopAsync(runtime, Math.Max(1, runtime.Config.PollingTimeoutSeconds), runtime.PollingCancellation.Token),
                CancellationToken.None);

            _logger.LogInformation(
                "{DisplayName} bot '{BotName}' polling mode started (AgentId: {AgentId}, AllowedChatCount: {AllowedChatCount}, PollingTimeoutSeconds: {PollingTimeoutSeconds})",
                DisplayName,
                runtime.BotName,
                runtime.Config.AgentId ?? "<default-router>",
                runtime.Config.AllowedChatIds.Count,
                Math.Max(1, runtime.Config.PollingTimeoutSeconds));
        }
    }

    /// <inheritdoc />
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in _bots.Values)
            runtime.PollingCancellation?.Cancel();

        foreach (var runtime in _bots.Values)
        {
            if (runtime.PollingTask is not null)
            {
                try
                {
                    await runtime.PollingTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            runtime.PollingCancellation?.Dispose();
            runtime.StreamingStates.Clear();
            runtime.LastErrorReplyUtcByChat.Clear();
        }

        _bots.Clear();
        _logger.LogInformation("{DisplayName} channel adapter stopped", DisplayName);
    }

    /// <summary>
    /// Sends a complete outbound message through the Telegram adapter.
    /// Content is converted from Markdown to Telegram MarkdownV2 format.
    /// </summary>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();

        if (!TelegramChannelAddress.TryDecode(message.ChannelAddress, out var chatId, out var decodedThreadId))
        {
            _logger.LogWarning(
                "{DisplayName} send requested with invalid conversation id '{ConversationId}'",
                DisplayName,
                message.ChannelAddress.Value);
            return;
        }

        var runtime = ResolveOutboundBot(message);
        EnsureChatAllowed(runtime.Config, chatId);
        var formatted = BuildOutboundText(message.Content, message.Metadata, message.DisplayPrefix);

        foreach (var chunk in SplitMessage(formatted, Math.Max(1, runtime.Config.MaxMessageLength)))
            await runtime.ApiClient.SendMessageAsync(chatId, chunk, decodedThreadId, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();
        if (string.IsNullOrEmpty(delta) || !TelegramChannelAddress.TryDecode(target.ChannelAddress, out var chatId, out var messageThreadId))
            return;

        var runtime = ResolveSingleConfiguredBot();
        EnsureChatAllowed(runtime.Config, chatId);
        var stateKey = target.ChannelAddress.Value;
        var state = runtime.StreamingStates.GetOrAdd(stateKey, _ => new StreamingState(chatId, messageThreadId));

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            // Buffer raw delta; MarkdownV2 conversion happens at flush time so formatting
            // tokens that span multiple deltas (e.g. **bold**) are preserved intact.
            state.Buffer.Append(delta);
            state.PendingCharacterCount += delta.Length;
            if (ShouldFlush(state, runtime.Config))
                await FlushStreamingStateAsync(runtime, state, force: false, cancellationToken);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();
        if (!TelegramChannelAddress.TryDecode(target.ChannelAddress, out var chatId, out var messageThreadId))
            return;

        var runtime = ResolveSingleConfiguredBot();
        EnsureChatAllowed(runtime.Config, chatId);
        var stateKey = target.ChannelAddress.Value;
        var state = runtime.StreamingStates.GetOrAdd(stateKey, _ => new StreamingState(chatId, messageThreadId));

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            switch (streamEvent.Type)
            {
                case AgentStreamEventType.MessageStart:
                    state.Reset();
                    break;

                case AgentStreamEventType.ContentDelta when streamEvent.ContentDelta is not null:
                    // Buffer raw markdown; conversion to MarkdownV2 happens at flush time.
                    state.Buffer.Append(streamEvent.ContentDelta);
                    state.PendingCharacterCount += streamEvent.ContentDelta.Length;
                    break;

                case AgentStreamEventType.ThinkingDelta when streamEvent.ThinkingContent is not null:
                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("Thinking: ");
                    state.Buffer.Append(streamEvent.ThinkingContent);
                    state.PendingCharacterCount += streamEvent.ThinkingContent.Length;
                    break;

                case AgentStreamEventType.ToolStart:
                {
                    var toolName = streamEvent.ToolName ?? "tool";
                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("[");
                    state.Buffer.Append(toolName);
                    state.Buffer.Append("] started");
                    state.PendingCharacterCount += toolName.Length + 10;
                    break;
                }

                case AgentStreamEventType.ToolEnd:
                {
                    var status = streamEvent.ToolIsError == true ? "failed" : "completed";
                    var toolName = streamEvent.ToolName ?? streamEvent.ToolCallId ?? "tool";
                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("[");
                    state.Buffer.Append(toolName);
                    state.Buffer.Append("] ");
                    state.Buffer.Append(status);
                    state.PendingCharacterCount += toolName.Length + status.Length + 3;
                    break;
                }

                case AgentStreamEventType.Error when streamEvent.ErrorMessage is not null:
                    if (!ShouldSendErrorReply(runtime, chatId))
                        return;

                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("⚠️ ");
                    state.Buffer.Append(streamEvent.ErrorMessage);
                    state.PendingCharacterCount += streamEvent.ErrorMessage.Length + 2;
                    break;

                case AgentStreamEventType.MessageEnd:
                    await FlushStreamingStateAsync(runtime, state, force: true, cancellationToken);
                    state.Reset();
                    // Remove the entry to prevent unbounded dictionary growth over time.
                    runtime.StreamingStates.TryRemove(stateKey, out _);
                    return;
            }

            if (ShouldFlush(state, runtime.Config))
                await FlushStreamingStateAsync(runtime, state, force: false, cancellationToken);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private async Task RunPollingLoopAsync(BotRuntime runtime, int pollingTimeoutSeconds, CancellationToken cancellationToken)
    {
        long? offset = null;
        var lastEvictionUtc = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await runtime.ApiClient.GetUpdatesAsync(offset, pollingTimeoutSeconds, cancellationToken);
                foreach (var update in updates.OrderBy(u => u.UpdateId))
                {
                    offset = update.UpdateId + 1;
                    await HandleUpdateAsync(runtime, update, cancellationToken);
                }

                // Periodically evict stale error-reply tracking entries (every 5 minutes).
                if (DateTimeOffset.UtcNow - lastEvictionUtc > TimeSpan.FromMinutes(5))
                {
                    EvictStaleErrorStateForRuntime(runtime, TimeSpan.FromMinutes(30));
                    lastEvictionUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{DisplayName} bot '{BotName}' polling loop error", DisplayName, runtime.BotName);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleUpdateAsync(BotRuntime runtime, TelegramUpdate update, CancellationToken cancellationToken)
    {
        // Only process real user messages — not channel posts (no authenticated sender)
        var message = update.Message
            ?? (runtime.Config.ProcessEditedMessages ? update.EditedMessage : null);

        // Accept text messages or photo messages (with optional caption)
        var hasText = !string.IsNullOrWhiteSpace(message?.Text);
        var hasPhoto = message?.Photo is { Length: > 0 };
        if (message?.Chat is null || (!hasText && !hasPhoto))
            return;

        var chatId = message.Chat.Id;
        if (!IsChatAllowed(runtime.Config, chatId))
        {
            _logger.LogDebug("{DisplayName} bot '{BotName}' ignored message from unauthorized chat {ChatId}", DisplayName, runtime.BotName, chatId);
            return;
        }

        if (message.From is null)
        {
            _logger.LogDebug("bot '{BotName}' ignored message with no sender (updateId={UpdateId})", runtime.BotName, update.UpdateId);
            return;
        }

        var fromId = message.From.Id;
        if (!IsUserAllowed(runtime.Config, fromId))
        {
            _logger.LogDebug("bot '{BotName}' ignored message from unauthorized user {UserId}", runtime.BotName, fromId);
            return;
        }

        var senderId = message.From.Id.ToString(CultureInfo.InvariantCulture);

        // Build optional image content parts from the attached photo (if any)
        IReadOnlyList<MessageContentPart>? contentParts = null;
        if (hasPhoto)
        {
            // Telegram provides multiple resolutions; the last element is always the largest.
            var largestPhoto = message.Photo!.OrderByDescending(p => p.FileSize ?? 0).First();
            try
            {
                var file = await runtime.ApiClient.GetFileAsync(largestPhoto.FileId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(file.FilePath))
                {
                    var imageBytes = await runtime.ApiClient.DownloadFileAsync(file.FilePath, cancellationToken);
                    contentParts = [new BinaryContentPart { MimeType = "image/jpeg", Data = imageBytes }];
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "bot '{BotName}' failed to download photo for updateId={UpdateId}; proceeding with caption only", runtime.BotName, update.UpdateId);
            }
        }

        // Caption is the text for photo messages; Text is the text for regular messages.
        var textContent = (hasPhoto ? message.Caption : message.Text) ?? string.Empty;

        await DispatchInboundAsync(new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            Sender = CitizenId.Of(UserId.From(senderId)),
            ChannelAddress = TelegramChannelAddress.Encode(chatId, message.MessageThreadId),
            Content = textContent,
            ContentParts = contentParts,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                targetAgentId: runtime.Config.AgentId,
                sessionId: null,
                conversationId: null),
            Metadata = new Dictionary<string, object?>
            {
                ["telegramBotName"] = runtime.BotName,
                ["telegramUpdateId"] = update.UpdateId,
                ["telegramMessageId"] = message.MessageId,
                ["telegramChatId"] = chatId
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Result of attempting to handle an inbound Telegram webhook update.
    /// Lets the HTTP receiver map outcomes to status codes without leaking detail to callers.
    /// </summary>
    public enum WebhookHandleResult
    {
        /// <summary>The update was accepted and dispatched (or intentionally ignored as non-actionable).</summary>
        Accepted,

        /// <summary>The supplied secret token did not match the bot's registered secret. Map to 403.</summary>
        SecretMismatch,

        /// <summary>No bot with the supplied name is configured, or it is not in webhook mode. Map to 404.</summary>
        UnknownBot,
    }

    /// <summary>
    /// Handles a single inbound Telegram update delivered to the webhook receiver for
    /// <paramref name="botName"/>. The supplied secret token is validated in constant time against
    /// the secret registered with Telegram before the update is routed through the same allow-list
    /// and dispatch path used by long polling.
    /// </summary>
    /// <param name="botName">Logical bot name from the webhook route.</param>
    /// <param name="update">The deserialized Telegram update.</param>
    /// <param name="providedSecret">Raw <c>X-Telegram-Bot-Api-Secret-Token</c> header value (may be null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An outcome the receiver maps to an HTTP status code.</returns>
    public async Task<WebhookHandleResult> HandleWebhookUpdateAsync(
        string botName,
        TelegramUpdate update,
        string? providedSecret,
        CancellationToken cancellationToken = default)
    {
        EnsureBotsInitialized();

        if (string.IsNullOrWhiteSpace(botName) || !_bots.TryGetValue(botName, out var runtime))
            return WebhookHandleResult.UnknownBot;

        // Webhook delivery is only meaningful for bots configured in webhook mode. A bot in polling
        // mode has no registered secret; treating it as unknown avoids an unauthenticated code path.
        if (string.IsNullOrWhiteSpace(runtime.Config.WebhookUrl))
            return WebhookHandleResult.UnknownBot;

        if (!TelegramWebhookSecret.Matches(runtime.WebhookSecret, providedSecret))
        {
            _logger.LogWarning(
                "{DisplayName} bot '{BotName}' rejected webhook update with invalid secret token",
                DisplayName,
                runtime.BotName);
            return WebhookHandleResult.SecretMismatch;
        }

        await HandleUpdateAsync(runtime, update, cancellationToken);
        return WebhookHandleResult.Accepted;
    }

    /// <summary>
    /// Returns the logical names of all configured bots running in webhook mode.
    /// Used by the HTTP receiver to map a route per webhook bot.
    /// </summary>
    public IReadOnlyCollection<string> GetWebhookBotNames()
    {
        EnsureBotsInitialized();
        return _bots.Values
            .Where(r => !string.IsNullOrWhiteSpace(r.Config.WebhookUrl))
            .Select(r => r.BotName)
            .ToArray();
    }

    private async Task FlushStreamingStateAsync(BotRuntime runtime, StreamingState state, bool force, CancellationToken cancellationToken)
    {
        if (state.Buffer.Length == 0)
            return;

        var maxLength = Math.Max(1, runtime.Config.MaxMessageLength);
        while (state.Buffer.Length > maxLength)
        {
            // Convert the raw chunk to MarkdownV2 before sending.
            var rawChunk = state.Buffer.ToString(0, maxLength);
            var formattedChunk = TelegramMarkdownFormatter.Convert(rawChunk);
            await SendOrEditStreamingMessageAsync(runtime, state, formattedChunk, cancellationToken);
            state.MessageId = null;
            state.Buffer.Remove(0, maxLength);
        }

        if (!force && !ShouldFlush(state, runtime.Config))
            return;

        var rawText = state.Buffer.ToString();
        if (string.IsNullOrEmpty(rawText))
            return;

        // Convert accumulated raw markdown to Telegram MarkdownV2 at flush time.
        // Doing the conversion here (not per-delta) ensures multi-delta formatting tokens
        // such as **bold** are fully assembled before being transformed.
        var formattedText = TelegramMarkdownFormatter.Convert(rawText);
        await SendOrEditStreamingMessageAsync(runtime, state, formattedText, cancellationToken);
        state.LastFlushUtc = DateTimeOffset.UtcNow;
        state.PendingCharacterCount = 0;
    }

    private async Task SendOrEditStreamingMessageAsync(BotRuntime runtime, StreamingState state, string text, CancellationToken cancellationToken)
    {
        if (state.MessageId is null)
        {
            var sent = await runtime.ApiClient.SendMessageAsync(state.ChatId, text, state.MessageThreadId, cancellationToken);
            state.MessageId = sent.MessageId;
            return;
        }

        var edited = await runtime.ApiClient.EditMessageTextAsync(state.ChatId, state.MessageId.Value, text, cancellationToken);
        state.MessageId = edited.MessageId;
    }

    private void EnsureBotsInitialized()
    {
        if (_bots.Count > 0)
            return;

        var configs = _options.ResolveActiveBots();
        if (configs.Count == 0)
            throw new InvalidOperationException("Telegram channel requires at least one configured bot.");

        foreach (var (botName, config) in configs)
        {
            if (string.IsNullOrWhiteSpace(config.BotToken))
                throw new InvalidOperationException($"Telegram bot '{botName}' requires BotToken.");

            var client = new TelegramBotApiClient(
                _httpClientFactory.CreateClient(botName),
                config.BotToken,
                NullLogger<TelegramBotApiClient>.Instance);

            // Resolve the webhook secret once, only for bots in webhook mode. A configured secret is
            // used when syntactically valid; otherwise a cryptographically strong one is generated so
            // webhook mode is never registered without authentication. Polling bots get no secret.
            var webhookSecret = string.Empty;
            if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
            {
                webhookSecret = TelegramWebhookSecret.IsValid(config.WebhookSecretToken)
                    ? config.WebhookSecretToken!
                    : TelegramWebhookSecret.Generate();

                if (!TelegramWebhookSecret.IsValid(config.WebhookSecretToken)
                    && !string.IsNullOrEmpty(config.WebhookSecretToken))
                {
                    _logger.LogWarning(
                        "{DisplayName} bot '{BotName}' configured webhookSecretToken is invalid (allowed: A-Z a-z 0-9 _ -, length 1-256); generated a replacement",
                        DisplayName,
                        botName);
                }
            }

            _bots.TryAdd(botName, new BotRuntime(botName, config, client, webhookSecret));
        }
    }

    private BotRuntime ResolveSingleConfiguredBot()
    {
        if (_bots.Count == 1)
            return _bots.Values.Single();

        throw new InvalidOperationException("Streaming over Telegram requires a single configured bot or explicit bot routing metadata.");
    }

    private BotRuntime ResolveOutboundBot(OutboundMessage message)
    {
        if (message.Metadata.TryGetValue("telegramBotName", out var raw) && raw?.ToString() is { Length: > 0 } botName)
        {
            if (_bots.TryGetValue(botName, out var runtimeByName))
                return runtimeByName;

            throw new InvalidOperationException($"Telegram bot '{botName}' is not configured.");
        }

        if (_bots.Count == 1)
            return _bots.Values.Single();

        throw new InvalidOperationException("Multiple Telegram bots are configured. Outbound Telegram messages must specify metadata['telegramBotName'].");
    }

    private static bool TryParseChatId(string conversationId, out long chatId)
        => TelegramChannelAddress.TryDecode(ChannelAddress.From(conversationId), out chatId, out _);

    private static bool IsChatAllowed(TelegramBotConfig config, long chatId)
        => config.AllowedChatIds.Count == 0 || config.AllowedChatIds.Contains(chatId);

    private static bool IsUserAllowed(TelegramBotConfig config, long userId)
        => config.AllowedUserIds.Count == 0 || config.AllowedUserIds.Contains(userId);

    private static void EnsureChatAllowed(TelegramBotConfig config, long chatId)
    {
        if (!IsChatAllowed(config, chatId))
            throw new InvalidOperationException($"Telegram chat '{chatId}' is not allowed for this bot.");
    }

    private static bool ShouldFlush(StreamingState state, TelegramBotConfig config)
        => state.PendingCharacterCount >= StreamingFlushThresholdChars ||
           DateTimeOffset.UtcNow - state.LastFlushUtc >= TimeSpan.FromMilliseconds(Math.Max(1, config.StreamingBufferMs));

    private static string BuildOutboundText(string content, IReadOnlyDictionary<string, object?> metadata, string? displayPrefix)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(displayPrefix))
        {
            builder.Append(TelegramMarkdownFormatter.EscapeMarkdownV2(displayPrefix));
            builder.Append(' ');
        }

        if (TryGetMetadataString(metadata, "thinking", out var thinking))
        {
            builder.Append("Thinking: ");
            builder.Append(TelegramMarkdownFormatter.Convert(thinking));
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(TelegramMarkdownFormatter.Convert(content));

        if (TryGetMetadataString(metadata, "toolCall", out var toolCall))
        {
            builder.AppendLine();
            builder.Append("\\[");
            builder.Append(TelegramMarkdownFormatter.EscapeMarkdownV2(toolCall));
            builder.Append("\\]");
        }

        return builder.ToString();
    }

    private bool ShouldSendErrorReply(BotRuntime runtime, long chatId)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromMilliseconds(Math.Max(1, runtime.Config.ErrorCooldownMs));

        while (true)
        {
            if (!runtime.LastErrorReplyUtcByChat.TryGetValue(chatId, out var lastSent))
            {
                if (runtime.LastErrorReplyUtcByChat.TryAdd(chatId, now))
                    return true;

                continue;
            }

            if (now - lastSent < cooldown)
                return false;

            if (runtime.LastErrorReplyUtcByChat.TryUpdate(chatId, now, lastSent))
                return true;
        }
    }

    private static bool TryGetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key, out string value)
    {
        if (metadata.TryGetValue(key, out var raw) && raw is not null && !string.IsNullOrWhiteSpace(raw.ToString()))
        {
            value = raw.ToString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> SplitMessage(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;
            yield break;
        }

        for (var offset = 0; offset < content.Length; offset += maxLength)
        {
            var length = Math.Min(maxLength, content.Length - offset);
            yield return content.Substring(offset, length);
        }
    }

    private static void AppendLineIfNeeded(StringBuilder builder)
    {
        if (builder.Length > 0)
            builder.AppendLine();
    }

    /// <summary>
    /// Returns the total number of streaming state entries across all bot runtimes.
    /// Used for diagnostics and testing to verify state is properly evicted.
    /// </summary>
    internal int GetStreamingStateCount()
        => _bots.Values.Sum(r => r.StreamingStates.Count);

    /// <summary>
    /// Returns the total number of error reply tracking entries across all bot runtimes.
    /// Used for diagnostics and testing to verify state is properly evicted.
    /// </summary>
    internal int GetErrorReplyStateCount()
        => _bots.Values.Sum(r => r.LastErrorReplyUtcByChat.Count);

    /// <summary>
    /// Removes error reply tracking entries older than <paramref name="maxAge"/>.
    /// Prevents unbounded growth of the per-chat error cooldown dictionary.
    /// </summary>
    /// <param name="maxAge">Maximum age for retained entries. Entries older than this are removed.</param>
    internal void EvictStaleErrorState(TimeSpan maxAge)
    {
        foreach (var runtime in _bots.Values)
            EvictStaleErrorStateForRuntime(runtime, maxAge);
    }

    private static void EvictStaleErrorStateForRuntime(BotRuntime runtime, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var kvp in runtime.LastErrorReplyUtcByChat)
        {
            if (kvp.Value < cutoff)
                runtime.LastErrorReplyUtcByChat.TryRemove(kvp.Key, out _);
        }
    }

    private sealed class BotRuntime(string botName, TelegramBotConfig config, TelegramBotApiClient apiClient, string webhookSecret)
    {
        public string BotName { get; } = botName;
        public TelegramBotConfig Config { get; } = config;
        public TelegramBotApiClient ApiClient { get; } = apiClient;

        /// <summary>
        /// Secret token registered with Telegram for this bot's webhook, validated against the
        /// <c>X-Telegram-Bot-Api-Secret-Token</c> header on each inbound update. Empty for polling bots.
        /// </summary>
        public string WebhookSecret { get; } = webhookSecret;

        public ConcurrentDictionary<string, StreamingState> StreamingStates { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<long, DateTimeOffset> LastErrorReplyUtcByChat { get; } = new();
        public CancellationTokenSource? PollingCancellation { get; set; }
        public Task? PollingTask { get; set; }
    }

    private sealed class StreamingState(long chatId, int? messageThreadId)
    {
        public long ChatId { get; } = chatId;
        public int? MessageThreadId { get; } = messageThreadId;
        public int? MessageId { get; set; }
        public int PendingCharacterCount { get; set; }
        public DateTimeOffset LastFlushUtc { get; set; } = DateTimeOffset.UtcNow;
        public StringBuilder Buffer { get; } = new();
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public void Reset()
        {
            MessageId = null;
            PendingCharacterCount = 0;
            LastFlushUtc = DateTimeOffset.UtcNow;
            Buffer.Clear();
        }
    }
}
