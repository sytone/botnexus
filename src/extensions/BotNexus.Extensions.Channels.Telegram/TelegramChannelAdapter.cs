using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Domain.Gateway.Models;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Channels.Startup;
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
    IConfiguration? configuration = null,
    IAskUserPromptResolver? promptResolver = null) : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private const int StreamingFlushThresholdChars = 100;

    // Fallbacks used when a bot config carries a non-positive bound (misconfiguration must not mean
    // "unbounded"). Rationale for the values lives on TelegramBotConfig.MaxMediaBytes and
    // TelegramBotConfig.MediaDownloadTimeoutSeconds.
    private const long DefaultMaxMediaBytes = 20L * 1024 * 1024;
    private const int DefaultMediaDownloadTimeoutSeconds = 30;

    private readonly ILogger<TelegramChannelAdapter> _logger = logger;
    private readonly LateBoundChannelOptions<TelegramGatewayOptions> _optionsHolder =
        new(() => ResolveOptions(optionsAccessor, configuration), configuration);

    // Read at point of use so a runtime config.json edit is reflected without a gateway restart (#2010).
    private TelegramGatewayOptions _options => _optionsHolder.Current;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IAskUserPromptResolver? _promptResolver = promptResolver;
    private readonly ConcurrentDictionary<string, BotRuntime> _bots = new(StringComparer.OrdinalIgnoreCase);

    // Monotonic source of short callback tokens. The Bot API caps callback_data at 64 bytes, so the
    // button carries a token + ordinal rather than the choice value itself (see TelegramPromptKeyboard).
    private long _promptTokenCounter;

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

    /// <summary>
    /// Telegram renders <c>ask_user</c> prompts as inline keyboards and receives taps as callback
    /// queries (#2323), so it is a fully interactive prompt surface rather than a text-degraded one.
    /// </summary>
    /// <remarks>
    /// Reported unconditionally: the capability describes the transport, not a per-prompt decision.
    /// A prompt with too many choices to draw still degrades to the shared numbered-text rendering,
    /// which is a rendering fallback inside an interactive channel, not a loss of the capability.
    /// </remarks>
    public override bool SupportsInteractivePrompts => true;

    /// <summary>
    /// Telegram chats are a user-visible surface, so the delimited internal runtime-context
    /// envelope is redacted from outbound text before delivery (#1430).
    /// </summary>
    protected override bool StripsRuntimeContext => true;

    /// <summary>
    /// Starts every configured bot that is not already started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Resumable by construction (#2447).</b> Bots are started in sequence, so a failure on the
    /// Nth bot used to abort the whole method while bots 1..N-1 were already polling. The gateway
    /// then recorded the adapter as failed even though live pollers remained running. Retrying
    /// that start would have launched a <em>second</em> <c>getUpdates</c> loop on an already-live
    /// bot token - precisely the duplicate-poller condition behind the 2026-07-24 HTTP 409 storm.
    /// </para>
    /// <para>
    /// Each bot runtime therefore owns a one-way start latch. A bot that has already started is
    /// skipped on every subsequent call, so a retry only starts the bots that never got going.
    /// The latch is the guarantee - not the call ordering - and it is released only by
    /// <see cref="OnStopAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Shutdown token.</param>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        EnsureBotsInitialized();

        foreach (var runtime in _bots.Values)
        {
            // Already live from a previous (partially successful) start attempt - never restart it.
            if (!runtime.TryBeginStart())
            {
                _logger.LogDebug(
                    "{DisplayName} bot '{BotName}' is already started; skipping (resumed start).",
                    DisplayName,
                    runtime.BotName);
                continue;
            }

            var committed = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(runtime.Config.WebhookUrl))
                {
                    await runtime.ApiClient.SetWebhookAsync(runtime.Config.WebhookUrl, runtime.WebhookSecret, cancellationToken);
                    committed = true;
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
                committed = true;

                _logger.LogInformation(
                    "{DisplayName} bot '{BotName}' polling mode started (AgentId: {AgentId}, AllowedChatCount: {AllowedChatCount}, PollingTimeoutSeconds: {PollingTimeoutSeconds})",
                    DisplayName,
                    runtime.BotName,
                    runtime.Config.AgentId ?? "<default-router>",
                    runtime.Config.AllowedChatIds.Count,
                    Math.Max(1, runtime.Config.PollingTimeoutSeconds));
            }
            finally
            {
                // This bot never reached a live state, so release its latch and let a later retry
                // try again. Bots that DID commit keep the latch and are skipped on retry.
                if (!committed)
                    runtime.AbandonStart();
            }
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
            // Pending prompts are adapter-local render state only; the durable checkpoint survives
            // restart independently, so dropping them here cannot lose an answerable prompt.
            runtime.PendingPrompts.Clear();

            // Release the start latch so a legitimate restart is not silently a no-op.
            runtime.AbandonStart();
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
            var richMarkdown = BuildOutboundMarkdown(ProjectOutboundText(message.Content), message.Metadata, message.DisplayPrefix);
            try
            {
                foreach (var chunk in TelegramMessageSplitter.SplitMarkdown(richMarkdown, Math.Max(1, runtime.Config.MaxRichMessageLength)))
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
        var formatted = BuildOutboundText(ProjectOutboundText(message.Content), message.Metadata, message.DisplayPrefix);
        foreach (var chunk in TelegramMessageSplitter.SplitMessage(formatted, Math.Max(1, runtime.Config.MaxMessageLength)))
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
                    // Tool annotations are delivered as their OWN dedicated message, NOT appended to the
                    // streaming content buffer. The buffer is reset on every MessageStart and the whole
                    // StreamingState is torn down (TryRemove) at MessageEnd -- and in the agent loop the
                    // sequence for a tool-using turn is [MessageEnd of the preamble] -> [ToolStart/ToolEnd]
                    // -> [MessageStart of the follow-up]. So a tool line buffered here would be wiped by the
                    // follow-up's MessageStart before it was ever flushed to a real message (the "tool
                    // messages eaten by the next assistant message" bug). Sending it immediately as a
                    // standalone message decouples it from the resettable buffer's lifecycle entirely.
                    if (!runtime.Config.ShowToolActivity)
                        break;
                    var toolName = streamEvent.ToolName ?? streamEvent.ToolCallId ?? "tool";
                    // Escape the tool name for MarkdownV2 (status messages are sent with that parse mode);
                    // names like memory_save contain underscores that would otherwise be read as italics.
                    await SendToolActivityAsync(runtime, state, $"{ToolGlyphs.ForTool(streamEvent.ToolName)} {TelegramMarkdownFormatter.EscapeMarkdownV2(toolName)}", cancellationToken);
                    break;
                }

                case AgentStreamEventType.ToolEnd:
                {
                    // See ToolStart: delivered as a standalone message so it survives the MessageStart/
                    // MessageEnd state churn that surrounds a tool cycle.
                    if (!runtime.Config.ShowToolActivity)
                        break;
                    var glyph = streamEvent.ToolIsError == true ? "\u26A0\uFE0F" : ToolGlyphs.ForTool(streamEvent.ToolName);
                    var status = streamEvent.ToolIsError == true ? "failed" : "done";
                    var toolName = streamEvent.ToolName ?? streamEvent.ToolCallId ?? "tool";
                    await SendToolActivityAsync(runtime, state, $"{glyph} {TelegramMarkdownFormatter.EscapeMarkdownV2(toolName)} {status}", cancellationToken);
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

                case AgentStreamEventType.UserInputRequired:
                    // The agent is BLOCKED until this resolves, so the prompt must be delivered as its
                    // own message immediately rather than buffered: the streaming buffer is reset at
                    // MessageStart and torn down at MessageEnd, and a prompt that never renders leaves
                    // the user staring at a turn that appears to hang (#2323). Any pending content is
                    // force-flushed first so the question appears after the text that led up to it.
                    await SendUserInputPromptAsync(runtime, state, streamEvent, cancellationToken);
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
        var breaker = new ChannelLoopCircuitBreaker($"Telegram bot '{runtime.BotName}' polling loop");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await runtime.ApiClient.GetUpdatesAsync(offset, pollingTimeoutSeconds, cancellationToken);
                breaker.RecordSuccess();
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
                // #2386: classify before retrying. A terminal fault (HTTP 409 "terminated by other
                // getUpdates request", a revoked token) cannot clear by retrying - hot-looping on it
                // produced thousands of ERR lines while the transport was already dead.
                var response = breaker.RecordFailure(ex);
                if (response.ShouldStop)
                {
                    if (response.CircuitOpened)
                    {
                        _logger.LogError(
                            ex,
                            // #3116: the cause list must enumerate only what the classifier can
                            // actually route here, and must not imply the list is exhaustive - an
                            // unrecognised exception type fails closed to Terminal, so "or an
                            // unrecognised fault" is a reachable case. Naming three credential/config
                            // causes for a transport fault sent an operator to check the bot token
                            // when the transport had merely timed out.
                            "{DisplayName} bot '{BotName}' polling loop is DEGRADED and has stopped: a non-transient failure was detected and will not clear by retrying. Likely causes are a duplicate poller (HTTP 409), a revoked or invalid bot token (HTTP 401/403), invalid configuration, or an unrecognised fault that the classifier fails closed on - see the exception below for which. Resolve the underlying fault and restart the channel.",
                            DisplayName,
                            runtime.BotName);
                    }

                    break;
                }

                _logger.LogWarning(
                    ex,
                    "{DisplayName} bot '{BotName}' polling loop transient error (failure {FailureCount}); retrying in {RetryDelaySeconds}s",
                    DisplayName,
                    runtime.BotName,
                    breaker.ConsecutiveTransientFailures,
                    response.RetryDelay.TotalSeconds);

                try
                {
                    await Task.Delay(response.RetryDelay, cancellationToken);
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
        // Inline-keyboard taps arrive as their own update kind, not as a message, and are handled on
        // a dedicated path that applies the identical chat/user authorization checks (#2323).
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQueryAsync(runtime, callbackQuery, cancellationToken);
            return;
        }

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
            var maxMediaBytes = runtime.Config.MaxMediaBytes > 0
                ? runtime.Config.MaxMediaBytes
                : DefaultMaxMediaBytes;

            // Cheapest possible rejection first: if the sender's own advertised size already blows the
            // cap, skip the media without spending a single Bot API call on it. The advertised value
            // is remote-supplied, so it is trusted only to REJECT, never to admit - the client
            // re-enforces the same ceiling while reading the body (issue #2724).
            if (largestPhoto.FileSize is { } advertisedSize && advertisedSize > maxMediaBytes)
            {
                _logger.LogWarning(
                    "bot '{BotName}' skipped photo for updateId={UpdateId}: advertised size {AdvertisedBytes} exceeds the {MaxMediaBytes}-byte cap; proceeding with caption only",
                    runtime.BotName,
                    update.UpdateId,
                    advertisedSize,
                    maxMediaBytes);
            }
            else
            {
                // A size cap alone cannot bound a slow-drip response, so the download also gets a
                // wall-clock budget. Linked (not standalone) so host shutdown still cancels promptly.
                var timeoutSeconds = runtime.Config.MediaDownloadTimeoutSeconds > 0
                    ? runtime.Config.MediaDownloadTimeoutSeconds
                    : DefaultMediaDownloadTimeoutSeconds;
                using var mediaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                mediaCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                try
                {
                    var file = await runtime.ApiClient.GetFileAsync(largestPhoto.FileId, mediaCts.Token);
                    if (!string.IsNullOrWhiteSpace(file.FilePath))
                    {
                        var imageBytes = await runtime.ApiClient.DownloadFileAsync(file.FilePath, maxMediaBytes, mediaCts.Token);
                        contentParts = [new BinaryContentPart { MimeType = "image/jpeg", Data = imageBytes }];
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Media budget expired, not host shutdown. Fail safe: drop the attachment, keep
                    // the message.
                    _logger.LogWarning(
                        "bot '{BotName}' photo download for updateId={UpdateId} exceeded the {TimeoutSeconds}s budget and was cancelled; proceeding with caption only",
                        runtime.BotName,
                        update.UpdateId,
                        timeoutSeconds);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "bot '{BotName}' failed to download photo for updateId={UpdateId}; proceeding with caption only", runtime.BotName, update.UpdateId);
                }
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
            var rawChunk = TelegramMessageSplitter.DrainStreamingBuffer(state.Buffer, maxLength);
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
            foreach (var chunk in TelegramMessageSplitter.SplitMarkdown(rawText, Math.Max(1, runtime.Config.MaxRichMessageLength)))
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
            foreach (var chunk in TelegramMessageSplitter.SplitMessage(formatted, Math.Max(1, runtime.Config.MaxMessageLength)))
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

        // Multi-bot, no explicit telegramBotName: degrade gracefully instead of dropping the reply.
        // The fan-out / non-streaming OutboundMessage does not carry the bot name, but the target
        // chat is still scoped to whichever bot's allow-list admits it. If exactly one configured
        // bot allows this chat, route through it (mirrors ResolveStreamingBot's agent-based
        // fallback). This is what keeps multi-bot Telegram DMs delivering replies -- see #1681.
        if (TelegramChannelAddress.TryDecode(message.ChannelAddress, out var chatId, out _))
        {
            var allowed = _bots.Values
                .Where(b => IsChatAllowed(b.Config, chatId))
                .ToList();

            if (allowed.Count == 1)
                return allowed[0];
        }

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
    /// Sends a tool-activity status line (e.g. "\U0001F4C4 read done") as its own standalone Telegram
    /// message, so the user can watch tool execution as the agent runs.
    /// </summary>
    /// <remarks>
    /// This intentionally bypasses the streaming content buffer. Tool events arrive at the seams
    /// between assistant messages, where the buffer is reset (MessageStart) and the StreamingState is
    /// removed (MessageEnd); a buffered tool line would be eaten by the follow-up message before it
    /// was ever delivered. Before sending, any pending buffered content is force-flushed so the tool
    /// status appears AFTER the preamble that preceded it (preserving chronological order). The glyph
    /// is supplied by the caller from the cross-channel <see cref="ToolGlyphs"/> map. Sent as plain
    /// text (no markdown parsing) since the line is a short fixed-format status, not model output.
    /// Failures are swallowed: a dropped status line must never abort the agent run or the reply.
    /// </remarks>
    private async Task SendToolActivityAsync(BotRuntime runtime, StreamingState state, string line, CancellationToken cancellationToken)
    {
        // Flush any preamble content first so ordering stays chronological (content -> tool status).
        if (state.Buffer.Length > 0)
        {
            try
            {
                await FlushStreamingStateAsync(runtime, state, force: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{DisplayName} bot '{BotName}' failed to flush pending content before tool-activity message for chat {ChatId}", DisplayName, runtime.BotName, state.ChatId);
            }
        }

        try
        {
            await runtime.ApiClient.SendMessageAsync(state.ChatId, line, state.MessageThreadId, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort: never let a tool-status line break the run or the reply.
            _logger.LogDebug(ex, "{DisplayName} bot '{BotName}' failed to send tool-activity message for chat {ChatId}", DisplayName, runtime.BotName, state.ChatId);
        }
    }

    private static void AppendLineIfNeeded(StringBuilder builder)
    {
        if (builder.Length > 0)
            builder.AppendLine();
    }

    // ── ask_user interactive prompts (#2323) ─────────────────────────────────

    /// <summary>
    /// Renders a <c>UserInputRequired</c> event as a visible Telegram message: an inline keyboard
    /// when the prompt carries a workable number of choices, otherwise the shared numbered-text
    /// rendering that a user can answer by typing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pending prompt is recorded against the bot runtime before the send, keyed by a short token
    /// carried in each button's <c>callback_data</c>. That indirection exists because the Bot API caps
    /// <c>callback_data</c> at 64 bytes: packing the request id and choice value into the button would
    /// make delivery depend on how verbose the agent's labels are.
    /// </para>
    /// <para>
    /// Failures are logged and swallowed rather than propagated. The prompt is already blocked on a
    /// human; throwing here would additionally fault the stream and lose the surrounding turn.
    /// </para>
    /// </remarks>
    private async Task SendUserInputPromptAsync(
        BotRuntime runtime,
        StreamingState state,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        if (streamEvent.UserInputRequest is not { } request)
        {
            _logger.LogWarning(
                "{DisplayName} bot '{BotName}' received a UserInputRequired event with no request payload; nothing to render",
                DisplayName,
                runtime.BotName);
            return;
        }

        var prompt = request.ToPrompt();

        // Preserve chronological order: whatever the agent said before asking goes out first.
        if (state.Buffer.Length > 0)
        {
            try
            {
                await FlushStreamingStateAsync(runtime, state, force: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{DisplayName} bot '{BotName}' failed to flush pending content before the ask_user prompt for chat {ChatId}",
                    DisplayName,
                    runtime.BotName,
                    state.ChatId);
            }
        }

        var token = NextPromptToken();
        var renderKeyboard = TelegramPromptKeyboard.ShouldRenderKeyboard(prompt);

        // Too many choices to draw as buttons: fall back to the numbered text list every channel
        // shares, which the free-text interception path can still resolve. Degrading beats failing
        // the send and leaving the user with no prompt at all.
        var text = renderKeyboard ? prompt.Prompt : AskUserPromptTextRenderer.Render(prompt);
        var keyboard = renderKeyboard ? TelegramPromptKeyboard.BuildKeyboard(prompt, token) : null;

        try
        {
            var sent = await runtime.ApiClient.SendMessageWithKeyboardAsync(
                state.ChatId,
                text,
                keyboard,
                state.MessageThreadId,
                cancellationToken);

            if (renderKeyboard)
            {
                runtime.PendingPrompts[token] = new PendingPrompt(
                    prompt,
                    state.ChatId,
                    sent.MessageId,
                    state.MessageThreadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{DisplayName} bot '{BotName}' failed to deliver the ask_user prompt for chat {ChatId}; the prompt will resolve only by timeout or a typed reply",
                DisplayName,
                runtime.BotName,
                state.ChatId);
        }
    }

    /// <summary>
    /// Handles an inline-keyboard tap: authorizes it, resolves the prompt through the shared gateway
    /// seam, and rewrites the prompt message so it cannot be answered twice (#2323).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Authorization is not optional here.</b> A callback query is an inbound event from a user and
    /// is run through the same <c>AllowedChatIds</c>/<c>AllowedUserIds</c> checks as a text message.
    /// A button is visible to everyone in a group, so without this an unauthorized member of an
    /// allowed chat could answer on the operator's behalf.
    /// </para>
    /// <para>
    /// <b>Idempotency has two layers.</b> The token is removed from the pending map with an atomic
    /// <c>TryRemove</c>, so only one of two racing taps proceeds; and resolution itself goes through
    /// <see cref="IAskUserPromptResolver"/>, which returns <c>NoPendingPrompt</c> for an already
    /// answered prompt. The adapter never touches the response registry directly.
    /// </para>
    /// </remarks>
    private async Task HandleCallbackQueryAsync(
        BotRuntime runtime,
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        // Not one of ours (or malformed): acknowledge so the client stops spinning, then ignore.
        if (!TelegramPromptKeyboard.TryParseCallbackData(callback.Data, out var token, out var kind, out var choiceIndex))
        {
            await AcknowledgeCallbackAsync(runtime, callback.Id, text: null, cancellationToken);
            return;
        }

        var chatId = callback.Message?.Chat?.Id;
        if (chatId is null || !IsChatAllowed(runtime.Config, chatId.Value))
        {
            _logger.LogDebug(
                "{DisplayName} bot '{BotName}' ignored callback query from unauthorized chat {ChatId}",
                DisplayName,
                runtime.BotName,
                chatId);
            await AcknowledgeCallbackAsync(runtime, callback.Id, "Not authorized.", cancellationToken);
            return;
        }

        if (callback.From is null || !IsUserAllowed(runtime.Config, callback.From.Id))
        {
            _logger.LogDebug(
                "{DisplayName} bot '{BotName}' ignored callback query from unauthorized user {UserId}",
                DisplayName,
                runtime.BotName,
                callback.From?.Id);
            await AcknowledgeCallbackAsync(runtime, callback.Id, "Not authorized.", cancellationToken);
            return;
        }

        if (!runtime.PendingPrompts.TryGetValue(token, out var pending))
        {
            // Already answered, or from a prompt this process never issued (e.g. after a restart).
            // Acknowledging is the whole response: a stale tap must never resolve anything.
            await AcknowledgeCallbackAsync(runtime, callback.Id, "This prompt has already been answered.", cancellationToken);
            return;
        }

        // Multi-select accumulates until the user explicitly submits, so a choice tap only updates the
        // keyboard - the prompt stays pending and tappable by design.
        if (kind == TelegramPromptCallbackKind.Choice && pending.Prompt.AllowMultiple)
        {
            await ToggleMultiSelectAsync(runtime, callback, token, pending, choiceIndex, cancellationToken);
            return;
        }

        // Terminal from here: claim the token atomically so a double-tap (or a tap racing a typed
        // answer) can only be claimed once.
        if (!runtime.PendingPrompts.TryRemove(token, out pending))
        {
            await AcknowledgeCallbackAsync(runtime, callback.Id, "This prompt has already been answered.", cancellationToken);
            return;
        }

        var cancelled = kind == TelegramPromptCallbackKind.Cancel;
        IReadOnlyList<string>? selectedValues = null;

        if (!cancelled)
        {
            selectedValues = kind switch
            {
                TelegramPromptCallbackKind.Submit => pending.Selected.ToArray(),
                TelegramPromptCallbackKind.Choice when TryGetChoiceValue(pending.Prompt, choiceIndex, out var value) => [value],
                _ => null
            };

            if (selectedValues is null)
            {
                // A choice ordinal that does not exist on the prompt - a stale keyboard from a prompt
                // that has since changed. Put the prompt back so a valid tap can still answer it.
                runtime.PendingPrompts.TryAdd(token, pending);
                await AcknowledgeCallbackAsync(runtime, callback.Id, "That option is no longer available.", cancellationToken);
                return;
            }
        }

        await AcknowledgeCallbackAsync(runtime, callback.Id, cancelled ? "Cancelled." : "Answer recorded.", cancellationToken);

        var resolved = await ResolvePromptAsync(runtime, pending.Prompt, selectedValues, cancelled, cancellationToken);

        // Rewrite the message and drop the keyboard whether or not the gateway still had a waiter:
        // a prompt that was resolved elsewhere is equally stale on this surface and must stop
        // presenting tappable buttons.
        await FinalizePromptMessageAsync(runtime, pending, selectedValues, cancelled, cancellationToken);

        if (!resolved)
        {
            _logger.LogDebug(
                "{DisplayName} bot '{BotName}' callback for request {RequestId} found no pending prompt to resolve (already answered or expired)",
                DisplayName,
                runtime.BotName,
                pending.Prompt.RequestId);
        }
    }

    /// <summary>
    /// Toggles one value in a multi-select prompt and redraws the keyboard with check marks, leaving
    /// the prompt pending until the user taps Submit.
    /// </summary>
    private async Task ToggleMultiSelectAsync(
        BotRuntime runtime,
        TelegramCallbackQuery callback,
        string token,
        PendingPrompt pending,
        int choiceIndex,
        CancellationToken cancellationToken)
    {
        if (!TryGetChoiceValue(pending.Prompt, choiceIndex, out var value))
        {
            await AcknowledgeCallbackAsync(runtime, callback.Id, "That option is no longer available.", cancellationToken);
            return;
        }

        lock (pending.SelectionLock)
        {
            if (!pending.Selected.Remove(value))
                pending.Selected.Add(value);
        }

        await AcknowledgeCallbackAsync(runtime, callback.Id, null, cancellationToken);

        string[] snapshot;
        lock (pending.SelectionLock)
            snapshot = [.. pending.Selected];

        try
        {
            await runtime.ApiClient.EditMessageTextWithKeyboardAsync(
                pending.ChatId,
                pending.MessageId,
                pending.Prompt.Prompt,
                TelegramPromptKeyboard.BuildKeyboard(pending.Prompt, token, snapshot),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // A failed redraw only costs the check marks; the accumulated selection is authoritative
            // in memory and Submit still resolves with it.
            _logger.LogDebug(
                ex,
                "{DisplayName} bot '{BotName}' failed to redraw the multi-select keyboard for chat {ChatId}",
                DisplayName,
                runtime.BotName,
                pending.ChatId);
        }
    }

    /// <summary>
    /// Resolves the prompt through the shared channel-agnostic gateway seam (#2322). The adapter
    /// deliberately owns no registry access of its own - that duplication is the exact thing the seam
    /// was introduced to remove.
    /// </summary>
    private async Task<bool> ResolvePromptAsync(
        BotRuntime runtime,
        AskUserPrompt prompt,
        IReadOnlyList<string>? selectedValues,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        if (_promptResolver is null)
        {
            _logger.LogWarning(
                "{DisplayName} bot '{BotName}' cannot resolve ask_user prompt {RequestId}: no IAskUserPromptResolver is registered",
                DisplayName,
                runtime.BotName,
                prompt.RequestId);
            return false;
        }

        if (prompt.ToConversationId() is not { } conversationId)
        {
            _logger.LogWarning(
                "{DisplayName} bot '{BotName}' cannot resolve ask_user prompt {RequestId}: the prompt carries no conversation id",
                DisplayName,
                runtime.BotName,
                prompt.RequestId);
            return false;
        }

        try
        {
            var result = await _promptResolver.ResolveAsync(
                new AskUserSubmission
                {
                    ConversationId = conversationId,
                    RequestId = prompt.RequestId,
                    SelectedValues = cancelled ? null : selectedValues,
                    Cancelled = cancelled,
                    OriginChannel = ChannelType
                },
                cancellationToken);

            return result.Succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{DisplayName} bot '{BotName}' failed to resolve ask_user prompt {RequestId}",
                DisplayName,
                runtime.BotName,
                prompt.RequestId);
            return false;
        }
    }

    /// <summary>
    /// Edits the prompt message to show the answer and removes its keyboard, so chat history records
    /// what was chosen and the buttons cannot be tapped again.
    /// </summary>
    private async Task FinalizePromptMessageAsync(
        BotRuntime runtime,
        PendingPrompt pending,
        IReadOnlyList<string>? selectedValues,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        try
        {
            await runtime.ApiClient.EditMessageTextWithKeyboardAsync(
                pending.ChatId,
                pending.MessageId,
                TelegramPromptKeyboard.RenderResolvedText(pending.Prompt, selectedValues, cancelled),
                // Null reply_markup strips the keyboard.
                replyMarkup: null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Cosmetic only - the prompt is already resolved and the token already claimed, so a
            // failed edit cannot produce a second resolution.
            _logger.LogDebug(
                ex,
                "{DisplayName} bot '{BotName}' failed to finalize the resolved prompt message in chat {ChatId}",
                DisplayName,
                runtime.BotName,
                pending.ChatId);
        }
    }

    /// <summary>
    /// Answers a callback query so Telegram stops showing the client-side spinner. Best-effort: this
    /// is cosmetic and must never fail a resolution that already succeeded.
    /// </summary>
    private async Task AcknowledgeCallbackAsync(
        BotRuntime runtime,
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken)
    {
        try
        {
            await runtime.ApiClient.AnswerCallbackQueryAsync(callbackQueryId, text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "{DisplayName} bot '{BotName}' failed to answer callback query {CallbackQueryId}",
                DisplayName,
                runtime.BotName,
                callbackQueryId);
        }
    }

    private static bool TryGetChoiceValue(AskUserPrompt prompt, int index, out string value)
    {
        value = string.Empty;
        if (prompt.Choices is not { } choices || index < 0 || index >= choices.Count)
            return false;

        value = choices[index].Value;
        return true;
    }

    private string NextPromptToken()
        => Interlocked.Increment(ref _promptTokenCounter).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the number of prompts currently awaiting a button tap, across all bot runtimes.
    /// Used by tests to assert a resolved prompt is actually forgotten.
    /// </summary>
    internal int GetPendingPromptCount()
        => _bots.Values.Sum(r => r.PendingPrompts.Count);

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

        /// <summary>
        /// Prompts awaiting an inline-keyboard tap, keyed by the short token embedded in each
        /// button's <c>callback_data</c> (#2323).
        /// </summary>
        /// <remarks>
        /// This map holds only what is needed to render and finalize the Telegram message; it is NOT
        /// a second source of truth for whether a prompt is answerable. The durable checkpoint
        /// (#2047) owns that, and every resolution goes through <c>IAskUserPromptResolver</c>. The
        /// atomic <c>TryRemove</c> on this map is a local double-tap guard layered on top of the
        /// gateway's own idempotency, not a replacement for it - which is also why a tap arriving
        /// after a restart is simply acknowledged as stale rather than resolved from adapter memory.
        /// </remarks>
        public ConcurrentDictionary<string, PendingPrompt> PendingPrompts { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource? PollingCancellation { get; set; }
        public Task? PollingTask { get; set; }

        // 0 = not started, 1 = start claimed. Interlocked so the latch holds even if two starts
        // ever race; the guarantee that a live bot token is never polled twice must not depend on
        // the caller happening to be sequential (#2447).
        private int _startClaimed;

        /// <summary>
        /// Atomically claims the right to start this bot. Returns <see langword="false"/> when the
        /// bot is already started (or a start is in flight), in which case the caller MUST skip it.
        /// </summary>
        public bool TryBeginStart() => Interlocked.CompareExchange(ref _startClaimed, 1, 0) == 0;

        /// <summary>
        /// Releases a claim taken by <see cref="TryBeginStart"/> when the start did not reach a
        /// live state, so a later retry may attempt this bot again.
        /// </summary>
        public void AbandonStart() => Interlocked.Exchange(ref _startClaimed, 0);
    }

    /// <summary>
    /// A prompt rendered as an inline keyboard and awaiting a tap (#2323).
    /// </summary>
    /// <param name="Prompt">The normalized prompt, used to map a tapped ordinal back to a choice value.</param>
    /// <param name="ChatId">Chat the prompt message was sent to.</param>
    /// <param name="MessageId">Message id of the prompt, so it can be edited on resolution.</param>
    /// <param name="MessageThreadId">Forum topic thread, when the prompt was sent into one.</param>
    private sealed record PendingPrompt(
        AskUserPrompt Prompt,
        long ChatId,
        int MessageId,
        int? MessageThreadId)
    {
        /// <summary>
        /// Values accumulated so far on a multi-select prompt. Guarded by <see cref="SelectionLock"/>
        /// because two taps on the same keyboard can be processed concurrently by the polling loop.
        /// </summary>
        public List<string> Selected { get; } = [];

        /// <summary>Guards <see cref="Selected"/>.</summary>
        public object SelectionLock { get; } = new();
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
