using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
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
    IHttpClientFactory httpClientFactory) : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private const int StreamingFlushThresholdChars = 100;

    private readonly ILogger<TelegramChannelAdapter> _logger = logger;
    private readonly TelegramGatewayOptions _options = optionsAccessor.Value;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ConcurrentDictionary<string, BotRuntime> _bots = new(StringComparer.OrdinalIgnoreCase);

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
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        EnsureBotsInitialized();

        foreach (var runtime in _bots.Values)
        {
            if (!string.IsNullOrWhiteSpace(runtime.Config.WebhookUrl))
            {
                await runtime.ApiClient.SetWebhookAsync(runtime.Config.WebhookUrl, cancellationToken);
                _logger.LogInformation(
                    "{DisplayName} bot '{BotName}' configured webhook mode at {WebhookUrl}",
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
    /// Content is escaped for Telegram HTML parse mode.
    /// </summary>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();

        if (!TryParseChatId(message.ChannelAddress, out var chatId))
        {
            _logger.LogWarning(
                "{DisplayName} send requested with invalid conversation id '{ConversationId}'",
                DisplayName,
                message.ChannelAddress);
            return;
        }

        var runtime = ResolveOutboundBot(message);
        EnsureChatAllowed(runtime.Config, chatId);
        var formatted = BuildOutboundText(message.Content, message.Metadata, message.DisplayPrefix);

        int? threadId = null;
        if (!string.IsNullOrEmpty(message.ThreadId) && int.TryParse(message.ThreadId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedThreadId))
            threadId = parsedThreadId;

        foreach (var chunk in SplitMessage(formatted, Math.Max(1, runtime.Config.MaxMessageLength)))
            await runtime.ApiClient.SendMessageAsync(chatId, chunk, threadId, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();
        if (string.IsNullOrEmpty(delta) || !TryParseChatId(conversationId, out var chatId))
            return;

        var runtime = ResolveSingleConfiguredBot();
        EnsureChatAllowed(runtime.Config, chatId);
        var state = runtime.StreamingStates.GetOrAdd(conversationId, _ => new StreamingState(chatId));

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            state.Buffer.Append(EscapeHtml(delta));
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
    public async Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBotsInitialized();
        if (!TryParseChatId(conversationId, out var chatId))
            return;

        var runtime = ResolveSingleConfiguredBot();
        EnsureChatAllowed(runtime.Config, chatId);
        var state = runtime.StreamingStates.GetOrAdd(conversationId, _ => new StreamingState(chatId));

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            switch (streamEvent.Type)
            {
                case AgentStreamEventType.MessageStart:
                    state.Reset();
                    break;

                case AgentStreamEventType.ContentDelta when streamEvent.ContentDelta is not null:
                    state.Buffer.Append(EscapeHtml(streamEvent.ContentDelta));
                    state.PendingCharacterCount += streamEvent.ContentDelta.Length;
                    break;

                case AgentStreamEventType.ThinkingDelta when streamEvent.ThinkingContent is not null:
                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("Thinking: ");
                    state.Buffer.Append(EscapeHtml(streamEvent.ThinkingContent));
                    state.PendingCharacterCount += streamEvent.ThinkingContent.Length;
                    break;

                case AgentStreamEventType.ToolStart:
                {
                    var toolName = streamEvent.ToolName ?? "tool";
                    AppendLineIfNeeded(state.Buffer);
                    state.Buffer.Append("[");
                    state.Buffer.Append(EscapeHtml(toolName));
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
                    state.Buffer.Append(EscapeHtml(toolName));
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
                    state.Buffer.Append(EscapeHtml(streamEvent.ErrorMessage));
                    state.PendingCharacterCount += streamEvent.ErrorMessage.Length + 2;
                    break;

                case AgentStreamEventType.MessageEnd:
                    await FlushStreamingStateAsync(runtime, state, force: true, cancellationToken);
                    state.Reset();
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
        var message = update.Message ?? update.EditedMessage ?? update.ChannelPost;
        if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
            return;

        var chatId = message.Chat.Id;
        if (!IsChatAllowed(runtime.Config, chatId))
        {
            _logger.LogDebug("{DisplayName} bot '{BotName}' ignored message from unauthorized chat {ChatId}", DisplayName, runtime.BotName, chatId);
            return;
        }

        var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);
        var senderId = (message.From?.Id ?? chatId).ToString(CultureInfo.InvariantCulture);

        await DispatchInboundAsync(new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            ChannelAddress = chatIdText,
            Content = message.Text,
            TargetAgentId = string.IsNullOrWhiteSpace(runtime.Config.AgentId) ? null : runtime.Config.AgentId,
            ThreadId = message.MessageThreadId.HasValue
                ? message.MessageThreadId.Value.ToString(CultureInfo.InvariantCulture)
                : null,
            Metadata = new Dictionary<string, object?>
            {
                ["telegramBotName"] = runtime.BotName,
                ["telegramUpdateId"] = update.UpdateId,
                ["telegramMessageId"] = message.MessageId,
                ["telegramChatId"] = chatId
            }
        }, cancellationToken);
    }

    private async Task FlushStreamingStateAsync(BotRuntime runtime, StreamingState state, bool force, CancellationToken cancellationToken)
    {
        if (state.Buffer.Length == 0)
            return;

        var maxLength = Math.Max(1, runtime.Config.MaxMessageLength);
        while (state.Buffer.Length > maxLength)
        {
            var chunk = state.Buffer.ToString(0, maxLength);
            await SendOrEditStreamingMessageAsync(runtime, state, chunk, cancellationToken);
            state.MessageId = null;
            state.Buffer.Remove(0, maxLength);
        }

        if (!force && !ShouldFlush(state, runtime.Config))
            return;

        var text = state.Buffer.ToString();
        if (string.IsNullOrEmpty(text))
            return;

        await SendOrEditStreamingMessageAsync(runtime, state, text, cancellationToken);
        state.LastFlushUtc = DateTimeOffset.UtcNow;
        state.PendingCharacterCount = 0;
    }

    private async Task SendOrEditStreamingMessageAsync(BotRuntime runtime, StreamingState state, string text, CancellationToken cancellationToken)
    {
        if (state.MessageId is null)
        {
            var sent = await runtime.ApiClient.SendMessageAsync(state.ChatId, text, cancellationToken);
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

            _bots.TryAdd(botName, new BotRuntime(botName, config, client));
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
        => long.TryParse(conversationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId);

    private static bool IsChatAllowed(TelegramBotConfig config, long chatId)
        => config.AllowedChatIds.Count == 0 || config.AllowedChatIds.Contains(chatId);

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
            builder.Append(EscapeHtml(displayPrefix));
            builder.Append(' ');
        }

        if (TryGetMetadataString(metadata, "thinking", out var thinking))
        {
            builder.Append("Thinking: ");
            builder.Append(EscapeHtml(thinking));
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(EscapeHtml(content));

        if (TryGetMetadataString(metadata, "toolCall", out var toolCall))
        {
            builder.AppendLine();
            builder.Append('[');
            builder.Append(EscapeHtml(toolCall));
            builder.Append(']');
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

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private sealed class BotRuntime(string botName, TelegramBotConfig config, TelegramBotApiClient apiClient)
    {
        public string BotName { get; } = botName;
        public TelegramBotConfig Config { get; } = config;
        public TelegramBotApiClient ApiClient { get; } = apiClient;
        public ConcurrentDictionary<string, StreamingState> StreamingStates { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<long, DateTimeOffset> LastErrorReplyUtcByChat { get; } = new();
        public CancellationTokenSource? PollingCancellation { get; set; }
        public Task? PollingTask { get; set; }
    }

    private sealed class StreamingState(long chatId)
    {
        public long ChatId { get; } = chatId;
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
