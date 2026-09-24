using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
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
/// Interactive sessions are replayed when <see cref="GatewayOptions.AutoReplayInterruptedTurns"/>
/// is <c>true</c>. Agent-only conversations are also replayed when the option is <c>false</c>
/// because no human participant can act on a resend notification. Cron/soul/subagent sessions
/// remain excluded. A replay counter in session metadata caps retries at
/// <see cref="GatewayOptions.MaxAutoReplayAttempts"/> to prevent infinite crash loops.
/// </remarks>
public sealed class InterruptedTurnNotificationService : IHostedLifecycleService
{
    internal const string NotificationContent =
        "⚠️ The gateway was restarted while your last message was being processed. " +
        "Your message was saved — please resend it to continue.";

    internal const string AgentOnlyNotificationContent =
        "⚠️ The gateway was restarted while the last message was being processed. " +
        "The message was saved; no human action is required.";

    internal const string MetadataKeyReplayCount = "interruption_replay_count";

    /// <summary>
    /// Gateway-authored banner (#3046) prepended to the session as a <see cref="MessageRole.System"/>
    /// entry flagged <see cref="SessionEntry.IsReplayBanner"/> immediately before an interrupted turn is
    /// auto-replayed. <c>{0}</c> is the replay attempt number, <c>{1}</c> the configured maximum, and
    /// <c>{2}</c> the original user message verbatim.
    /// </summary>
    /// <remarks>
    /// The wording deliberately refuses to claim the prior work was incomplete. The gateway knows only
    /// that the process died mid-turn, not how far the turn got; asserting "you did not finish" invites
    /// the agent to redo steps that already applied, which for a side-effecting turn (a file mutation, a
    /// revert, a push) is exactly the damage this banner exists to prevent. Stating the completion state
    /// as UNKNOWN and requiring verification closes both the "already done" and the "do it twice" reading.
    /// </remarks>
    internal const string ReplayBannerTemplate =
        "[PLATFORM RESTART - AUTOMATIC REPLAY {0} of {1}]\n" +
        "The gateway restarted while your previous turn was still running, so that turn was cut off at an " +
        "unknown point. Some of the work described above may have completed and some may not have. Do not " +
        "assume either. Before taking any action - especially anything that writes, mutates, or is otherwise " +
        "not safely repeatable - verify the current state directly with your tools, then continue from " +
        "wherever you actually are.\n\n" +
        "This is the original message that started the interrupted loop, repeated verbatim below:\n\n{2}";

