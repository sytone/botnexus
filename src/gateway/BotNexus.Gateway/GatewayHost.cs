using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway;

/// <summary>
/// The central Gateway orchestration service. Manages the lifecycle of channel adapters,
/// listens for inbound messages, routes them to agents, and streams responses back.
/// </summary>
/// <remarks>
/// <para>
/// This is the heart of BotNexus — it wires together the agent supervisor, message router,
/// session store, channel adapters, and activity broadcaster into a coherent pipeline.
/// </para>
/// <para>Flow:</para>
/// <list type="number">
///   <item>Channel adapters receive messages from external sources and call <see cref="IChannelDispatcher.DispatchAsync"/>.</item>
///   <item>The dispatcher routes the message via <see cref="IMessageRouter"/> to find target agents.</item>
///   <item>For each target agent, the supervisor gets or creates an instance via <see cref="IAgentSupervisor"/>.</item>
///   <item>The message is forwarded to the agent handle, which processes it and streams events.</item>
///   <item>Responses are sent back through the originating channel adapter.</item>
///   <item>All activity is broadcast via <see cref="IActivityBroadcaster"/> for real-time monitoring.</item>
/// </list>
/// </remarks>
public sealed class GatewayHost : BackgroundService, IChannelDispatcher
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IMessageRouter _router;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly IEnumerable<IChannelAdapter> _channels;
    private readonly ILogger<GatewayHost> _logger;

    // Channel adapters indexed by type for response routing
    private readonly Dictionary<string, IChannelAdapter> _channelMap = new(StringComparer.OrdinalIgnoreCase);

    public GatewayHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IEnumerable<IChannelAdapter> channels,
        ILogger<GatewayHost> logger)
    {
        _supervisor = supervisor;
        _router = router;
        _sessions = sessions;
        _activity = activity;
        _channels = channels;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Build channel map and start all adapters
        foreach (var channel in _channels)
        {
            _channelMap[channel.ChannelType] = channel;
            try
            {
                await channel.StartAsync(this, stoppingToken);
                _logger.LogInformation("Started channel adapter: {ChannelType} ({DisplayName})", channel.ChannelType, channel.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start channel adapter: {ChannelType}", channel.ChannelType);
            }
        }

        _logger.LogInformation("Gateway started with {ChannelCount} channel adapter(s)", _channelMap.Count);

        // Keep running until shutdown
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* Expected on shutdown */ }

        // Graceful shutdown
        _logger.LogInformation("Gateway shutting down...");
        await _supervisor.StopAllAsync(CancellationToken.None);

        foreach (var channel in _channels)
        {
            try { await channel.StopAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping channel adapter: {ChannelType}", channel.ChannelType); }
        }
    }

    /// <inheritdoc />
    public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.MessageReceived,
            ChannelType = message.ChannelType,
            Message = message.Content,
            SessionId = message.SessionId
        }, cancellationToken);

        var targetAgents = await _router.ResolveAsync(message, cancellationToken);
        if (targetAgents.Count == 0)
        {
            _logger.LogWarning("No agent resolved for message from {ChannelType}:{SenderId}", message.ChannelType, message.SenderId);
            return;
        }

        foreach (var agentId in targetAgents)
        {
            // Each agent gets its own session
            var sessionId = message.SessionId ?? $"{message.ChannelType}:{message.ConversationId}:{agentId}";
            var session = await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);

            // Record user message
            session.History.Add(new SessionEntry { Role = "user", Content = message.Content });
            session.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentProcessing,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);

                // Stream if the channel supports it, otherwise collect and send
                if (_channelMap.TryGetValue(message.ChannelType, out var channel) && channel.SupportsStreaming)
                {
                    await foreach (var evt in handle.StreamAsync(message.Content, cancellationToken))
                    {
                        if (evt.Type == AgentStreamEventType.ContentDelta && evt.ContentDelta is not null)
                            await channel.SendStreamDeltaAsync(message.ConversationId, evt.ContentDelta, cancellationToken);
                    }
                }
                else
                {
                    var response = await handle.PromptAsync(message.Content, cancellationToken);
                    if (_channelMap.TryGetValue(message.ChannelType, out var ch))
                    {
                        await ch.SendAsync(new OutboundMessage
                        {
                            ChannelType = message.ChannelType,
                            ConversationId = message.ConversationId,
                            Content = response.Content,
                            SessionId = sessionId
                        }, cancellationToken);
                    }

                    session.History.Add(new SessionEntry { Role = "assistant", Content = response.Content });
                }

                session.UpdatedAt = DateTimeOffset.UtcNow;
                await _sessions.SaveAsync(session, cancellationToken);

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentCompleted,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message for agent '{AgentId}' session '{SessionId}'", agentId, sessionId);
                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.Error,
                    AgentId = agentId,
                    SessionId = sessionId,
                    Message = ex.Message
                }, cancellationToken);
            }
        }
    }
}
