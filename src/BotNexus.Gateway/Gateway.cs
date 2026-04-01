using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Channels.Base;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

/// <summary>
/// The main gateway hosted service. Starts all channels and dispatches
/// inbound messages from the message bus to registered agent runners.
/// Publishes activity events so the web UI can observe all traffic.
/// </summary>
public sealed class Gateway : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IActivityStream _activityStream;
    private readonly ChannelManager _channelManager;
    private readonly IEnumerable<IAgentRunner> _agentRunners;
    private readonly ILogger<Gateway> _logger;
    private readonly BotNexusConfig _config;

    public Gateway(
        IMessageBus messageBus,
        IActivityStream activityStream,
        ChannelManager channelManager,
        IEnumerable<IAgentRunner> agentRunners,
        ILogger<Gateway> logger,
        IOptions<BotNexusConfig> config)
    {
        _messageBus = messageBus;
        _activityStream = activityStream;
        _channelManager = channelManager;
        _agentRunners = agentRunners;
        _logger = logger;
        _config = config.Value;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BotNexus Gateway starting...");

        await _channelManager.StartAllAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("BotNexus Gateway ready. Listening for messages...");

        await foreach (var message in _messageBus.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            // Broadcast inbound message to activity stream
            await _activityStream.PublishAsync(new ActivityEvent(
                ActivityEventType.MessageReceived,
                message.Channel,
                message.SessionKey,
                message.ChatId,
                message.SenderId,
                message.Content,
                message.Timestamp), stoppingToken).ConfigureAwait(false);

            // Dispatch concurrently to all agent runners
            var runners = _agentRunners.ToList();
            if (runners.Count == 0)
            {
                _logger.LogWarning("No agent runners registered, dropping message");
                continue;
            }

            // Capture activity stream for the closure
            var activityStream = _activityStream;

            // Run the first matching runner (could be extended for per-agent routing)
            _ = Task.Run(async () =>
            {
                try
                {
                    await runners[0].RunAsync(message, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message from {Channel}/{ChatId}",
                        message.Channel, message.ChatId);

                    await activityStream.PublishAsync(new ActivityEvent(
                        ActivityEventType.Error,
                        message.Channel,
                        message.SessionKey,
                        message.ChatId,
                        message.SenderId,
                        ex.Message,
                        DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
                }
            }, stoppingToken);
        }

        _logger.LogInformation("BotNexus Gateway stopping...");
        await _channelManager.StopAllAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
