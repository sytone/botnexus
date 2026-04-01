using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace BotNexus.Channels.Slack;

/// <summary>
/// Slack channel implementation using SlackNet.
/// </summary>
public sealed class SlackChannel : BaseChannel
{
    private readonly ISlackApiClient _slackClient;
    private readonly string _botToken;

    public SlackChannel(
        string botToken,
        IMessageBus messageBus,
        ILogger<SlackChannel> logger,
        IReadOnlyList<string>? allowList = null)
        : base(messageBus, logger, allowList)
    {
        _botToken = botToken;
        _slackClient = new SlackServiceBuilder()
            .UseApiToken(botToken)
            .GetApiClient();
    }

    /// <inheritdoc/>
    public override string Name => "slack";

    /// <inheritdoc/>
    public override string DisplayName => "Slack";

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Slack channel started (webhook mode - waiting for events)");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        await _slackClient.Chat.PostMessage(new Message
        {
            Channel = message.ChatId,
            Text = message.Content
        }).ConfigureAwait(false);
    }

    /// <summary>Handles an incoming Slack message event.</summary>
    public async Task HandleMessageAsync(MessageEvent slackMessage, CancellationToken cancellationToken = default)
    {
        if (slackMessage.BotId is not null) return; // Ignore bot messages

        var inbound = new InboundMessage(
            Channel: Name,
            SenderId: slackMessage.User ?? string.Empty,
            ChatId: slackMessage.Channel,
            Content: slackMessage.Text ?? string.Empty,
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                ["team_id"] = slackMessage.Team ?? string.Empty,
                ["ts"] = slackMessage.Ts ?? string.Empty
            });

        await PublishMessageAsync(inbound, cancellationToken).ConfigureAwait(false);
    }
}
