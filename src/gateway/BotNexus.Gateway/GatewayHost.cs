using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using MessageRole = BotNexus.Domain.Primitives.MessageRole;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using UserId = BotNexus.Domain.Primitives.UserId;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using BotNexus.Domain.World;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

/// <summary>
/// The central Gateway orchestration service. Manages the lifecycle of channel adapters,
/// listens for inbound messages, routes them to agents, and streams responses back.
/// </summary>
public sealed class GatewayHost : BackgroundService, IChannelDispatcher, IInboundMessageProcessor, IAsyncDisposable
{
    private const string ControlSteer = "steer";
    private const string ControlCompact = "compact";

    private readonly IAgentSupervisor _supervisor;
    private readonly IMessageRouter _router;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly IChannelManager _channelManager;
    private readonly ISessionCompactor _compactor;
    private readonly IOptionsMonitor<CompactionOptions> _compactionOptions;
    private readonly ILogger<GatewayHost> _logger;
    private readonly IAgentRegistry? _registry;
    private readonly IMediaPipeline? _mediaPipeline;
    private readonly IConversationDispatcher? _conversationDispatcher;
    private readonly IConversationRouter? _conversationRouter;
    private readonly SessionLifecycleEvents? _sessionLifecycleEvents;
    private readonly PendingAskUserInterceptor? _pendingAskUserInterceptor;
    private readonly IPreCompactionMemoryFlusher? _memoryFlusher;
    private readonly ISessionCompactionCoordinator _compactionCoordinator;
    private readonly IConversationStore? _conversationStore;
    private readonly DefaultInboundMessageOrchestrator _orchestrator;
    private readonly IOptions<PlatformConfig>? _platformConfig;

