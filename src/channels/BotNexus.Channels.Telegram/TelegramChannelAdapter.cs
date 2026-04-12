using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace BotNexus.Channels.Telegram;

/// <summary>
/// Telegram Bot channel adapter.
/// </summary>
public sealed class TelegramChannelAdapter(
    ILogger<TelegramChannelAdapter> logger,
    IOptions<TelegramOptions> optionsAccessor,
    TelegramBotApiClient apiClient) : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private const int StreamingFlushThresholdChars = 100;

    private readonly ILogger<TelegramChannelAdapter> _logger = logger;
    private readonly TelegramOptions _options = optionsAccessor.Value;
    private readonly TelegramBotApiClient _apiClient = apiClient;
    private readonly ConcurrentDictionary<string, StreamingState> _streamingStates = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pollingCancellation;
    private Task? _pollingTask;

    /// <summary>
    /// Gets the channel type identifier.
    /// </summary>
    public override ChannelKey ChannelType => ChannelKey.From("telegram");

    /// <summary>
    /// Gets the human-readable channel display name.
    /// </summary>
    public override string DisplayName => "Telegram Bot";

    /// <summary>
    /// Gets a value indicating whether this channel supports streaming deltas.
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
        if (string.IsNullOrWhiteSpace(_options.BotToken))
            throw new InvalidOperationException("Telegram channel requires BotToken.");

        if (!string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            await _apiClient.SetWebhookAsync(_options.WebhookUrl, cancellationToken);
            _logger.LogInformation(
                "{DisplayName} configured webhook mode at {WebhookUrl}",
                DisplayName,
                _options.WebhookUrl);
            return;
        }

        await _apiClient.DeleteWebhookAsync(cancellationToken);

        _pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = Task.Run(
            () => RunPollingLoopAsync(Math.Max(1, _options.PollingTimeoutSeconds), _pollingCancellation.Token),
            CancellationToken.None);
        _logger.LogInformation(
            "{DisplayName} polling mode started (AllowedChatCount: {AllowedChatCount}, PollingTimeoutSeconds: {PollingTimeoutSeconds})",
            DisplayName,
            _options.AllowedChatIds.Count,
            Math.Max(1, _options.PollingTimeoutSeconds));
    }

    /// <inheritdoc />
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        _pollingCancellation?.Cancel();
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _pollingCancellation?.Dispose();
        _pollingCancellation = null;
        _pollingTask = null;
        _streamingStates.Clear();
        _logger.LogInformation("{DisplayName} channel adapter stopped", DisplayName);
    }

    /// <summary>
    /// Sends a complete outbound message through the Telegram adapter stub.
    /// </summary>
    /// <param name="message">Outbound message payload.</param>
    /// <param name="cancellationToken">Cancellation token for send operations.</param>
    /// <returns>A task that completes when all Telegram message chunks have been sent.</returns>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseChatId(message.ConversationId, out var chatId))
        {
            _logger.LogWarning(
                "{DisplayName} send requested with invalid conversation id '{ConversationId}'",
                DisplayName,
                message.ConversationId);
            return;
        }

        EnsureChatAllowed(chatId);
        var formatted = BuildOutboundText(message.Content, message.Metadata);
        foreach (var chunk in SplitMessage(formatted, Math.Max(1, _options.MaxMessageLength)))
            await _apiClient.SendMessageAsync(chatId, chunk, cancellationToken);
    }

    public override async Task SendStreamDeltaAsync(
        string conversationId,
        string delta,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(delta))
            return;

        if (!TryParseChatId(conversationId, out var chatId))
            return;

        EnsureChatAllowed(chatId);
        var state = _streamingStates.GetOrAdd(conversationId, _ => new StreamingState(chatId));
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            state.Buffer.Append(EscapeMarkdownV2(delta));
            state.PendingCharacterCount += delta.Length;
            if (ShouldFlush(state))
                await FlushStreamingStateAsync(state, force: false, cancellationToken);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task SendStreamEventAsync(
        string conversationId,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseChatId(conversationId, out var chatId))
            return;

        EnsureChatAllowed(chatId);
        var state = _streamingStates.GetOrAdd(conversationId, _ => new StreamingState(chatId));
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            switch (streamEvent.Type)
            {
                case AgentStreamEventType.MessageStart:
                    state.Reset();
                    break;
                case AgentStreamEventType.ContentDelta when streamEvent.ContentDelta is not null:
                    state.Buffer.Append(EscapeMarkdownV2(streamEvent.ContentDelta));
                    state.PendingCharacterCount += streamEvent.ContentDelta.Length;
                    break;
                case AgentStreamEventType.ThinkingDelta when streamEvent.ThinkingContent is not null:
                    state.Buffer.AppendLine();
                    state.Buffer.Append("_Thinking:_ ");
                    state.Buffer.Append(EscapeMarkdownV2(streamEvent.ThinkingContent));
                    state.PendingCharacterCount += streamEvent.ThinkingContent.Length;
                    break;
                case AgentStreamEventType.ToolStart:
                {
                    var toolName = streamEvent.ToolName ?? "tool";
                    state.Buffer.AppendLine();
                    state.Buffer.Append('`');
                    state.Buffer.Append(EscapeInlineCode(toolName));
                    state.Buffer.Append("` started");
                    state.PendingCharacterCount += toolName.Length + 8;
                    break;
                }
                case AgentStreamEventType.ToolEnd:
                {
                    var status = streamEvent.ToolIsError == true ? "failed" : "completed";
                    var toolName = streamEvent.ToolName ?? streamEvent.ToolCallId ?? "tool";
                    state.Buffer.AppendLine();
                    state.Buffer.Append('`');
                    state.Buffer.Append(EscapeInlineCode(toolName));
                    state.Buffer.Append("` ");
                    state.Buffer.Append(status);
                    state.PendingCharacterCount += toolName.Length + status.Length + 2;
                    break;
                }
                case AgentStreamEventType.Error when streamEvent.ErrorMessage is not null:
                    state.Buffer.AppendLine();
                    state.Buffer.Append("⚠️ ");
                    state.Buffer.Append(EscapeMarkdownV2(streamEvent.ErrorMessage));
                    state.PendingCharacterCount += streamEvent.ErrorMessage.Length + 2;
                    break;
                case AgentStreamEventType.MessageEnd:
                    await FlushStreamingStateAsync(state, force: true, cancellationToken);
                    state.Reset();
                    return;
            }

            if (ShouldFlush(state))
                await FlushStreamingStateAsync(state, force: false, cancellationToken);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private async Task RunPollingLoopAsync(int pollingTimeoutSeconds, CancellationToken cancellationToken)
    {
        long? offset = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _apiClient.GetUpdatesAsync(offset, pollingTimeoutSeconds, cancellationToken);
                foreach (var update in updates.OrderBy(u => u.UpdateId))
                {
                    offset = update.UpdateId + 1;
                    await HandleUpdateAsync(update, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{DisplayName} polling loop error", DisplayName);
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

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message ?? update.EditedMessage ?? update.ChannelPost;
        if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
            return;

        var chatId = message.Chat.Id;
        if (!IsChatAllowed(chatId))
        {
            _logger.LogDebug("{DisplayName} ignored message from unauthorized chat {ChatId}", DisplayName, chatId);
            return;
        }

        var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);
        var senderId = (message.From?.Id ?? chatId).ToString(CultureInfo.InvariantCulture);

        await DispatchInboundAsync(new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = senderId,
            ConversationId = chatIdText,
            Content = message.Text,
            Metadata = new Dictionary<string, object?>
            {
                ["telegramUpdateId"] = update.UpdateId,
                ["telegramMessageId"] = message.MessageId,
                ["telegramChatId"] = chatId
            }
        }, cancellationToken);
    }

    private async Task FlushStreamingStateAsync(StreamingState state, bool force, CancellationToken cancellationToken)
    {
        if (state.Buffer.Length == 0)
            return;

        var maxLength = Math.Max(1, _options.MaxMessageLength);
        while (state.Buffer.Length > maxLength)
        {
            var chunk = state.Buffer.ToString(0, maxLength);
            await SendOrEditStreamingMessageAsync(state, chunk, cancellationToken);
            state.MessageId = null;
            state.Buffer.Remove(0, maxLength);
        }

        if (!force && !ShouldFlush(state))
            return;

        var text = state.Buffer.ToString();
        if (string.IsNullOrEmpty(text))
            return;

        await SendOrEditStreamingMessageAsync(state, text, cancellationToken);
        state.LastFlushUtc = DateTimeOffset.UtcNow;
        state.PendingCharacterCount = 0;
    }

    private async Task SendOrEditStreamingMessageAsync(StreamingState state, string text, CancellationToken cancellationToken)
    {
        if (state.MessageId is null)
        {
            var sent = await _apiClient.SendMessageAsync(state.ChatId, text, cancellationToken);
            state.MessageId = sent.MessageId;
            return;
        }

        var edited = await _apiClient.EditMessageTextAsync(state.ChatId, state.MessageId.Value, text, cancellationToken);
        state.MessageId = edited.MessageId;
    }

    private static bool TryParseChatId(string conversationId, out long chatId)
        => long.TryParse(conversationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId);

    private bool IsChatAllowed(long chatId)
        => _options.AllowedChatIds.Count == 0 || _options.AllowedChatIds.Contains(chatId);

    private void EnsureChatAllowed(long chatId)
    {
        if (!IsChatAllowed(chatId))
            throw new InvalidOperationException($"Telegram chat '{chatId}' is not allowed for this adapter.");
    }

    private bool ShouldFlush(StreamingState state)
        => state.PendingCharacterCount >= StreamingFlushThresholdChars ||
           DateTimeOffset.UtcNow - state.LastFlushUtc >= TimeSpan.FromMilliseconds(Math.Max(1, _options.StreamingBufferMs));

    private static string BuildOutboundText(string content, IReadOnlyDictionary<string, object?> metadata)
    {
        var builder = new StringBuilder();
        if (TryGetMetadataString(metadata, "thinking", out var thinking))
        {
            builder.Append("_Thinking:_ ");
            builder.Append(EscapeMarkdownV2(thinking));
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(EscapeMarkdownV2(content));

        if (TryGetMetadataString(metadata, "toolCall", out var toolCall))
        {
            builder.AppendLine();
            builder.Append('`');
            builder.Append(EscapeInlineCode(toolCall));
            builder.Append('`');
        }

        return builder.ToString();
    }

    private static bool TryGetMetadataString(
        IReadOnlyDictionary<string, object?> metadata,
        string key,
        out string value)
    {
        if (metadata.TryGetValue(key, out var raw) &&
            raw is not null &&
            !string.IsNullOrWhiteSpace(raw.ToString()))
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

    private static string EscapeInlineCode(string value)
        => value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeMarkdownV2(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            if (ch is '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or '#'
                or '+' or '-' or '=' or '|' or '{' or '}' or '.' or '!' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
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