    /// <summary>Builds the restart-replay banner for a given attempt and original message.</summary>
    internal static string BuildReplayBanner(string originalContent, int attempt, int maxAttempts)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ReplayBannerTemplate,
            attempt,
            maxAttempts,
            originalContent);

    private readonly ISessionStore _sessions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IActivityBroadcaster _broadcaster;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<InterruptedTurnNotificationService> _logger;
    private readonly IInboundMessageOrchestrator? _orchestrator;
    private readonly GatewayOptions _options;
    private readonly IConversationStore? _conversations;

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
               orchestrator: null, options: null, conversations: null)
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
        IOptions<GatewayOptions>? options,
        IConversationStore? conversations = null)
    {
        _sessions = sessions;
        _agentRegistry = agentRegistry;
        _broadcaster = broadcaster;
        _channelManager = channelManager;
        _logger = logger;
        _orchestrator = orchestrator;
        _options = options?.Value ?? new GatewayOptions();
        _conversations = conversations;
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op at StartAsync: the interrupted-turn scan is deferred to <see cref="StartedAsync"/>.
    /// The scan iterates <see cref="IAgentRegistry.GetAll"/>, but agents are registered by other
    /// hosted services (e.g. AgentConfigurationHostedService, ConfigHydrationService (formerly BuiltInAgentRegistrationService)/AgentConfigurationHostedService)
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

                var isAgentOnlyConversation = await IsAgentOnlyConversationAsync(session, cancellationToken)
                    .ConfigureAwait(false);
                var notificationContent = isAgentOnlyConversation
                    ? AgentOnlyNotificationContent
                    : NotificationContent;
                var notification = new SessionEntry
                {
                    Role = MessageRole.Notification,
                    Content = notificationContent,
                    Timestamp = DateTimeOffset.UtcNow
                };

                session.AddEntry(notification);

                // Agent-only conversations have nobody who can act on a resend instruction, so
                // replay them independently of the human-facing opt-in. The same interactive and
                // attempt-cap guards still apply. Missing conversation data is conservative: it
                // does not prove the absence of a human.
                var shouldAttemptReplay = (_options.AutoReplayInterruptedTurns || isAgentOnlyConversation)
                    && session.IsInteractive
                    && _orchestrator is not null;
                var didReplay = false;
                if (shouldAttemptReplay)
                {
                    didReplay = await TryAutoReplayAsync(session, agentId, cancellationToken).ConfigureAwait(false);
                    if (didReplay)
                        replayed++;
                }

                // The sentinel is the durable recovery marker. A refused or impossible replay must
                // leave it in place so a later startup can try again. Notify-only sessions retain
                // the historical behaviour because there is deliberately no replacement turn.
                if (didReplay || !shouldAttemptReplay)
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
                    Message = didReplay
                        ? $"{notificationContent} (auto-replaying)"
                        : notificationContent
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
                            Content = notificationContent,
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

    private async Task<bool> IsAgentOnlyConversationAsync(
        GatewaySession session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInteractive || _conversations is null || !session.ConversationId.IsInitialized())
            return false;

        try
        {
            var conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken)
                .ConfigureAwait(false);
            return conversation is not null
                && conversation.Participants.Count > 0
                && conversation.Participants.All(static participant =>
                    participant.CitizenId.Kind != CitizenKind.User);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve conversation {ConversationId} participants during interrupted-turn scan; preserving notify-only behavior",
                session.ConversationId.Value);
            return false;
        }
    }

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

        // Human requests are authoritative user entries. Agent-origin API requests are authoritative
        // assistant entries with durable API sender provenance; accepting every assistant response
        // would replay model output as a new request.
        var initiatingEntry = session.History
            .Where(e => !e.IsCrashSentinel && !string.IsNullOrWhiteSpace(e.Content))
            .Where(e => e.Role == MessageRole.User
                || (session.ChannelType == ChannelKey.From("api")
                    && e.Role == MessageRole.Assistant
                    && !string.IsNullOrWhiteSpace(e.SenderId)
                    && e.SenderId.StartsWith("api", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Timestamp)
            .LastOrDefault();

        if (initiatingEntry is null)
        {
            _logger.LogWarning(
                "Session {SessionId} has no authoritative initiating message; recovery remains retryable",
                session.SessionId.Value);
            return Task.FromResult(false);
        }

        var channelType = session.ChannelType ?? ChannelKey.From("internal");
        var callerId = initiatingEntry.SenderId ?? session.CallerId ?? session.SessionId.Value;
        var outcomeUnknownToolCallIds = FindOutcomeUnknownToolCallIds(session.History);
        var metadata = new Dictionary<string, object?>
        {
            ["isReplay"] = true,
            ["originalTimestamp"] = initiatingEntry.Timestamp.ToString("o")
        };
        if (outcomeUnknownToolCallIds.Length > 0)
            metadata["outcomeUnknownToolCallIds"] = outcomeUnknownToolCallIds;

        var replay = new InboundMessage
        {
            ChannelType = channelType,
            SenderId = callerId,
            Sender = CitizenId.Of(agentId),
            ChannelAddress = ChannelAddress.From(session.ConversationId.IsInitialized()
                ? session.ConversationId.Value
                : callerId),
            Content = initiatingEntry.Content,
            Timestamp = DateTimeOffset.UtcNow,
            Trigger = initiatingEntry.Trigger,
            SpeakAs = initiatingEntry.Role,
            Kind = initiatingEntry.Kind,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: agentId,
                RequestedSessionId: session.SessionId,
                RequestedConversationId: session.ConversationId.IsInitialized() ? session.ConversationId : null),
            Metadata = metadata
        };

        var accepted = _orchestrator!.Post(replay);

        _logger.LogInformation(
            "Auto-replay for session {SessionId}: Post returned {Accepted} (attempt {Attempt}/{Max}, outcome-unknown tools {OutcomeUnknownCount})",
            session.SessionId.Value, accepted, replayCount + 1, _options.MaxAutoReplayAttempts,
            outcomeUnknownToolCallIds.Length);

        if (!accepted)
            return Task.FromResult(false);

        session.Metadata[MetadataKeyReplayCount] = replayCount + 1;
        var bannerContent = BuildReplayBanner(
            initiatingEntry.Content, replayCount + 1, _options.MaxAutoReplayAttempts);
        if (outcomeUnknownToolCallIds.Length > 0)
        {
            bannerContent += "\n\nOutcome is unknown for these recent tool calls; verify each before repeating it: "
                + string.Join(", ", outcomeUnknownToolCallIds);
        }

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = bannerContent,
            Timestamp = DateTimeOffset.UtcNow,
            IsReplayBanner = true
        });

        return Task.FromResult(true);
    }

    private static string[] FindOutcomeUnknownToolCallIds(IEnumerable<SessionEntry> history)
    {
        var completed = history
            .Where(static entry => entry.IsToolResultRow() && !string.IsNullOrWhiteSpace(entry.ToolCallId))
            .Select(static entry => entry.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        return history
            .Where(static entry => entry.IsToolStartRow() && !string.IsNullOrWhiteSpace(entry.ToolCallId))
            .Select(static entry => entry.ToolCallId!)
            .Where(toolCallId => !completed.Contains(toolCallId))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
    }
}
