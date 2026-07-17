using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// <remarks>
/// When <see cref="GatewayOptions.AutoReplayInterruptedTurns"/> is <c>true</c> and the
/// session is interactive (not cron/soul/subagent), the service additionally re-dispatches
/// the last user message so the agent can complete the interrupted turn automatically.
/// A replay counter in session metadata caps retries at <see cref="GatewayOptions.MaxAutoReplayAttempts"/>
/// to prevent infinite crash loops.
/// </remarks>
public sealed class InterruptedTurnNotificationService : IHostedLifecycleService
{
    internal const string NotificationContent =
        "⚠️ The gateway was restarted while your last message was being processed. " +
        "Your message was saved — please resend it to continue.";

    internal const string MetadataKeyReplayCount = "interruption_replay_count";

    private readonly ISessionStore _sessions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IActivityBroadcaster _broadcaster;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<InterruptedTurnNotificationService> _logger;
    private readonly IInboundMessageOrchestrator? _orchestrator;
    private readonly GatewayOptions _options;

    /// <summary>
    /// Initializes a new instance without auto-replay support (backwards-compat overload).
    /// </summary>
    public InterruptedTurnNotificationService(
        ISessionStore sessions,
        IAgentRegistry agentRegistry,
        IActivityBroadcaster broadcaster,
        IChannelManager channelManager,
        ILogger<InterruptedTurnNotificationService> logger)
        : this(sessions, agentRegistry, broadcaster, channelManager, logger,
               orchestrator: null, options: null)
    {
    }

    /// <summary>
    /// Initializes a new instance with optional auto-replay support.
    /// </summary>
    public InterruptedTurnNotificationService(
        ISessionStore sessions,
        IAgentRegistry agentRegistry,
        IActivityBroadcaster broadcaster,
        IChannelManager channelManager,
        ILogger<InterruptedTurnNotificationService> logger,
        IInboundMessageOrchestrator? orchestrator,
        IOptions<GatewayOptions>? options)
    {
        _sessions = sessions;
        _agentRegistry = agentRegistry;
        _broadcaster = broadcaster;
        _channelManager = channelManager;
        _logger = logger;
        _orchestrator = orchestrator;
        _options = options?.Value ?? new GatewayOptions();
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op at StartAsync: the interrupted-turn scan is deferred to <see cref="StartedAsync"/>.
    /// The scan iterates <see cref="IAgentRegistry.GetAll"/>, but agents are registered by other
    /// hosted services (e.g. BuiltInAgentRegistrationService, AgentConfigurationHostedService)
    /// during their own StartAsync. Running the scan here races that registration and historically
    /// always observed an empty registry, so the scan silently no-opped and orphaned sentinels
    /// survived every restart (#2030). StartedAsync runs only after every hosted service's
    /// StartAsync has completed, so the registry is fully populated by then.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var agents = _agentRegistry.GetAll();
        var notified = 0;
        var replayed = 0;

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

                // Attempt auto-replay before saving (replay decision is metadata-driven).
                var didReplay = false;
                if (_options.AutoReplayInterruptedTurns && session.IsInteractive && _orchestrator is not null)
                {
                    didReplay = await TryAutoReplayAsync(session, agentId, cancellationToken).ConfigureAwait(false);
                    if (didReplay)
                        replayed++;
                }

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
                    Message = didReplay
                        ? $"{NotificationContent} (auto-replaying)"
                        : NotificationContent
                }, cancellationToken).ConfigureAwait(false);

                // Deliver via channel adapter when we have enough addressing information.
                if (!didReplay
                    && session.ChannelType.HasValue
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
            "Interrupted-turn scan complete: {NotifiedCount} session(s) found with crash sentinels ({ReplayedCount} auto-replayed)",
            notified, replayed);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<bool> TryAutoReplayAsync(GatewaySession session, AgentId agentId, CancellationToken cancellationToken)
    {
        // Read existing replay count from metadata.
        var replayCount = 0;
        if (session.Metadata.TryGetValue(MetadataKeyReplayCount, out var raw))
        {
            replayCount = raw switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }

        if (replayCount >= _options.MaxAutoReplayAttempts)
        {
            _logger.LogWarning(
                "Session {SessionId} has reached max auto-replay attempts ({Max}); falling back to notification only",
                session.SessionId.Value, _options.MaxAutoReplayAttempts);
            return Task.FromResult(false);
        }

        // Find the last user message before the sentinel — this is what we replay.
        var lastUser = session.History
            .Where(e => e.Role == MessageRole.User && !e.IsCrashSentinel)
            .OrderBy(e => e.Timestamp)
            .LastOrDefault();

        if (lastUser is null || string.IsNullOrWhiteSpace(lastUser.Content))
        {
            _logger.LogDebug(
                "Session {SessionId} has no replayable user message; skipping auto-replay",
                session.SessionId.Value);
            return Task.FromResult(false);
        }

        // Increment the replay counter in metadata.
        session.Metadata[MetadataKeyReplayCount] = replayCount + 1;

        var channelType = session.ChannelType ?? ChannelKey.From("internal");
        var callerId = session.CallerId ?? session.SessionId.Value;

        var replay = new InboundMessage
        {
            ChannelType = channelType,
            SenderId = callerId,
            Sender = CitizenId.Of(agentId),
            ChannelAddress = ChannelAddress.From(callerId),
            Content = lastUser.Content,
            Timestamp = DateTimeOffset.UtcNow,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: agentId,
                RequestedSessionId: session.SessionId,
                RequestedConversationId: session.ConversationId.IsInitialized() ? session.ConversationId : null),
            Metadata = new Dictionary<string, object?>
            {
                ["isReplay"] = true,
                ["originalTimestamp"] = lastUser.Timestamp.ToString("o")
            }
        };

        var accepted = _orchestrator!.Post(replay);

        _logger.LogInformation(
            "Auto-replay for session {SessionId}: Post returned {Accepted} (attempt {Attempt}/{Max})",
            session.SessionId.Value, accepted, replayCount + 1, _options.MaxAutoReplayAttempts);

        return Task.FromResult(accepted);
    }
}
