using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Hosted service that runs once at gateway startup to detect and notify users whose
/// agent turn was interrupted by a gateway restart. Any session that contains an
/// unresolved crash-sentinel entry (written by <see cref="GatewayHost"/> before each
/// LLM call) indicates the previous run did not complete cleanly. For each such session
/// this service appends a <see cref="MessageRole.Notification"/> entry, removes the
/// sentinels, persists the session, and publishes that committed session-item fact through
/// the channel-neutral conversation event seam.
/// </summary>
/// <remarks>
/// Interactive sessions are replayed when <see cref="GatewayOptions.AutoReplayInterruptedTurns"/>
/// is <c>true</c>. Agent-only conversations are also replayed when the option is <c>false</c>
/// because no human participant can act on a resend notification. Cron/soul/subagent sessions
/// remain excluded. A replay counter in session metadata caps retries at
/// <see cref="GatewayOptions.MaxAutoReplayAttempts"/> to prevent infinite crash loops.
/// </remarks>
/// <remarks>
/// Conversation-event publication is deliberately post-commit and best-effort. A rejection or
/// exception is logged but never rolls back the saved notification, and this startup scan does not
/// retry publication because the crash sentinel has already been consumed. The session store is the
/// source of truth: clients recover missed live events by hydrating the persisted transcript, while
/// broader startup reconciliation belongs at the session/conversation projection boundary rather
/// than in this one-shot recovery service. <see cref="GatewayActivity"/> remains telemetry only.
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
    private readonly IConversationEventPublisher _eventPublisher;
    private readonly ILogger<InterruptedTurnNotificationService> _logger;
    private readonly IInboundMessageOrchestrator? _orchestrator;
    private readonly GatewayOptions _options;
    private readonly IConversationStore? _conversations;
    private readonly SessionLifecycleEvents? _lifecycleEvents;
    private readonly ConditionalWeakTable<GatewaySession, SemaphoreSlim> _recoveryGates = new();

    /// <summary>
    /// Initializes the startup recovery scan and its post-commit conversation-event projection.
    /// </summary>
    public InterruptedTurnNotificationService(
        ISessionStore sessions,
        IAgentRegistry agentRegistry,
        IActivityBroadcaster broadcaster,
        IConversationEventPublisher eventPublisher,
        ILogger<InterruptedTurnNotificationService> logger,
        IInboundMessageOrchestrator? orchestrator,
        IOptions<GatewayOptions>? options,
        IConversationStore? conversations = null,
        SessionLifecycleEvents? lifecycleEvents = null)
    {
        _sessions = sessions;
        _agentRegistry = agentRegistry;
        _broadcaster = broadcaster;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _orchestrator = orchestrator;
        _options = options?.Value ?? new GatewayOptions();
        _conversations = conversations;
        _lifecycleEvents = lifecycleEvents;
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged += OnSessionLifecycleChangedAsync;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 256;
        var registeredAgents = _agentRegistry.GetAll().Select(static descriptor => descriptor.AgentId).ToHashSet();
        var notified = 0;
        var replayed = 0;
        var scanned = 0;
        string? cursor = null;
        var started = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        try
        {
            do
            {
                var page = await _sessions.ListUnresolvedCrashSentinelsAsync(pageSize, cursor, cancellationToken)
                    .ConfigureAwait(false);
                scanned += page.Rows.Count;
                foreach (var row in page.Rows)
                {
                    if (!registeredAgents.Contains(row.AgentId))
                        continue;

                    var session = await _sessions.GetAsync(row.SessionId, cancellationToken).ConfigureAwait(false);
                    if (session is null || !session.History.Any(static entry => entry.IsCrashSentinel))
                        continue;

                    _logger.LogInformation(
                        "Session {SessionId} (agent {AgentId}) has unresolved crash sentinels - notifying user",
                        row.SessionId.Value, row.AgentId.Value);
                    var isAgentOnlyConversation = await IsAgentOnlyConversationAsync(session, cancellationToken)
                        .ConfigureAwait(false);
                    var didReplay = await RecoverSessionAsync(
                        session, row.AgentId, isAgentOnlyConversation, cancellationToken).ConfigureAwait(false);
                    if (didReplay) replayed++;
                    notified++;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed during bounded interrupted-turn scan after {ScannedRows} candidate row(s)", scanned);
        }

        _logger.LogInformation(
            "Interrupted-turn scan complete: {ScannedRows} candidate row(s), {ElapsedMilliseconds} ms, {AllocatedBytes} allocated bytes; " +
            "{NotifiedCount} session(s) notified ({ReplayedCount} auto-replayed)",
            scanned, started.ElapsedMilliseconds, GC.GetTotalAllocatedBytes() - allocatedBefore, notified, replayed);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged -= OnSessionLifecycleChangedAsync;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OnSessionLifecycleChangedAsync(
        SessionLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        if (lifecycleEvent.Type != SessionLifecycleEventType.TerminalFailure
            || lifecycleEvent.Session is not { } session
            || session.ChannelType != ChannelKey.From("api")
            || !session.History.Any(static entry => entry.IsCrashSentinel))
        {
            return;
        }

        if (!session.IsInteractive
            || !await IsAgentOnlyConversationAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var gate = _recoveryGates.GetValue(session, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!session.History.Any(static entry => entry.IsCrashSentinel))
                return;

            await RecoverSessionAsync(session, session.AgentId, isAgentOnlyConversation: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> RecoverSessionAsync(
        GatewaySession session,
        AgentId agentId,
        bool isAgentOnlyConversation,
        CancellationToken cancellationToken)
    {
        var writeFence = SessionWriteFence.Capture(session);
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

        var shouldAttemptReplay = (_options.AutoReplayInterruptedTurns || isAgentOnlyConversation)
            && session.IsInteractive
            && _orchestrator is not null;
        var didReplay = shouldAttemptReplay
            && await TryAutoReplayAsync(session, agentId, cancellationToken).ConfigureAwait(false);

        if (didReplay || !shouldAttemptReplay)
            session.RemoveCrashSentinels();

        try
        {
            var outcome = await _sessions.SaveAsync(session, writeFence, cancellationToken).ConfigureAwait(false);
            if (outcome != SessionSaveOutcome.Persisted)
            {
                _logger.LogInformation(
                    "Interrupted-turn recovery for session {SessionId} was skipped because the session was deleted, sealed, or rebound",
                    session.SessionId.Value);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save session {SessionId} after interrupted-turn recovery", session.SessionId.Value);
            return false;
        }

        await PublishPersistedNotificationAsync(session, agentId, notification, cancellationToken)
            .ConfigureAwait(false);

        await _broadcaster.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.System,
            AgentId = agentId.Value,
            SessionId = session.SessionId.Value,
            ConversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
            Message = didReplay ? $"{notificationContent} (auto-replaying)" : notificationContent
        }, cancellationToken).ConfigureAwait(false);

        return didReplay;
    }

    private async Task PublishPersistedNotificationAsync(
        GatewaySession session,
        AgentId agentId,
        SessionEntry notification,
        CancellationToken cancellationToken)
    {
        if (!session.ConversationId.IsInitialized())
        {
            _logger.LogWarning(
                "Persisted interrupted-turn notification for session {SessionId} has no conversation id; live event publication was skipped",
                session.SessionId.Value);
            return;
        }

        var bindings = System.Collections.Immutable.ImmutableArray<ConversationBindingSnapshot>.Empty;
        if (_conversations is not null)
        {
            try
            {
                var conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken)
                    .ConfigureAwait(false);
                bindings = ConversationBindingSnapshot.FromMany(conversation?.ChannelBindings);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The committed transcript remains authoritative. A later client hydration or
                // reconciliation can recover it even when binding projection is temporarily unavailable.
                _logger.LogWarning(
                    ex,
                    "Failed to snapshot bindings for persisted interrupted-turn notification in conversation {ConversationId}; publishing with no bindings",
                    session.ConversationId.Value);
            }
        }

        try
        {
            var accepted = await _eventPublisher.PublishAsync(new ConversationSessionItemPersistedEvent
            {
                AgentId = agentId,
                ConversationId = session.ConversationId,
                SessionId = session.SessionId,
                Bindings = bindings,
                Item = notification,
                OccurredAt = notification.Timestamp
            }, cancellationToken).ConfigureAwait(false);

            if (!accepted)
            {
                _logger.LogWarning(
                    "Conversation event publisher rejected persisted interrupted-turn notification for session {SessionId}; the committed session item will not be retried",
                    session.SessionId.Value);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Publication is not transactional with session persistence. Never compensate the durable
            // mutation: consumers reconcile from the transcript when live best-effort delivery is missed.
            _logger.LogWarning(
                ex,
                "Failed to publish persisted interrupted-turn notification for session {SessionId}; the committed session item will not be retried",
                session.SessionId.Value);
        }
    }

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

    private static bool IsSynthesizedIncompleteResult(SessionEntry entry)
        => entry.ToolIsError == true
            && entry.Content?.Contains("did not complete", StringComparison.Ordinal) == true
            && entry.Content.Contains("result synthesized for transcript consistency.", StringComparison.Ordinal);

    private static string[] FindOutcomeUnknownToolCallIds(IEnumerable<SessionEntry> history)
    {
        var completed = history
            .Where(static entry => entry.IsToolResultRow()
                && !string.IsNullOrWhiteSpace(entry.ToolCallId)
                && !IsSynthesizedIncompleteResult(entry))
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
