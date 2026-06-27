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

    // Monotonic source of non-zero Rich Message draft ids. Telegram animates updates that reuse the
    // same draft id, so each stream takes one id and keeps it for the life of that message.
    private long _draftIdCounter;

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

        await SendOutboundAsync(runtime, chatId, decodedThreadId, message, cancellationToken);
    }

    /// <summary>
    /// Sends a complete outbound message, preferring Telegram Rich Markdown (Bot API 10.1+) when
    /// enabled and falling back to the legacy MarkdownV2 (then plain text) path if Telegram rejects
    /// the rich send — so content is never dropped even for older clients.
    /// </summary>
    private async Task SendOutboundAsync(
        BotRuntime runtime,
        long chatId,
        int? decodedThreadId,
        OutboundMessage message,
        CancellationToken cancellationToken)
    {
        if (runtime.Config.RichMessages)
        {
            var richMarkdown = BuildOutboundMarkdown(message.Content, message.Metadata, message.DisplayPrefix);
            try
            {
                foreach (var chunk in SplitMarkdown(richMarkdown, Math.Max(1, runtime.Config.MaxRichMessageLength)))
                    await runtime.ApiClient.SendRichMessageAsync(chatId, chunk, decodedThreadId, cancellationToken);
                return;
            }
            catch (TelegramMarkdownParseException ex)
            {
                // Telegram rejected the Rich Message (e.g. a client older than Bot API 10.1, or
                // malformed markdown). Fall back to the legacy MarkdownV2/plain path below so the
                // message still arrives.
                _logger.LogWarning(
                    ex,
                    "{DisplayName} bot '{BotName}' rich send rejected for chat {ChatId}; falling back to MarkdownV2",
                    DisplayName,
                    runtime.BotName,
                    chatId);
            }
        }

        // Legacy path: MarkdownV2 with an automatic plain-text fallback inside SendMessageAsync.
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

        var runtime = ResolveStreamingBot(null);
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

        var runtime = ResolveStreamingBot(streamEvent.AgentId);
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
                    await FinalizeStreamAsync(runtime, state, cancellationToken);
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

        // Rich Markdown streaming uses ephemeral animated drafts, but only in private chats
        // (sendRichMessageDraft is DM-only). In groups/forum topics the content is buffered silently
        // and delivered once at MessageEnd via a single sendRichMessage (see FinalizeStreamAsync).
        if (runtime.Config.RichMessages && state.ChatId > 0 && !state.RichDraftDisabled)
        {
            await FlushRichDraftAsync(runtime, state, force, cancellationToken);
            return;
        }

        if (runtime.Config.RichMessages)
        {
            // Group/forum Rich Markdown: no live preview (drafts are DM-only). Just keep buffering;
            // FinalizeStreamAsync sends the whole message at MessageEnd. Throttle the pending counter
            // so ShouldFlush doesn't spin.
            state.LastFlushUtc = DateTimeOffset.UtcNow;
            state.PendingCharacterCount = 0;
            return;
        }

        // Legacy MarkdownV2 streaming: send first, then edit; convert raw markdown to MarkdownV2 at
        // flush time so multi-delta tokens like **bold** are assembled before transformation.
        await FlushLegacyMarkdownV2Async(runtime, state, force, cancellationToken);
    }

    // Sends/updates the ephemeral Rich Message draft preview for a private chat. The draft animates
    // as the same draft id is reused; it is finalized into a persistent message at MessageEnd.
    private async Task FlushRichDraftAsync(BotRuntime runtime, StreamingState state, bool force, CancellationToken cancellationToken)
    {
        if (!force && !ShouldFlush(state, runtime.Config))
            return;

        state.RichDraftId ??= NextDraftId();
        var markdown = state.Buffer.ToString();

        try
        {
            await runtime.ApiClient.SendRichMessageDraftAsync(
                state.ChatId,
                state.RichDraftId.Value,
                markdown,
                state.MessageThreadId,
                cancellationToken);
            state.HasRichDraft = true;
        }
        catch (TelegramMarkdownParseException ex)
        {
            // The draft was rejected (e.g. transient malformed-markdown window mid-stream). Stop
            // drafting; the accumulated content is still delivered intact at MessageEnd. A lost
            // preview frame is harmless.
            _logger.LogDebug(
                ex,
                "{DisplayName} bot '{BotName}' rich draft rejected for chat {ChatId}; suppressing further drafts this stream",
                DisplayName,
                runtime.BotName,
                state.ChatId);
            state.RichDraftDisabled = true;
        }

        state.LastFlushUtc = DateTimeOffset.UtcNow;
        state.PendingCharacterCount = 0;
    }

    private async Task FlushLegacyMarkdownV2Async(BotRuntime runtime, StreamingState state, bool force, CancellationToken cancellationToken)
    {
        var maxLength = Math.Max(1, runtime.Config.MaxMessageLength);
        while (state.Buffer.Length > maxLength)
        {
            var rawChunk = DrainStreamingBuffer(state.Buffer, maxLength);
            var formattedChunk = TelegramMarkdownFormatter.Convert(rawChunk);
            await SendOrEditStreamingMessageAsync(runtime, state, formattedChunk, cancellationToken);
            state.MessageId = null;
        }

        if (!force && !ShouldFlush(state, runtime.Config))
            return;

        var rawText = state.Buffer.ToString();
        if (string.IsNullOrEmpty(rawText))
            return;

        var formattedText = TelegramMarkdownFormatter.Convert(rawText);
        await SendOrEditStreamingMessageAsync(runtime, state, formattedText, cancellationToken);
        state.LastFlushUtc = DateTimeOffset.UtcNow;
        state.PendingCharacterCount = 0;
    }

    /// <summary>
    /// Finalizes a stream at MessageEnd, delivering the persistent message. For Rich Markdown this is
    /// a single <c>sendRichMessage</c> (which also replaces any ephemeral draft preview); on rejection
    /// it falls back to the legacy MarkdownV2/plain path so the message always persists. For the
    /// legacy path the buffered content is flushed as the final MarkdownV2 edit.
    /// </summary>
    private async Task FinalizeStreamAsync(BotRuntime runtime, StreamingState state, CancellationToken cancellationToken)
    {
        var rawText = state.Buffer.ToString();

        if (!runtime.Config.RichMessages)
        {
            await FlushLegacyMarkdownV2Async(runtime, state, force: true, cancellationToken);
            return;
        }

        if (string.IsNullOrEmpty(rawText))
        {
            // Nothing to persist (e.g. an empty stream). A dangling draft simply expires after 30s.
            return;
        }

        try
        {
            foreach (var chunk in SplitMarkdown(rawText, Math.Max(1, runtime.Config.MaxRichMessageLength)))
                await runtime.ApiClient.SendRichMessageAsync(state.ChatId, chunk, state.MessageThreadId, cancellationToken);
        }
        catch (TelegramMarkdownParseException ex)
        {
            // Rich finalize was rejected — fall back to MarkdownV2 (then plain) so the message still
            // arrives. Send fresh (do not edit the draft, which is a separate ephemeral object).
            _logger.LogWarning(
                ex,
                "{DisplayName} bot '{BotName}' rich finalize rejected for chat {ChatId}; falling back to MarkdownV2",
                DisplayName,
                runtime.BotName,
                state.ChatId);

            var formatted = TelegramMarkdownFormatter.Convert(rawText);
            foreach (var chunk in SplitMessage(formatted, Math.Max(1, runtime.Config.MaxMessageLength)))
                await runtime.ApiClient.SendMessageAsync(state.ChatId, chunk, state.MessageThreadId, cancellationToken);
        }
    }

    private long NextDraftId()
    {
        // Telegram requires a non-zero draft id; skip 0 on the (astronomically unlikely) wrap.
        var id = System.Threading.Interlocked.Increment(ref _draftIdCounter);
        return id == 0 ? System.Threading.Interlocked.Increment(ref _draftIdCounter) : id;
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

    // Resolves which bot a streamed reply should be sent through. Single-bot
    // deployments are unambiguous; multi-bot deployments route by the originating
    // agent because each bot binds a distinct agentId and the enriched
    // AgentStreamEvent carries that agent — this sends the reply back through the
    // same bot that received the message.
    private BotRuntime ResolveStreamingBot(AgentId? agentId)
    {
        if (_bots.Count == 1)
            return _bots.Values.Single();

        if (agentId is { } id)
        {
            var matches = _bots.Values
                .Where(b => string.Equals(b.Config.AgentId, id.Value, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1)
                return matches[0];

            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple Telegram bots are configured for agent '{id.Value}'. Each bot must bind a distinct agentId.");
        }

        throw new InvalidOperationException(
            "Streaming over Telegram requires a single configured bot or a stream event carrying the originating agent id.");
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

    /// <summary>
    /// Builds the outbound message body as raw Rich Markdown (GitHub-Flavored-Markdown-ish) for the
    /// Telegram Rich Message <c>markdown</c> field. Unlike <see cref="BuildOutboundText"/>, no
    /// MarkdownV2 escaping is applied — Rich Markdown renders standard markdown (tables, headings,
    /// lists, links) directly, so the LLM content passes through nearly as-is.
    /// </summary>
    private static string BuildOutboundMarkdown(string content, IReadOnlyDictionary<string, object?> metadata, string? displayPrefix)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(displayPrefix))
        {
            // Render the prefix in bold; Rich Markdown shows literal punctuation without escaping.
            builder.Append("**");
            builder.Append(displayPrefix.Trim());
            builder.Append("** ");
        }

        if (TryGetMetadataString(metadata, "thinking", out var thinking))
        {
            builder.Append("_Thinking:_ ");
            builder.Append(thinking);
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(content);

        if (TryGetMetadataString(metadata, "toolCall", out var toolCall))
        {
            builder.AppendLine();
            // Tool-call marker as inline code so brackets/underscores render literally.
            builder.Append('`');
            builder.Append(toolCall);
            builder.Append('`');
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

    /// <summary>
    /// Splits plain (non-rich / MarkdownV2 / fallback) outbound text into chunks no longer than
    /// <paramref name="maxLength"/> UTF-16 code units, never severing a surrogate pair across a chunk
    /// boundary (see <see cref="SliceSurrogateSafe"/>). Used by the legacy and fallback send paths.
    /// </summary>
    internal static IEnumerable<string> SplitMessage(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;
            yield break;
        }

        var offset = 0;
        while (offset < content.Length)
        {
            var chunk = SliceSurrogateSafe(content, offset, maxLength);
            yield return chunk;
            offset += chunk.Length;
        }
    }

    /// <summary>
    /// Returns a slice of <paramref name="content"/> starting at <paramref name="offset"/> that is at
    /// most <paramref name="maxLength"/> UTF-16 code units long and never ends in the middle of a
    /// surrogate pair. If taking <paramref name="maxLength"/> code units would split an astral
    /// character (emoji / non-BMP glyph) — i.e. the last code unit is a high surrogate — the slice is
    /// shortened by one so the whole pair moves into the next chunk instead of leaving a lone
    /// surrogate that serializes to invalid UTF-16 (which Telegram rejects with
    /// <c>400 can't parse entities</c>). Callers must advance by the returned slice's length, not by
    /// a fixed <paramref name="maxLength"/>, so the deferred code unit is not skipped.
    /// </summary>
    /// <remarks>
    /// At least one code unit is always returned (assuming <paramref name="maxLength"/> &gt;= 1 and
    /// characters remain) to guarantee forward progress. The only case that can return a slice ending
    /// on a high surrogate is the degenerate <paramref name="maxLength"/> == 1 with a surrogate pair
    /// at <paramref name="offset"/>; a single-code-unit limit cannot physically hold a two-unit pair,
    /// and real Telegram limits (4096 / 32768) never hit this.
    /// </remarks>
    internal static string SliceSurrogateSafe(string content, int offset, int maxLength)
    {
        var remaining = content.Length - offset;
        var length = Math.Min(maxLength, remaining);

        // If the slice would end on a high surrogate whose low half lies in the next chunk, back off
        // by one so the pair is not severed. Keep at least one code unit for forward progress.
        if (length > 1 && offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1]))
        {
            length--;
        }

        return content.Substring(offset, length);
    }

    /// <summary>
    /// Removes and returns a leading chunk of at most <paramref name="maxLength"/> UTF-16 code units
    /// from the front of the streaming <paramref name="buffer"/>, never severing a surrogate pair at
    /// the chunk boundary. Used by the mid-stream MarkdownV2 flush (<see cref="FlushLegacyMarkdownV2Async"/>)
    /// to drain an over-length buffer one message at a time.
    /// </summary>
    /// <remarks>
    /// The previous implementation sliced and removed a fixed <paramref name="maxLength"/> code units
    /// (<c>Buffer.ToString(0, maxLength)</c> + <c>Buffer.Remove(0, maxLength)</c>), which severed an
    /// emoji / astral glyph straddling the boundary into a lone high surrogate (this chunk) and an
    /// orphaned low surrogate (left at the head of the buffer) — both invalid UTF-16 that Telegram
    /// rejects. This shares the same boundary-safe back-off as <see cref="SliceSurrogateSafe"/>: if the
    /// cut would land between a surrogate pair, the cut is shortened by one so the whole pair moves
    /// into the next chunk. The buffer is advanced by the actual chunk length, so the deferred code
    /// unit is never skipped, and at least one code unit is always drained (forward progress).
    /// </remarks>
    internal static string DrainStreamingBuffer(StringBuilder buffer, int maxLength)
    {
        var available = Math.Min(maxLength, buffer.Length);
        var length = available;

        // Back off by one if the cut would split a surrogate pair (last code unit is a high surrogate
        // whose low half is still in the buffer). Keep at least one unit so the drain always advances.
        if (length > 1 && length < buffer.Length && char.IsHighSurrogate(buffer[length - 1]))
        {
            length--;
        }

        var chunk = buffer.ToString(0, length);
        buffer.Remove(0, length);
        return chunk;
    }

    /// <summary>
    /// Splits Rich Markdown into chunks at line boundaries so that tables, code blocks, and other
    /// multi-line constructs are not severed mid-line. Lines are accumulated until adding the next
    /// would exceed <paramref name="maxLength"/>; a single line longer than the limit is split as a
    /// last resort. Most replies fit in one chunk (the rich limit is 32768 characters).
    /// </summary>
    internal static IEnumerable<string> SplitMarkdown(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;
            yield break;
        }

        if (content.Length <= maxLength)
        {
            yield return content;
            yield break;
        }

        var builder = new StringBuilder();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Length - 1;

            // A single line longer than the limit must be hard-split as a last resort. Slice on
            // surrogate-pair boundaries so an emoji straddling the limit is not severed into a lone
            // surrogate (the same hazard SliceSurrogateSafe guards in SplitMessage).
            if (line.Length > maxLength)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                var offset = 0;
                while (offset < line.Length)
                {
                    var chunk = SliceSurrogateSafe(line, offset, maxLength);
                    yield return chunk;
                    offset += chunk.Length;
                }
                continue;
            }

            // +1 accounts for the '\n' that re-joins this line to the buffer.
            var projected = builder.Length == 0 ? line.Length : builder.Length + 1 + line.Length;
            if (projected > maxLength)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);

            if (isLast && builder.Length > 0)
                yield return builder.ToString();
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

        /// <summary>
        /// Non-zero id identifying the Rich Message draft for this stream. Reusing the same id across
        /// flushes makes Telegram animate the successive draft updates. Assigned lazily on the first
        /// draft flush; null until then. Reset between messages.
        /// </summary>
        public long? RichDraftId { get; set; }

        /// <summary>
        /// True once a Rich Message draft has been sent for this stream, so MessageEnd knows it must
        /// finalize the draft with a persistent <c>sendRichMessage</c> (drafts are ephemeral 30s
        /// previews that vanish if not finalized).
        /// </summary>
        public bool HasRichDraft { get; set; }

        /// <summary>
        /// Set when a draft send fails (e.g. Telegram rejects it). Further draft attempts are skipped
        /// for this stream; the accumulated content is delivered once at MessageEnd via the normal
        /// send/fallback path so the message still arrives.
        /// </summary>
        public bool RichDraftDisabled { get; set; }

        public void Reset()
        {
            MessageId = null;
            PendingCharacterCount = 0;
            LastFlushUtc = DateTimeOffset.UtcNow;
            Buffer.Clear();
            RichDraftId = null;
            HasRichDraft = false;
            RichDraftDisabled = false;
        }
    }
}
