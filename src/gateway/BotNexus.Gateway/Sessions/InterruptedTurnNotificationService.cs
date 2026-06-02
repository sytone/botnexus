using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Hosted service that runs once at gateway startup to detect and notify users whose
/// agent turn was interrupted by a gateway restart. Any session that contains an
/// unresolved crash-sentinel entry (written by <see cref="GatewayHost"/> before each
/// LLM call) indicates the previous run did not complete cleanly. For each such session
/// this service appends a <see cref="MessageRole.Notification"/> entry, removes the
/// sentinels, persists the session, and delivers an out-of-band notification through
/// the originating channel when possible.
/// </summary>
public sealed class InterruptedTurnNotificationService : IHostedService
{
    private const string NotificationContent =
        "⚠️ The gateway was restarted while your last message was being processed. " +
        "Your message was saved — please resend it to continue.";

    private readonly ISessionStore _sessions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IActivityBroadcaster _broadcaster;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<InterruptedTurnNotificationService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="InterruptedTurnNotificationService"/>.
    /// </summary>
    public InterruptedTurnNotificationService(
        ISessionStore sessions,
        IAgentRegistry agentRegistry,
        IActivityBroadcaster broadcaster,
        IChannelManager channelManager,
        ILogger<InterruptedTurnNotificationService> logger)
    {
        _sessions = sessions;
        _agentRegistry = agentRegistry;
        _broadcaster = broadcaster;
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var agents = _agentRegistry.GetAll();
        var notified = 0;

        foreach (var descriptor in agents)
        {
            var agentId = descriptor.AgentId;
            IReadOnlyList<GatewaySession> agentSessions;
            try
            {
                agentSessions = await _sessions.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list sessions for agent {AgentId} during interrupted-turn scan", agentId.Value);
                continue;
            }

            foreach (var session in agentSessions)
            {
                if (!session.History.Any(static e => e.IsCrashSentinel))
                    continue;

                _logger.LogInformation(
                    "Session {SessionId} (agent {AgentId}) has unresolved crash sentinels — notifying user",
                    session.SessionId.Value, agentId.Value);

                var notification = new SessionEntry
                {
                    Role = MessageRole.Notification,
                    Content = NotificationContent,
                    Timestamp = DateTimeOffset.UtcNow
                };

                session.AddEntry(notification);
                session.RemoveCrashSentinels();

                try
                {
                    await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save session {SessionId} after removing crash sentinels", session.SessionId.Value);
                    continue;
                }

                // Broadcast activity so dashboards and monitoring surfaces pick it up.
                await _broadcaster.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.System,
                    AgentId = agentId.Value,
                    SessionId = session.SessionId.Value,
                    ConversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
                    Message = NotificationContent
                }, cancellationToken).ConfigureAwait(false);

                // Deliver via channel adapter when we have enough addressing information.
                if (session.ChannelType.HasValue
                    && !string.IsNullOrWhiteSpace(session.CallerId)
                    && _channelManager.Get(session.ChannelType.Value) is { } adapter)
                {
                    try
                    {
                        await adapter.SendAsync(new OutboundMessage
                        {
                            ChannelType = session.ChannelType.Value,
                            ChannelAddress = ChannelAddress.From(session.CallerId),
                            Content = NotificationContent,
                            SessionId = session.SessionId.Value,
                            ConversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : null
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to deliver interrupted-turn notification via channel {ChannelType} for session {SessionId}",
                            session.ChannelType.Value, session.SessionId.Value);
                    }
                }

                notified++;
            }
        }

        _logger.LogInformation(
            "Interrupted-turn scan complete: {NotifiedCount} session(s) found with crash sentinels and notified",
            notified);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
