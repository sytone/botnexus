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
    private readonly IAgentRouter _agentRouter;
    private readonly ILogger<Gateway> _logger;
    private readonly BotNexusConfig _config;

    public Gateway(
        IMessageBus messageBus,
        IActivityStream activityStream,
        ChannelManager channelManager,
        IAgentRouter agentRouter,
        ILogger<Gateway> logger,
        IOptions<BotNexusConfig> config)
    {
        _messageBus = messageBus;
        _activityStream = activityStream;
        _channelManager = channelManager;
        _agentRouter = agentRouter;
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

            var targetRunners = _agentRouter.ResolveTargets(message);
            if (targetRunners.Count == 0)
            {
                _logger.LogWarning("No agent runners registered, dropping message");
                continue;
            }

            // Capture activity stream for the closure
            var activityStream = _activityStream;

            _ = Task.Run(async () =>
            {
                try
                {
                    var runTasks = targetRunners.Select(async runner =>
                    {
                        _logger.LogInformation(
                            "Dispatching message from {Channel}/{ChatId} to agent {AgentName}",
                            message.Channel,
                            message.ChatId,
                            runner.AgentName);
                        await runner.RunAsync(message, stoppingToken).ConfigureAwait(false);
                    });

                    await Task.WhenAll(runTasks).ConfigureAwait(false);
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