    public GatewayHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager,
        ISessionCompactor compactor,
        IOptionsMonitor<CompactionOptions> compactionOptions,
        ILogger<GatewayHost> logger,
        int sessionQueueCapacity = DefaultInboundMessageOrchestrator.DefaultQueueCapacity,
        SessionLifecycleEvents? sessionLifecycleEvents = null,
        IMediaPipeline? mediaPipeline = null,
        IConversationDispatcher? conversationDispatcher = null,
        IConversationRouter? conversationRouter = null,
        PendingAskUserInterceptor? pendingAskUserInterceptor = null,
        IPreCompactionMemoryFlusher? memoryFlusher = null,
        IAgentRegistry? registry = null,
        ISessionCompactionCoordinator? compactionCoordinator = null,
        IConversationStore? conversationStore = null,
        IOptions<PlatformConfig>? platformConfig = null)
    {
        _supervisor = supervisor;
        _router = router;
        _sessions = sessions;
        _activity = activity;
        _channelManager = channelManager;
        _compactor = compactor;
        _compactionOptions = compactionOptions;
        _logger = logger;
        _mediaPipeline = mediaPipeline;
        _conversationDispatcher = conversationDispatcher;
        _conversationRouter = conversationRouter;
        _sessionLifecycleEvents = sessionLifecycleEvents;
        _pendingAskUserInterceptor = pendingAskUserInterceptor;
        _memoryFlusher = memoryFlusher;
        _registry = registry;
        _conversationStore = conversationStore;
        _platformConfig = platformConfig;
        // The coordinator is the single source of truth for compaction (PR #602
        // followup). Tests that construct GatewayHost directly may not provide
        // one; build a fallback from the deps we already have so behaviour
        // matches production.
        _compactionCoordinator = compactionCoordinator
            ?? new BotNexus.Gateway.Sessions.SessionCompactionCoordinator(
                compactor,
                sessions,
                supervisor,
                channelManager,
                compactionOptions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Sessions.SessionCompactionCoordinator>.Instance,
                memoryFlusher);
        // Sub-PR #696 (W-5 PR3): the per-session FIFO queue plus its bounded
        // backpressure now live in DefaultInboundMessageOrchestrator. GatewayHost
        // constructs the orchestrator with itself as the IInboundMessageProcessor
        // and exposes the instance via Orchestrator so DI can register the same
        // singleton for transports such as SignalR's GatewayHub (PR4).
        _orchestrator = new DefaultInboundMessageOrchestrator(
            this,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultInboundMessageOrchestrator>.Instance,
            channelManager,
            Math.Max(sessionQueueCapacity, 1));
    }

    /// <summary>
    /// Inbound-message orchestrator owning the per-session queue. Exposed so the
    /// composition root can register the SAME orchestrator instance under
    /// <see cref="IInboundMessageOrchestrator"/> for other transports (e.g. the
    /// SignalR GatewayHub in PR4) — every entry point shares one queue and one
    /// processor.
    /// </summary>
    public IInboundMessageOrchestrator Orchestrator => _orchestrator;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sessions is null)
        {
            _logger.LogWarning(
                "No ISessionStore is configured. Register an ISessionStore implementation in DI (e.g. services.AddSingleton<ISessionStore, InMemorySessionStore>()).");
            return;
        }

        foreach (var channel in _channelManager.Adapters)
        {
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

        _logger.LogInformation("Gateway started with {ChannelCount} channel adapter(s)", _channelManager.Adapters.Count);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        _logger.LogInformation("Gateway shutting down...");
        await _supervisor.StopAllAsync(CancellationToken.None);

        foreach (var channel in _channelManager.Adapters)
        {
            try { await channel.StopAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping channel adapter: {ChannelType}", channel.ChannelType); }
        }

        await _orchestrator.DisposeAsync();
    }

    /// <inheritdoc />
    public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        => _orchestrator.DispatchAsync(message, cancellationToken);

    /// <summary>
    /// <see cref="IInboundMessageProcessor"/> entry point invoked by the
    /// orchestrator's per-session worker. Runs resolution, session save, and
    /// agent execution and returns a <see cref="InboundProcessingOutcome"/>
    /// describing per-agent dispatch results and whether the queue should now
    /// close (e.g. session was sealed).
    /// </summary>
    public async Task<InboundProcessingOutcome> ProcessAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        var dispatches = new List<DispatchResult>();

        // Sub-PR 6.2 (#582): lift the legacy string-typed routing overrides into Vogen-typed
        // hints once at entry; every downstream telemetry tag / activity payload / fall-back
        // reads from the typed hints. This matches the pattern established for the conversation
        // dispatcher in 6.1 and is the structural prerequisite for sub-PR 6.3 deleting the
        // legacy fields from InboundMessage.
        var hints = InboundMessageRoutingHints.FromMessage(message);
        var requestedSessionIdValue = hints.RequestedSessionId?.Value;

        using var activity = GatewayDiagnostics.Source.StartActivity("gateway.dispatch", ActivityKind.Server);
        activity?.SetTag("botnexus.channel.type", message.ChannelType);
        activity?.SetTag("botnexus.session.id", requestedSessionIdValue);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());
        GatewayTelemetry.MessagesProcessed.Add(1,
            new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType),
            new KeyValuePair<string, object?>("botnexus.session.id", requestedSessionIdValue));

        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.MessageReceived,
            ChannelType = message.ChannelType,
            Message = message.Content,
            SessionId = requestedSessionIdValue
        }, cancellationToken);

        var targetAgents = await _router.ResolveAsync(message, cancellationToken);
        if (targetAgents.Count == 0)
        {
            _logger.LogWarning("No agent resolved for message from {ChannelType}:{SenderId}", message.ChannelType, message.SenderId);
            return new InboundProcessingOutcome(dispatches, ShouldClosePerSessionQueue: false);
        }

        foreach (var agentId in targetAgents)
        {
            var sessionId = requestedSessionIdValue ?? $"{message.ChannelType}:{message.ChannelAddress}:{agentId}";
            using var agentActivity = GatewayDiagnostics.Source.StartActivity("gateway.agent_process", ActivityKind.Internal);
            agentActivity?.SetTag("botnexus.agent.id", agentId);
            agentActivity?.SetTag("botnexus.session.id", sessionId);
            agentActivity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());
            agentActivity?.SetTag("botnexus.channel.type", message.ChannelType);

            using var executionActivity = GatewayDiagnostics.Source.StartActivity("gateway.agent.execution", ActivityKind.Internal);
            executionActivity?.SetTag("botnexus.agent.id", agentId);
            executionActivity?.SetTag("botnexus.session.id", sessionId);
            executionActivity?.SetTag("botnexus.channel.type", message.ChannelType);
            executionActivity?.SetTag("botnexus.trace.id", System.Diagnostics.Activity.Current?.TraceId.ToString());
            var executionTimer = Stopwatch.StartNew();

            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["AgentId"] = agentId,
                ["SessionId"] = sessionId,
                ["Channel"] = message.ChannelType,
                ["TraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
            });

            using var getOrCreateActivity = GatewayDiagnostics.Source.StartActivity("session.get_or_create", ActivityKind.Internal);
            getOrCreateActivity?.SetTag("botnexus.session.id", sessionId);
            getOrCreateActivity?.SetTag("botnexus.agent.id", agentId);
            var resolvedSource = new ChannelSource(
                message.ChannelType,
                message.ChannelAddress,
                message.SenderId,
                message.BindingId,
                DisplayPrefix: null);
            ConversationSessionResolution? resolution = null;
            var isInternalWakeMessage =
                message.ChannelType.Equals(ChannelKey.From("internal"));

            if (isInternalWakeMessage)
            {
                // Internal sub-agent wake-ups target an already-known parent session.
                // Routing them through conversation resolution creates synthetic internal
                // conversations and can misroute the user-visible response stream.
                var internalTargetSessionId = requestedSessionIdValue
                    ?? message.ChannelAddress.Value;

                if (!string.IsNullOrWhiteSpace(internalTargetSessionId))
                    sessionId = internalTargetSessionId;
            }
            else if (_conversationDispatcher is not null)
            {
                var dispatchResult = await _conversationDispatcher.DispatchAsync(
                    InboundMessageContext.FromInboundMessage(AgentId.From(agentId), message),
                    cancellationToken);
                sessionId = dispatchResult.Resolution.SessionId.Value;
                resolution = dispatchResult.Resolution;
                resolvedSource = dispatchResult.Source;
                message = message with
                {
                    BindingId = message.BindingId ?? resolvedSource.BindingId
                };
                dispatches.Add(dispatchResult);
            }
            else if (_conversationRouter is not null)
            {
                // Back-compat path while runtime callers migrate to dispatcher injection.
                var routingResult = await _conversationRouter.ResolveInboundAsync(
                    AgentId.From(agentId),
                    message.ChannelType,
                    message.ChannelAddress,
                    conversationId: hints.RequestedConversationId?.Value,
                    cancellationToken,
                    initiator: message.Sender);
                sessionId = routingResult.SessionId.Value;
                var originatingBinding = routingResult.OriginatingBinding;
                resolvedSource = originatingBinding is null
                    ? resolvedSource
                    : resolvedSource with
                    {
                        BindingId = originatingBinding.BindingId,
                        DisplayPrefix = originatingBinding.DisplayPrefix
                    };
                resolution = new ConversationSessionResolution(
                    routingResult.Conversation.ConversationId,
                    routingResult.SessionId,
                    IsNewConversation: false,
                    IsNewSession: routingResult.IsNewSession,
                    OriginatingBindingId: resolvedSource.BindingId,
                    DisplayPrefix: resolvedSource.DisplayPrefix);
                message = message with
                {
                    BindingId = message.BindingId ?? resolvedSource.BindingId
                };
                // Synthesise a DispatchResult for the back-compat router path so
                // the orchestrator's InboundDispatchResult uniformly reflects
                // EVERY routed agent regardless of which resolver branch ran.
                var routerContext = InboundMessageContext.FromInboundMessage(AgentId.From(agentId), message);
                dispatches.Add(new DispatchResult(routerContext, resolvedSource, resolution));
            }

            var existingSessionTask = _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
            var existingSession = existingSessionTask is null ? null : await existingSessionTask;
            var createdSession = existingSession is null;
            var session = existingSession ?? await _sessions.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId), cancellationToken);
            if (createdSession)
            {
                GatewayTelemetry.ActiveSessions.Add(1,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType));
            }
            session.ChannelType ??= message.ChannelType;
            // Update session ConversationId from dispatch resolution.
            // Always update (not just when null) so that switching conversations on the same
            // session correctly routes messages to the new conversation (#314).
            if (resolution is not null && session.ConversationId != resolution.ConversationId)
                session.ConversationId = resolution.ConversationId;
            session.CallerId ??= message.SenderId;
            session.SessionType = ResolveSessionType(session, message, isNewSession: createdSession);
            // P9-F (#657): participant add must run AFTER SaveAsync below so that the
            // legacy-conversation resolver inside SaveAsync has stamped session.ConversationId
            // (and CreateAsync'd the row in the conversation store). Adding before save would
            // race the orphan-stamp and silently no-op when the dispatcher hasn't resolved a
            // conversation up front.
            if (ShouldInitializeSystemPrompt(session))
            {
                // Force fresh handle creation so isolation strategy rebuilds system prompt from workspace files.
                var stopTask = _supervisor.StopAsync(AgentId.From(agentId), SessionId.From(sessionId), cancellationToken);
                if (stopTask is not null)
                    await stopTask;
            }

            if (session.Status is SessionStatus.Expired or SessionStatus.Sealed)
            {
                _logger.LogInformation("Reactivating {Status} session {SessionId}", session.Status, sessionId);
                session.Status = SessionStatus.Active;
                session.ExpiresAt = null;
            }
            else if (session.Status != SessionStatus.Active)
            {
                await SendSessionStatusRejectedAsync(message, agentId, sessionId, session.Status, cancellationToken);
                continue;
            }

            if (TryGetControlCommand(message, out var controlCommand))
            {
                if (string.Equals(controlCommand, ControlSteer, StringComparison.OrdinalIgnoreCase))
                {
                    if (await HandleSteeringAsync(message, agentId, sessionId, cancellationToken))
                        continue;
                    // Steering must not fall through to normal message processing.
                    // Steering is control-plane metadata; discard it rather than converting to a user prompt.
                    _logger.LogWarning("Steering discarded for session {SessionId} because agent is not running. Control messages must not enter the data plane.", sessionId);
                    continue;
                }
                else if (string.Equals(controlCommand, ControlCompact, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCompactionAsync(message, session, sessionId, cancellationToken);
                    continue;
                }
            }

            if (_pendingAskUserInterceptor is not null &&
                session.ConversationId.IsInitialized() &&
                _pendingAskUserInterceptor.TryIntercept(message, session.ConversationId))
            {
                _logger.LogInformation(
                    "Captured ask_user response for conversation {ConversationId}; skipping normal agent dispatch.",
                    session.ConversationId);
                continue;
            }

            IReadOnlyList<MessageContentPart>? originalParts = message.ContentParts;
            IReadOnlyList<MessageContentPart>? processedParts = null;

            if (originalParts is { Count: > 0 } && _mediaPipeline is not null)
            {
                var mediaContext = new MediaProcessingContext
                {
                    SessionId = sessionId,
                    ChannelType = message.ChannelType,
                    CancellationToken = cancellationToken
                };
                processedParts = await _mediaPipeline.ProcessAsync(originalParts, mediaContext);
            }

            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = message.Content,
                OriginalContentParts = originalParts,
                ProcessedContentParts = processedParts
            });

            // Write-ahead Layer 1: persist user message before starting the LLM call.
            // Ensures user input survives a gateway restart mid-turn (#363).
            await _sessions.SaveAsync(session, cancellationToken);

            // P9-F (#657): now that SaveAsync has run the legacy resolver and stamped
            // session.ConversationId (creating the row in the conversation store if needed),
            // it is safe to add the caller participant. AddParticipantsAsync no-ops when the
            // target conversation doesn't exist, so this MUST follow the first SaveAsync.
            await EnsureCallerParticipantAsync(session, message.Sender, cancellationToken).ConfigureAwait(false);

            if (_compactor.ShouldCompact(session.Session, _compactionOptions.CurrentValue))
            {
                _logger.LogInformation("Auto-compacting session {SessionId}", sessionId);
                try
                {
                    var outcome = await _compactionCoordinator.CompactAsync(session.AgentId, session, cancellationToken).ConfigureAwait(false);
                    if (outcome.Applied)
                    {
                        await _compactionCoordinator.TrySendChannelNotificationAsync(
                            outcome,
                            message.ChannelType,
                            message.ChannelAddress,
                            sessionId,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-compaction failed for session {SessionId}, continuing without compaction", sessionId);
                }
            }

            // Write-ahead Layer 2: crash sentinel written before the LLM call.
            // If the gateway restarts mid-turn, this sentinel survives in the session store,
            // showing an interrupted-turn marker to the user rather than a silent gap (#363).
            var crashSentinel = new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress — gateway restarted if visible]",
                IsCrashSentinel = true
            };
            session.AddEntry(crashSentinel);
            await _sessions.SaveAsync(session, cancellationToken);

            try
            {
                var handle = await _supervisor.GetOrCreateAsync(AgentId.From(agentId), SessionId.From(sessionId), cancellationToken);

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentProcessing,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);

                var sessionSaved = false;
                var agentDescriptor = _registry?.Get(AgentId.From(agentId));
                var resolvedChannel = ResolveChannelAdapter(message.ChannelType);
                _logger.LogInformation("Channel resolution: type='{ChannelType}' found={Found} streaming={Streaming} streamEvents={StreamEvents}",
                    message.ChannelType,
                    resolvedChannel is not null,
                    resolvedChannel?.SupportsStreaming,
                    resolvedChannel is IStreamEventChannelAdapter);

                if (resolvedChannel is { SupportsStreaming: true } channel)
                {
                    // Streaming uses the message's opaque ChannelAddress as the stream key.
                    // Adapters that need to disambiguate native sub-addresses (e.g. Telegram
                    // forum topics) already fold them into the address itself.
                    var streamingSource = resolvedSource;

                    // Resolve SignalR observer bindings for cross-channel live update (#332).
                    // When the originating channel is not SignalR (e.g. Telegram), any SignalR
                    // bindings on the conversation receive stream events so connected web
                    // clients update in real-time without a page reload.
                    IReadOnlyList<(IStreamEventChannelAdapter Adapter, ChannelStreamTarget Target)> signalRObservers = [];
                    if (_conversationRouter is not null && message.ChannelType.Value != "signalr")
                    {
                        try
                        {
                            var observerBindings = await _conversationRouter.GetOutboundBindingsAsync(
                                Domain.Primitives.SessionId.From(sessionId),
                                message.BindingId,
                                cancellationToken);

                            signalRObservers = observerBindings
                                .Where(b => b.ChannelType.Value == "signalr")
                                .Select(b =>
                                {
                                    var adapter = ResolveChannelAdapter(b.ChannelType) as IStreamEventChannelAdapter;
                                    var observerTarget = new ChannelStreamTarget(
                                        session.ConversationId,
                                        Domain.Primitives.SessionId.From(sessionId),
                                        b.ChannelAddress,
                                        b.BindingId);
                                    return (Adapter: adapter!, Target: observerTarget);
                                })
                                .Where(x => x.Adapter is not null)
                                .ToList();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to resolve SignalR observer bindings for session {SessionId}. Live update disabled for this turn.", sessionId);
                        }
                    }

                    var userMessage = BuildUserMessage(message, processedParts ?? originalParts, agentDescriptor);
                    await StreamingSessionHelper.ProcessAndSaveAsync(
                        handle.StreamAsync(userMessage, cancellationToken),
                        session,
                        _sessions,
                        new StreamingSessionOptions(
                            IncludeErrorsInHistory: true,
                            OnEventAsync: async (AgentStreamEvent evt, CancellationToken ct) =>
                            {
                                // Enrich with agentId so the client can route events
                                // even before session registration completes. Stamp the
                                // ConversationId too so subscribers can route by
                                // conversation (stable across compaction).
                                // Unconditional enrichment — the ?? fallbacks no-op when
                                // the event already carries the field. This shape also
                                // avoids triggering the Session.ConversationId-nullable
                                // architecture fence (which can't tell evt.ConversationId
                                // from session.ConversationId).
                                var enriched = evt with
                                {
                                    AgentId = evt.AgentId ?? Domain.Primitives.AgentId.From(agentId),
                                    SessionId = evt.SessionId ?? Domain.Primitives.SessionId.From(sessionId),
                                    ConversationId = evt.ConversationId ?? session.ConversationId
                                };

                                // Build the typed stream target the channel adapter uses to
                                // route this delta or event. Each adapter consumes the field
                                // that matches its routing semantics — see ChannelStreamTarget.
                                var streamTarget = new ChannelStreamTarget(
                                    session.ConversationId,
                                    Domain.Primitives.SessionId.From(sessionId),
                                    message.ChannelAddress,
                                    message.BindingId);

                                if (enriched.Type == AgentStreamEventType.UserInputRequired)
                                {
                                    await HandleUserInputRequiredAsync(
                                        message,
                                        sessionId,
                                        session.ConversationId,
                                        streamingSource,
                                        enriched,
                                        ct);
                                    return;
                                }

                                if (channel is IStreamEventChannelAdapter streamEventChannel)
                                    await streamEventChannel.SendStreamEventAsync(streamTarget, enriched, ct);
                                else if (evt.Type == AgentStreamEventType.ContentDelta && evt.ContentDelta is not null)
                                    await channel.SendStreamDeltaAsync(streamTarget, evt.ContentDelta, ct);

                                // Fan-out stream events to SignalR observer bindings (#332).
                                // Each observer gets the event keyed by its own typed target so the
                                // portal can route it correctly and exclude its own originating binding.
                                foreach (var (observerAdapter, observerTarget) in signalRObservers)
                                {
                                    try
                                    {
                                        await observerAdapter.SendStreamEventAsync(observerTarget, enriched, ct);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "SignalR observer fan-out failed for address {Address}. Skipping.", observerTarget.ChannelAddress);
                                    }
                                }
                            }),
                        _sessionLifecycleEvents,
                        cancellationToken);
                    sessionSaved = true;
                }
                else
                {
                    var userMessage = BuildUserMessage(message, processedParts ?? originalParts, agentDescriptor);
                    var response = await handle.PromptAsync(userMessage, cancellationToken);
                    if (IsHeartbeatAck(response.Content))
                    {
                        _logger.LogDebug("Heartbeat ack from agent '{AgentId}' session '{SessionId}'", agentId, sessionId);
                    }
                    else if (ResolveChannelAdapter(message.ChannelType) is { } ch)
                    {
                        await ch.SendAsync(new OutboundMessage
                        {
                            ChannelType = message.ChannelType,
                            ChannelAddress = message.ChannelAddress,
                            Content = response.Content,
                            SessionId = sessionId,
                            // Binding-aware fields from originating binding fix #126:
                            // ensure replies carry the binding's decoration. Native sub-addresses
                            // (e.g. Telegram forum topics) are already encoded in ChannelAddress.
                            BindingId = resolvedSource.BindingId,
                            DisplayPrefix = resolvedSource.DisplayPrefix
                        }, cancellationToken);
                    }

                    session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
                }

                // Remove crash sentinel on clean turn completion (#363).
                session.RemoveCrashSentinels();

                if (!sessionSaved)
                {
                    // Isolate transcript write from delivery success: a SaveAsync failure
                    // must not propagate as a delivery failure — the channel send already
                    // succeeded and retrying the outer operation would duplicate the reply
                    // to the user. Log a warning and continue (#756).
                    using var saveActivity = GatewayDiagnostics.Source.StartActivity("session.save", ActivityKind.Internal);
                    saveActivity?.SetTag("botnexus.session.id", session.SessionId);
                    saveActivity?.SetTag("botnexus.agent.id", session.AgentId);
                    try
                    {
                        await _sessions.SaveAsync(session, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Suppress: session is shutting down. Transcript loss is acceptable
                        // on clean cancellation; no retry, no duplicate send.
                        _logger.LogDebug(
                            "Session transcript save skipped (cancellation) for session '{SessionId}'",
                            sessionId);
                    }
                    catch (Exception saveEx)
                    {
                        // Delivery succeeded; log the transcript failure as a warning only.
                        // The caller must NOT retry the outer operation — the channel send
                        // already reached the user.
                        _logger.LogWarning(
                            saveEx,
                            "Session transcript save failed after successful channel send for session '{SessionId}'. " +
                            "Delivery was successful; transcript may be missing the last assistant turn.",
                            sessionId);
                    }
                }

                // Outbound fan-out: deliver response to other bindings in the conversation
                await FanOutResponseAsync(message, sessionId, cancellationToken);

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentCompleted,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);

                executionTimer.Stop();
                executionActivity?.SetStatus(ActivityStatusCode.Ok);
                GatewayTelemetry.AgentExecutionDurationMs.Record(executionTimer.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType),
                    new KeyValuePair<string, object?>("outcome", "success"));
                GatewayTelemetry.ProviderLatencyMs.Record(executionTimer.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                executionTimer.Stop();
                executionActivity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                GatewayTelemetry.AgentExecutionDurationMs.Record(executionTimer.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType),
                    new KeyValuePair<string, object?>("outcome", "cancelled"));
                _logger.LogInformation("Processing cancelled for agent '{AgentId}' session '{SessionId}' (client disconnected)", agentId, sessionId);
            }
            catch (Exception ex)
            {
                executionTimer.Stop();
                executionActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                GatewayTelemetry.AgentExecutionDurationMs.Record(executionTimer.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType),
                    new KeyValuePair<string, object?>("outcome", "error"));
                GatewayTelemetry.ProviderLatencyMs.Record(executionTimer.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType));
                _logger.LogError(ex, "Error processing message for agent '{AgentId}' session '{SessionId}'", agentId, sessionId);

                try
                {
                    if (ResolveChannelAdapter(message.ChannelType) is { } errorChannel)
                    {
                        await errorChannel.SendAsync(new OutboundMessage
                        {
                            ChannelType = message.ChannelType,
                            ChannelAddress = message.ChannelAddress,
                            Content = $"Error: {ex.Message}",
                            SessionId = sessionId
                        }, CancellationToken.None);
                    }
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Failed to send error response for agent '{AgentId}' session '{SessionId}'", agentId, sessionId);
                }

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.Error,
                    AgentId = agentId,
                    SessionId = sessionId,
                    Message = ex.Message
                }, CancellationToken.None);
            }
        }

        // Sub-PR 6.2 (#582): replicate the legacy CleanupQueueIfClosedSessionAsync
        // signal: if the message addressed an explicit session and that session
        // is now sealed, ask the orchestrator to close the per-session queue so
        // no further messages can be enqueued against a dead session.
        var shouldClosePerSessionQueue = false;
        if (hints.RequestedSessionId is { } sealedCheckId)
        {
            using var sessionActivity = GatewayDiagnostics.Source.StartActivity("session.get", ActivityKind.Internal);
            sessionActivity?.SetTag("botnexus.session.id", sealedCheckId.Value);

            var sealedCheckSession = await _sessions.GetAsync(sealedCheckId, cancellationToken);
            if (sealedCheckSession?.Status is SessionStatus.Sealed)
            {
                shouldClosePerSessionQueue = true;
            }
        }

        return new InboundProcessingOutcome(dispatches, shouldClosePerSessionQueue);
    }

    private async Task SendSessionStatusRejectedAsync(
        InboundMessage message,
        string agentId,
        string sessionId,
        GatewaySessionStatus status,
        CancellationToken cancellationToken)
    {
        var statusMessage = status == GatewaySessionStatus.Suspended
            ? "Session is suspended. Resume the session before sending new messages."
            : $"Session is in '{status}' state and cannot accept messages.";

        if (ResolveChannelAdapter(message.ChannelType) is { } channel)
        {
            await channel.SendAsync(new OutboundMessage
            {
                ChannelType = message.ChannelType,
                ChannelAddress = message.ChannelAddress,
                Content = statusMessage,
                SessionId = sessionId
            }, cancellationToken);
        }

        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.Error,
            AgentId = agentId,
            SessionId = sessionId,
            Message = statusMessage
        }, cancellationToken);
    }

    private async Task<bool> HandleSteeringAsync(
        InboundMessage message,
        string agentId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(AgentId.From(agentId), SessionId.From(sessionId));
        IAgentHandle? handle = null;

        if (instance is not null)
        {
            try
            {
                handle = await _supervisor.GetOrCreateAsync(AgentId.From(agentId), SessionId.From(sessionId), cancellationToken);
            }
            catch { /* instance exists but handle creation failed */ }
        }

        // If no handle or agent is not actively running, steering can't be
        // injected mid-turn. Return false so the caller falls through to
        // normal message processing (avoiding a recursive DispatchAsync
        // deadlock on the single-reader session queue).
        if (handle is null || !handle.IsRunning)
        {
            _logger.LogInformation(
                "Steering received but agent is not running (instance={HasInstance}, running={IsRunning}). Steering will be discarded for session {SessionId}.",
                instance is not null, handle?.IsRunning ?? false, sessionId);

            await _activity.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.SteeringQueued,
                AgentId = agentId,
                SessionId = sessionId
            }, cancellationToken);

            return false;
        }

        // Record steering message in session history
        var session = await _sessions.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId), cancellationToken);
        IReadOnlyList<MessageContentPart>? originalParts = message.ContentParts;
        IReadOnlyList<MessageContentPart>? processedParts = null;

        if (originalParts is { Count: > 0 } && _mediaPipeline is not null)
        {
            var mediaContext = new MediaProcessingContext
            {
                SessionId = sessionId,
                ChannelType = message.ChannelType,
                CancellationToken = cancellationToken
            };
            processedParts = await _mediaPipeline.ProcessAsync(originalParts, mediaContext);
        }

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = message.Content,
            OriginalContentParts = originalParts,
            ProcessedContentParts = processedParts
        });
        await _sessions.SaveAsync(session, cancellationToken);

        await handle.SteerAsync(message.Content, cancellationToken);

        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.SteeringInjected,
            AgentId = agentId,
            SessionId = sessionId
        }, cancellationToken);

        _logger.LogInformation("Steering message injected for agent {AgentId} session {SessionId}", agentId, sessionId);
        return true;
    }

    private async Task HandleCompactionAsync(
        InboundMessage message,
        GatewaySession session,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var outcome = await _compactionCoordinator.CompactAsync(session.AgentId, session, cancellationToken).ConfigureAwait(false);
        // Always notify on this path — channel-driven /compact callers expect feedback
        // even on failure so the user knows the command landed. Use the canonical
        // text (including the FailureReason when applicable).
        await _compactionCoordinator.TrySendChannelNotificationAsync(
            outcome,
            message.ChannelType,
            message.ChannelAddress,
            sessionId,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetControlCommand(InboundMessage message, out string? command)
    {
        command = null;
        if (!message.Metadata.TryGetValue("control", out var controlValue))
            return false;

        command = controlValue?.ToString();
        return !string.IsNullOrWhiteSpace(command);
    }

    /// <summary>
    /// Indicates whether the system prompt should be (re)loaded for this session's
    /// next agent invocation. Returns true exactly when the session has no history
    /// entries — the natural invariant for "no LLM call has been made yet against
    /// this session." Phase 3d (issue #537) replaced an explicit prompt-initialised
    /// metadata flag with this invariant; it is safe because compaction now
    /// <em>marks</em> historical entries rather than deleting them (Phase 3a, #531)
    /// and reset creates a fresh empty session in the same conversation (Phase 3c,
    /// #536), so a non-empty <c>History</c> is a reliable signal that prompt
    /// initialisation has already happened.
    /// </summary>
    private static bool ShouldInitializeSystemPrompt(GatewaySession session)
        => session.History.Count == 0;

    private static bool IsHeartbeatAck(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var trimmed = response.Trim();
        return trimmed.Equals("HEARTBEAT_OK", StringComparison.Ordinal)
               || trimmed.StartsWith("HEARTBEAT_OK", StringComparison.Ordinal);
    }

    private async Task HandleUserInputRequiredAsync(
        InboundMessage message,
        string sessionId,
        ConversationId conversationId,
        ChannelSource source,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        var request = streamEvent.UserInputRequest;

        if (ResolveChannelAdapter(message.ChannelType) is { } sourceAdapter)
            await SendAskUserToBindingAsync(sourceAdapter, source, sessionId, conversationId, streamEvent, request, cancellationToken);

        if (_conversationRouter is null)
            return;

        var outboundBindings = await _conversationRouter.GetOutboundBindingsAsync(
            SessionId.From(sessionId),
            source.BindingId,
            cancellationToken);

        foreach (var binding in outboundBindings)
        {
            var adapter = ResolveChannelAdapter(binding.ChannelType, binding.AdapterId);
            if (adapter is null)
                continue;

            var bindingSource = new ChannelSource(
                binding.ChannelType,
                binding.ChannelAddress,
                message.SenderId,
                binding.BindingId,
                binding.DisplayPrefix);
            await SendAskUserToBindingAsync(adapter, bindingSource, sessionId, conversationId, streamEvent, request, cancellationToken);
        }
    }

    private static async Task SendAskUserToBindingAsync(
        IChannelAdapter adapter,
        ChannelSource source,
        string sessionId,
        ConversationId conversationId,
        AgentStreamEvent streamEvent,
        AskUserRequest? request,
        CancellationToken cancellationToken)
    {
        if (adapter is IStreamEventChannelAdapter streamAdapter)
        {
            var target = new ChannelStreamTarget(
                conversationId,
                SessionId.From(sessionId),
                source.ChannelAddress,
                source.BindingId);
            await streamAdapter.SendStreamEventAsync(target, streamEvent, cancellationToken);
            return;
        }

        if (request is null)
            return;

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = source.ChannelType,
            ChannelAddress = source.ChannelAddress,
            Content = FormatAskUserFallbackPrompt(request),
            SessionId = sessionId,
            BindingId = source.BindingId,
            DisplayPrefix = source.DisplayPrefix
        }, cancellationToken);
    }

    private static string FormatAskUserFallbackPrompt(AskUserRequest request)
    {
        var lines = new List<string>
        {
            "❓ Agent question:",
            request.Prompt
        };

        if (request.Choices is { Count: > 0 })
        {
            lines.Add(string.Empty);
            for (var index = 0; index < request.Choices.Count; index++)
            {
                var choice = request.Choices[index];
                var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label;
                lines.Add($"{index + 1}. {label}");
            }
        }

        lines.Add(string.Empty);
        lines.Add(request.AllowMultiple
            ? "Reply with one or more answers."
            : "Reply with your answer.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Drains any session queue workers that were started by the orchestrator
    /// but never cleaned up via the BackgroundService shutdown path (e.g., in
    /// unit tests where ExecuteAsync was never invoked).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        base.Dispose();
    }

    private async Task EnsureCallerParticipantAsync(GatewaySession session, CitizenId callerCitizenId, CancellationToken cancellationToken)
    {
        if (!callerCitizenId.IsValid)
            return;

        // Skip when the conversation has not yet been pinned to the session (legacy/dispatch
        // races) — without a ConversationId we have nothing to merge against. Skip when no
        // conversation store is wired (legacy unit-test compositions); the architecture fence
        // pins direct Session.Participants mutations as removed, so the no-store path simply
        // becomes a no-op rather than reaching back into the deleted field.
        if (!session.ConversationId.IsInitialized() || _conversationStore is null)
            return;

        await _conversationStore.AddParticipantsAsync(
            session.ConversationId,
            [new SessionParticipant { CitizenId = callerCitizenId }],
            cancellationToken).ConfigureAwait(false);
    }

    private SessionType ResolveSessionType(GatewaySession session, InboundMessage message, bool isNewSession)
    {
        // Phase 5 / F-6 step 2 (#554): sub-agent classification is driven by the
        // typed AgentDescriptor.Kind signal sourced from IAgentRegistry rather than
        // the legacy SessionId.IsSubAgent substring check. The substring path is
        // deleted from this method — pinned by AgentKindArchitectureTests.
        var descriptor = _registry?.Get(session.AgentId);
        if (descriptor is { Kind: AgentKind.SubAgent })
            return SessionType.AgentSubAgent;

        // Fail-closed preservation: an EXISTING session previously stamped as
        // AgentSubAgent stays AgentSubAgent when no typed signal contradicts it.
        // This guards the gateway-restart and post-Unregister windows where the
        // sub-agent descriptor is transiently absent from the in-memory registry
        // (DefaultSubAgentManager.OnChildCompleteAsync calls _registry.Unregister
        // when a sub-agent finishes). Silently downgrading to UserAgent would
        // flip Session.IsInteractive and warmup/memory-flush gating downstream.
        // Note: we explicitly require isNewSession=false so this does NOT re-open
        // a substring back door for fresh dispatches — first-classification of a
        // sub-agent-shaped SessionId still depends on the typed registry signal.
        if (!isNewSession && descriptor is null && session.SessionType == SessionType.AgentSubAgent)
            return SessionType.AgentSubAgent;

        // P9-E (#645): Soul/Cron/Heartbeat SessionType discriminators collapsed.
        // The Soul/Cron channel and SessionId.IsSoul substring branches are deleted
        // here too — triggers stamp their proxy origin on SessionEntry.Trigger, and
        // Session.IsInteractive carries the cron-channel exclusion downstream.
        return SessionType.UserAgent;
    }

    /// <summary>
    /// Best-effort outbound fan-out to other conversation bindings after a response is delivered.
    /// </summary>
    private async Task FanOutResponseAsync(
        InboundMessage message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_conversationRouter is null)
            return;

        try
        {
            var otherBindings = await _conversationRouter.GetOutboundBindingsAsync(
                SessionId.From(sessionId),
                message.BindingId,
                cancellationToken);

            if (otherBindings.Count == 0)
                return;

            // Get last assistant message from session history
            var session = await _sessions.GetAsync(SessionId.From(sessionId), cancellationToken);
            var lastAssistantEntry = session?.GetHistorySnapshot()
                .LastOrDefault(e => e.Role == MessageRole.Assistant);

            if (lastAssistantEntry is null)
                return;

            foreach (var binding in otherBindings)
            {
                try
                {
                    var adapter = ResolveChannelAdapter(binding.ChannelType, binding.AdapterId);
                    if (adapter is null)
                    {
                        _logger.LogWarning(
                            "Fan-out: no channel adapter for type '{ChannelType}' (binding {BindingId}). Skipping.",
                            binding.ChannelType,
                            binding.BindingId);
                        continue;
                    }

                    await adapter.SendAsync(new OutboundMessage
                    {
                        ChannelType = binding.ChannelType,
                        ChannelAddress = binding.ChannelAddress,
                        Content = lastAssistantEntry.Content,
                        SessionId = sessionId,
                        // Binding-aware fields: let the adapter render prefix decoration when
                        // configured. Native sub-addresses (e.g. Telegram forum topics) are
                        // already encoded in ChannelAddress by the originating adapter.
                        BindingId = binding.BindingId,
                        DisplayPrefix = binding.DisplayPrefix
                    }, cancellationToken);

                    _logger.LogDebug(
                        "Fan-out delivered to {ChannelType}:{ChannelAddress} for session {SessionId}",
                        binding.ChannelType, binding.ChannelAddress, sessionId);
                }
                catch (BotNexus.Gateway.Abstractions.Channels.StaleChannelConnectionException ex)
                {
                    // Self-heal: demote stale bindings to Muted so future fan-outs skip them.
                    _logger.LogWarning(
                        ex,
                        "Fan-out: stale connection for binding {BindingId} in conversation {ConversationId}. Demoting to Muted.",
                        ex.BindingId, ex.ConversationId);

                    if (session is not null && session.ConversationId.IsInitialized())
                        await _conversationRouter.MuteBindingAsync(session.ConversationId, ex.BindingId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Fan-out failed for binding {BindingId} ({ChannelType}:{ChannelAddress}). Continuing.",
                        binding.BindingId, binding.ChannelType, binding.ChannelAddress);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out resolution failed for session {SessionId}. Continuing.", sessionId);
        }
    }

    private IChannelAdapter? ResolveChannelAdapter(ChannelKey channelType, string? adapterId = null)
    {
        var adapter = _channelManager.Get(channelType, adapterId);
        if (adapter is not null)
            return adapter;

        _logger.LogWarning("No channel adapter found for type '{ChannelType}' (adapterId: '{AdapterId}'). Available: {Available}",
            channelType,
            adapterId ?? "<any>",
            string.Join(", ", _channelManager.Adapters.Select(a => a.ChannelType)));
        return null;
    }

    /// <summary>
    /// Builds a <see cref="AgentUserMessage"/> from an inbound message, attaching any image
    /// content parts as <see cref="AgentImageContent"/> so vision-capable models receive them.
    /// Falls back to a plain text <see cref="AgentUserMessage"/> when no image parts are present.
    /// When datetime injection is configured, prepends the current datetime to the message content
    /// inside a <c>&lt;currentdatetime&gt;</c> XML tag before sending to the provider.
    /// The raw session-store entry is NOT modified.
    /// </summary>
    private AgentUserMessage BuildUserMessage(
        InboundMessage message,
        IReadOnlyList<MessageContentPart>? contentParts,
        AgentDescriptor? descriptor = null)
    {
        var content = InjectDateTimeIfEnabled(message.Content, descriptor);
        var images = BuildImageContent(contentParts);
        return images is { Count: > 0 }
            ? new AgentUserMessage(content, images)
            : new AgentUserMessage(content);
    }

    /// <summary>
    /// Resolves the effective datetime injection config for the given agent descriptor,
    /// applies it to the raw user content, and returns the (possibly prefixed) result.
    /// When injection is disabled or not configured, returns the original content unchanged.
    /// </summary>
    internal string InjectDateTimeIfEnabled(string content, AgentDescriptor? descriptor)
    {
        var worldInjection = _platformConfig?.Value.Gateway?.DateTimeInjection;
        var agentInjection = descriptor?.DateTimeInjection;

        // Per-agent override supersedes world default when present.
        // An agent with Enabled=false explicitly disables injection even if world default is on.
        var effective = agentInjection ?? worldInjection;
        if (effective is null || !effective.Enabled)
            return content;

        // Resolve timezone: agent override > world DateTimeInjection timezone > world DefaultTimezone > UTC.
        var tzId = agentInjection?.Timezone
            ?? worldInjection?.Timezone
            ?? _platformConfig?.Value.Gateway?.DefaultTimezone
            ?? "UTC";

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var offset = tz.GetUtcOffset(now);
        var offsetStr = offset >= TimeSpan.Zero
            ? $"+{offset:hh\\:mm}"
            : $"-{offset.Negate():hh\\:mm}";
        var dateTimeStr = $"{now:yyyy-MM-ddTHH:mm:ss}{offsetStr} ({tzId})";

        return $"<currentdatetime>{dateTimeStr}</currentdatetime>\n{content}";
    }

    private static IReadOnlyList<AgentImageContent>? BuildImageContent(
        IReadOnlyList<MessageContentPart>? contentParts)
    {
        if (contentParts is null or { Count: 0 })
            return null;

        List<AgentImageContent>? images = null;
        foreach (var part in contentParts)
        {
            AgentImageContent? imageContent = part switch
            {
                // Inline binary — convert to base64 data URI
                BinaryContentPart bin when bin.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    => new AgentImageContent($"data:{bin.MimeType};base64,{Convert.ToBase64String(bin.Data)}"),
                // External URL reference
                ReferenceContentPart refPart when refPart.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    => new AgentImageContent(refPart.Uri),
                _ => null
            };

            if (imageContent is not null)
            {
                images ??= [];
                images.Add(imageContent);
            }
        }

        return images;
    }
}

