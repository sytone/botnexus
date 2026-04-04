using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BotNexus.Channels.Telegram;

/// <summary>
/// Telegram channel implementation using the Telegram.Bot library.
/// </summary>
public sealed class TelegramChannel : BaseChannel
{
    private readonly TelegramBotClient _botClient;
    private CancellationTokenSource? _receiverCts;

    public TelegramChannel(
        string botToken,
        IMessageBus messageBus,
        ILogger<TelegramChannel> logger,
        IReadOnlyList<string>? allowList = null)
        : base(messageBus, logger, allowList)
    {
        _botClient = new TelegramBotClient(botToken);
    }

    /// <inheritdoc/>
    public override string Name => "telegram";

    /// <inheritdoc/>
    public override string DisplayName => "Telegram";

    /// <inheritdoc/>
    public override bool SupportsStreaming => false;

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _receiverCts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message]
        };

        _botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            _receiverCts.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_receiverCts is not null)
        {
            await _receiverCts.CancelAsync().ConfigureAwait(false);
            _receiverCts.Dispose();
            _receiverCts = null;
        }
    }

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(message.ChatId, out var chatId))
        {
            Logger.LogWarning("Invalid Telegram chat ID: {ChatId}", message.ChatId);
            return;
        }

        await _botClient.SendMessage(chatId, message.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message?.Text is not { } text) return;

        var msg = update.Message;
        var senderId = msg.From?.Id.ToString() ?? string.Empty;
        var chatId = msg.Chat.Id.ToString();

        var inbound = new InboundMessage(
            Channel: Name,
            SenderId: senderId,
            ChatId: chatId,
            Content: text,
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                ["username"] = msg.From?.Username ?? string.Empty,
                ["message_id"] = msg.MessageId
            });

        await PublishMessageAsync(inbound, ct).ConfigureAwait(false);
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        Logger.LogError(exception, "Telegram error from {Source}", source);
        return Task.CompletedTask;
    }
}
