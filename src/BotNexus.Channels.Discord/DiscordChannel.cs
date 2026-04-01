using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Discord;

/// <summary>
/// Discord channel implementation using Discord.Net.
/// </summary>
public sealed class DiscordChannel : BaseChannel
{
    private readonly DiscordSocketClient _client;
    private readonly string _botToken;

    public DiscordChannel(
        string botToken,
        IMessageBus messageBus,
        ILogger<DiscordChannel> logger,
        IReadOnlyList<string>? allowList = null)
        : base(messageBus, logger, allowList)
    {
        _botToken = botToken;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Warning
        });
        _client.MessageReceived += OnMessageReceivedAsync;
    }

    /// <inheritdoc/>
    public override string Name => "discord";

    /// <inheritdoc/>
    public override string DisplayName => "Discord";

    /// <inheritdoc/>
    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        await _client.LoginAsync(TokenType.Bot, _botToken).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(message.ChatId, out var channelId))
        {
            Logger.LogWarning("Invalid Discord channel ID: {ChatId}", message.ChatId);
            return;
        }

        if (_client.GetChannel(channelId) is IMessageChannel channel)
            await channel.SendMessageAsync(message.Content).ConfigureAwait(false);
        else
            Logger.LogWarning("Discord channel {ChannelId} not found", channelId);
    }

    private async Task OnMessageReceivedAsync(SocketMessage socketMessage)
    {
        if (socketMessage.Author.IsBot) return;

        var inbound = new InboundMessage(
            Channel: Name,
            SenderId: socketMessage.Author.Id.ToString(),
            ChatId: socketMessage.Channel.Id.ToString(),
            Content: socketMessage.Content,
            Timestamp: socketMessage.Timestamp,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                ["username"] = socketMessage.Author.Username,
                ["message_id"] = socketMessage.Id
            });

        await PublishMessageAsync(inbound).ConfigureAwait(false);
    }
}
