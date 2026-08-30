using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Channels.Startup;
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
using BotNexus.Gateway.Diagnostics;
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
using MessageKind = BotNexus.Domain.Primitives.MessageKind;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BotNexus.Agent.Providers.Core;
using BotNexus.Gateway.Services;

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
    private readonly ISessionContextWindowResolver? _contextWindowResolver;
    private readonly Sessions.IContextExhaustionNotifier _contextExhaustionNotifier;
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
    private readonly ConversationAutoTitleService? _autoTitleService;
    private readonly IActivityTracker? _activityTracker;
    private readonly IActiveLoopTracker? _activeLoopTracker;
    private readonly GatewayAuthManager? _authManager;
    private readonly IOutboundResponseDeliverer _deliverer;
    private readonly Sessions.ISessionTurnTracker _turnTracker;
    private readonly ChannelStartupReport _startupReport;

    /// <summary>
    /// The single execution-layer tool-audit sink (#2614), used by the blocking branch of
    /// <c>ProcessAsync</c> so a non-streamed turn records the same tool timeline a streamed one
    /// does (#2616 AC3).
    /// </summary>
    private readonly Audit.IToolAuditSink _toolAudit;

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
        IOptions<PlatformConfig>? platformConfig = null,
        LlmClient? llmClient = null,
        IConversationChangeNotifier? conversationChangeNotifier = null,
        IActivityTracker? activityTracker = null,
        IActiveLoopTracker? activeLoopTracker = null,
        GatewayAuthManager? authManager = null,
        IOutboundResponseDeliverer? outboundResponseDeliverer = null,
        Sessions.ISessionTurnTracker? turnTracker = null,
        ChannelStartupReport? startupReport = null,
        ISessionContextWindowResolver? contextWindowResolver = null,
        Audit.IToolAuditSink? toolAudit = null,
        Sessions.IContextExhaustionNotifier? contextExhaustionNotifier = null)
    {
        _toolAudit = toolAudit ?? Audit.DefaultToolAuditSink.Instance;
        _contextWindowResolver = contextWindowResolver;
        // #3535: a run that ends on an empty assistant completion because the context window is
        // exhausted must say so instead of presenting as a blank turn. Built from the collaborators
        // already in hand when DI did not supply one, so a directly-constructed host behaves the
        // same as the wired one; it judges against the SAME scope-resolved window as compaction.
        _contextExhaustionNotifier = contextExhaustionNotifier
            ?? new Sessions.ContextExhaustionNotifier(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Sessions.ContextExhaustionNotifier>.Instance,
                contextWindowResolver);
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
        _activityTracker = activityTracker;
        _activeLoopTracker = activeLoopTracker;
        _authManager = authManager;
        // Live-turn tracker powers write-time self-heal of orphaned crash sentinels (#2030).
        // Tests that construct GatewayHost directly may not supply one; a fresh tracker keeps
        // behaviour identical (no live turns tracked from other GatewayHost instances).
        _turnTracker = turnTracker ?? new Sessions.SessionTurnTracker();
        _startupReport = startupReport ?? new ChannelStartupReport();
        // Outbound fan-out delivery is a focused collaborator (#1811). Tests that construct
        // GatewayHost directly may not provide one; build a fallback from the deps we already
        // have so behaviour matches production. When no conversation router is configured there
        // is nothing to fan out to, so a null-object deliverer preserves the prior no-op path.
        _deliverer = outboundResponseDeliverer
            ?? (conversationRouter is not null
                ? new OutboundResponseDeliverer(
                    conversationRouter,
                    channelManager,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboundResponseDeliverer>.Instance)
                : NullOutboundResponseDeliverer.Instance);
        // Wire up the auto-title service when the required dependencies are present. The auth
        // manager is threaded through so the titling call applies the same per-provider
        // API-endpoint override the live agent path uses (#1636).
        if (llmClient is not null && conversationStore is not null)
        {
            _autoTitleService = new ConversationAutoTitleService(
                conversationStore,
                llmClient,
                _logger,
                conversationChangeNotifier,
                _authManager);
        }
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
            Math.Max(sessionQueueCapacity, 1),
            // #3028: the steer-vs-queue decision moves server-side. Both collaborators are built from
            // dependencies GatewayHost already holds, so every transport that reaches the orchestrator
            // - channel adapters, the SignalR hub, the REST endpoints - resolves delivery through ONE
            // seam instead of each caller choosing a mechanism for itself.
            new DefaultInboundDeliveryResolver(supervisor),
            new AgentHandleSteerDeliverer(
                supervisor,
                sessions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentHandleSteerDeliverer>.Instance));
    }

    /// <summary>
    /// Inbound-message orchestrator owning the per-session queue. Exposed so the
    /// composition root can register the SAME orchestrator instance under
    /// <see cref="IInboundMessageOrchestrator"/> for other transports (e.g. the
    /// SignalR GatewayHub in PR4) — every entry point shares one queue and one
    /// processor.
    /// </summary>
    public IInboundMessageOrchestrator Orchestrator => _orchestrator;

    /// <summary>
    /// Per-adapter startup outcomes recorded by the most recent host start (#2447).
    /// </summary>
    /// <remarks>
    /// Empty until <c>ExecuteAsync</c> has run its startup pass. This is the honest signal that
    /// distinguishes adapters CONFIGURED from adapters actually STARTED: an adapter whose start
    /// failed after its bounded retries appears here with <c>Started == false</c> and the
    /// classified failure, rather than being silently folded into a single success count.
    /// </remarks>
    public IReadOnlyList<ChannelStartOutcome> ChannelStartOutcomes => _channelStartOutcomes;

    private IReadOnlyList<ChannelStartOutcome> _channelStartOutcomes = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sessions is null)
        {
            _logger.LogWarning(
                "No ISessionStore is configured. Register an ISessionStore implementation in DI (e.g. services.AddSingleton<ISessionStore, InMemorySessionStore>()).");
            return;
        }

        // #2447: adapter start is retried with bounded, classification-aware backoff. A single
        // transient upstream 502 used to disable a channel for the whole process lifetime because
        // start was one-shot (catch, log, move on) - and the host still reported success.
        var startupCoordinator = new ChannelStartupCoordinator(_logger);
        var startOutcomes = await startupCoordinator.StartAllAsync(_channelManager.Adapters, this, stoppingToken);

        _channelStartOutcomes = startOutcomes;

        // #2447 clause 4: publish the real per-adapter result so GET /api/channels/health can
        // distinguish adapters configured from adapters started and name the ones that failed.
        // Recording it only in a private field left a dead channel diagnosable solely from logs.
        _startupReport.Record(startOutcomes);

        var degraded = startOutcomes.Any(o => !o.Started);
        if (degraded)
            _logger.LogWarning("{Summary}", ChannelStartupCoordinator.DescribeStartup(startOutcomes));
        else
            _logger.LogInformation("{Summary}", ChannelStartupCoordinator.DescribeStartup(startOutcomes));

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
        _activityTracker?.RecordActivity();
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
            var typedAgentId = AgentId.From(agentId);
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

            // #1381 (increment 1): the 3-branch conversation/session resolution
            // (internal-wake vs dispatcher vs back-compat router) is extracted into
            // ResolveConversationSessionAsync so ProcessAsync no longer inlines it. The
            // method is a pure transform of the resolution inputs into
            // (sessionId, resolution, resolvedSource, possibly-rewritten message, an
            // optional DispatchResult); applying those outputs here preserves the exact
            // prior control flow and mutation order.
            var resolved = await ResolveConversationSessionAsync(
                message, typedAgentId, requestedSessionIdValue, hints, sessionId, cancellationToken);
            sessionId = resolved.SessionId;
            var resolution = resolved.Resolution;
            var resolvedSource = resolved.ResolvedSource;
            message = resolved.Message;
            if (resolved.Dispatch is not null)
                dispatches.Add(resolved.Dispatch);

            var typedSessionId = SessionId.From(sessionId);

            var existingSessionTask = _sessions.GetAsync(typedSessionId, cancellationToken);
            var existingSession = existingSessionTask is null ? null : await existingSessionTask;
            var createdSession = existingSession is null;
            var session = existingSession ?? await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, cancellationToken);
            if (createdSession)
            {
                GatewayTelemetry.ActiveSessions.Add(1,
                    new KeyValuePair<string, object?>("botnexus.agent.id", agentId),
                    new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType));
            }

            // Write-time self-heal of orphaned crash sentinels (#2030). A turn that died
            // mid-flight (sub-agent bail, provider stream death, host sleep, unhandled crash)
            // leaves a crash sentinel that wedges the session: every later user message queues
            // behind a phantom "turn in progress" that never completes, and the startup scan
            // only runs on restart. When an inbound message arrives for a session that carries a
            // sentinel but has NO live in-memory turn, the sentinel is provably an orphan (the
            // per-session FIFO queue guarantees no concurrent turn for this session is running),
            // so clear it and proceed - the session unblocks the instant the user retries, no
            // restart required. If a turn IS live the sentinel is legitimate and left untouched.
            if (!_turnTracker.HasLiveTurn(sessionId)
                && session.History.Any(static e => e.IsCrashSentinel))
            {
                _logger.LogInformation(
                    "Self-healing orphaned crash sentinel for session '{SessionId}' (no live turn); "
                    + "clearing so the interrupted turn unblocks without a restart (#2030).",
                    sessionId);
                session.RemoveCrashSentinels();
            }
            session.ChannelType ??= message.ChannelType;
            // Carry the connecting client kind (e.g. SignalR "mobile" vs "desktop") from the
            // inbound message onto the session so the system-prompt builder can surface it on
            // the runtime line. Only stamp when the channel supplied a non-empty value; absent
            // metadata leaves the session untouched so non-signalr channels stay unchanged (#1209).
            if (message.Metadata.TryGetValue("clientKind", out var clientKindValue)
                && clientKindValue is string clientKind
                && !string.IsNullOrWhiteSpace(clientKind))
            {
                session.Metadata["clientKind"] = clientKind;
            }
            // Update session ConversationId from dispatch resolution.
            // Always update (not just when null) so that switching conversations on the same
            // session correctly routes messages to the new conversation (#314).
            if (resolution is not null && session.ConversationId != resolution.ConversationId)
                session.ConversationId = resolution.ConversationId;

            // #3065: a conversation-scoped event must NEVER be emitted without a conversation id.
            // Everything downstream of this point (stream events, ask-user prompts, steering
            // feedback) is conversation-scoped, and every one of those emit sites sources its id
            // from session.ConversationId. When neither the dispatcher nor the back-compat router
            // resolved a conversation - a channel that genuinely has none yet - mint one here via
            // the same server-side seam POST /conversations uses, rather than emitting an
            // unattributed event and leaving the receiver to guess from ambient client state.
            // The mint is a no-op on every path that already resolved a conversation.
            await EnsureConversationForEmissionAsync(session, typedAgentId, message, cancellationToken)
                .ConfigureAwait(false);

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
                var stopTask = _supervisor.StopAsync(typedAgentId, typedSessionId, cancellationToken);
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
                await SendSessionStatusRejectedAsync(message, typedAgentId, typedSessionId, session.Status, cancellationToken);
                continue;
            }

            if (TryGetControlCommand(message, out var controlCommand))
            {
                if (string.Equals(controlCommand, ControlSteer, StringComparison.OrdinalIgnoreCase))
                {
                    if (await HandleSteeringAsync(message, typedAgentId, typedSessionId, session.ConversationId, cancellationToken))
                        continue;
                    // Steering must not fall through to normal message processing.
                    // Steering is control-plane metadata; discard it rather than converting to a user prompt.
                    _logger.LogWarning("Steering discarded for session {SessionId} because agent is not running. Control messages must not enter the data plane.", sessionId);
                    continue;
                }
                else if (string.Equals(controlCommand, ControlCompact, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCompactionAsync(message, session, typedSessionId, cancellationToken);
                    continue;
                }
            }

            if (_pendingAskUserInterceptor is not null &&
                session.ConversationId.IsInitialized() &&
                await _pendingAskUserInterceptor.TryInterceptAsync(message, session.ConversationId, cancellationToken).ConfigureAwait(false))
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

            // #2149: derive the orthogonal typed kinds ONCE for this turn. The inbound entry is
            // stamped with the message's own kind (e.g. subagent-completion for the internal
            // completion notification); the parent agent's response entry produced while handling
            // a subagent-completion is stamped subagent-response so channels can distinguish the
            // three cases (message / subagent-completion / subagent-response) on both live delivery
            // and history replay - without re-parsing role, ids, or text. Ordinary turns resolve to
            // MessageKind.Message and store as null (the default), so nothing else changes.
            var inboundKind = ResolveInboundKind(message);
            var responseKind = inboundKind.Equals(MessageKind.SubAgentCompletion)
                ? MessageKind.SubAgentResponse
                : MessageKind.Message;

            session.AddEntry(new SessionEntry
            {
                // Hybrid rule (#1650): an agent posting into a channel (e.g. via the
                // conversation tool, cross-channel relays) defaults to the assistant
                // role when speaking as itself; a human sender stays user; an explicit
                // InboundMessage.SpeakAs override wins. Derived once on the message so
                // this entry and the producer's local copy cannot diverge.
                Role = message.DeriveChannelPostRole(),
                Content = message.Content,
                OriginalContentParts = originalParts,
                ProcessedContentParts = processedParts,
                // Persist null for the default so legacy/ordinary rows stay unstamped (#2149).
                Kind = StampKind(inboundKind)
            });

            // Write-ahead Layer 1: persist user message before starting the LLM call.
            // Ensures user input survives a gateway restart mid-turn (#363).
            await _sessions.SaveAsync(session, cancellationToken);

            // P9-F (#657): now that SaveAsync has run the legacy resolver and stamped
            // session.ConversationId (creating the row in the conversation store if needed),
            // it is safe to add the caller participant. AddParticipantsAsync no-ops when the
            // target conversation doesn't exist, so this MUST follow the first SaveAsync.
            await EnsureCallerParticipantAsync(session, message.Sender, cancellationToken).ConfigureAwait(false);

            // #2126: generate a PROVISIONAL conversation title from the first user message now,
            // before the assistant turn runs, so a human-agent conversation stops showing
            // "New conversation" almost immediately - even for a long, tool-heavy first turn or a
            // turn that is cancelled before completion. Best-effort and fire-and-forget: a title
            // failure never delays or fails the foreground turn (the post-response path still runs
            // and refines the provisional title once when the assistant response completes).
            TryTriggerProvisionalTitle(session, typedAgentId, message);

            // #1503 (increment 2, follow-up to #1381): the pre-execution "prepare turn"
            // concern -- auto-compaction, abandoned-turn detection (#790), and the
            // write-ahead crash sentinel (#363) -- is extracted into PrepareTurnAsync so
            // ProcessAsync no longer inlines it. The method mutates+persists the session in
            // the exact prior order (compaction -> abandoned-turn handle-stop+notify ->
            // crash sentinel save); none of its locals escape into the execution path below,
            // so applying it here preserves the prior control flow and side-effect ordering.
            // Mark this session as having a live turn for the duration of execution so the
            // write-time self-heal above never mistakes a genuinely in-flight turn's sentinel
            // for an orphan (#2030). Disposed at the end of this per-agent iteration.
            using var turnScope = _turnTracker.BeginTurn(sessionId);
            await PrepareTurnAsync(session, message, typedAgentId, typedSessionId, sessionId, cancellationToken);

            // #1518: capture the run's authoritative session identity (id + conversation) NOW,
            // before the agent run starts. Every post-run finalizer write below is fenced against
            // it so a delete/reset that lands while the run is in flight cannot be undone by the
            // finalizer resurrecting or clobbering the row.
            var runFence = SessionWriteFence.Capture(session);

            try
            {
                var handle = await _supervisor.GetOrCreateAsync(typedAgentId, typedSessionId, cancellationToken);

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentProcessing,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);

                var sessionSaved = false;
                var agentDescriptor = _registry?.Get(typedAgentId);
                var resolvedChannel = ResolveChannelAdapter(message.ChannelType);
                var shouldStream = resolvedChannel is not null && message.StreamResponse switch
                {
                    true => resolvedChannel is IStreamEventChannelAdapter,
                    false => false,
                    null => resolvedChannel.SupportsStreaming,
                };
                _logger.LogInformation("Channel resolution: type='{ChannelType}' found={Found} streaming={Streaming} streamEvents={StreamEvents} requested={RequestedStreaming} selected={SelectedStreaming}",
                    message.ChannelType,
                    resolvedChannel is not null,
                    resolvedChannel?.SupportsStreaming,
                    resolvedChannel is IStreamEventChannelAdapter,
                    message.StreamResponse,
                    shouldStream);

                // #2388: busy-agent boundary policy. An inbound message that lands while this
                // agent's previous turn is still in flight used to be pushed straight at the
                // running agent, tripping Agent.RunAsync's (correct) single-turn guard with
                // `InvalidOperationException: Agent is already running`. That exception escaped
                // into the generic catch below - logged as an ERR, with the message discarded.
                // Both entry paths below reproduced it independently in production: the streaming
                // path (InProcessAgentHandle.StreamCoreAsync) and the blocking path
                // (InProcessAgentHandle.PromptAsync), so the policy is applied ONCE here, above
                // the stream/prompt branch, rather than per-path.
                //
                // The policy is option 1 from the issue - QUEUE - and it deliberately reuses the
                // existing follow-up seam introduced for #2438 (IAgentHandle.
                // TryFollowUpWhileRunningAsync) rather than inventing a second queue. That
                // primitive already makes the running/idle decision atomically: it enqueues first
                // and re-verifies, so a run that settles between the check and the enqueue cannot
                // strand the message in a queue that is never drained. A plain
                // `if (handle.IsRunning)` test here would reintroduce exactly that race.
                //
                // Nothing is swallowed: `false` means the agent was (or became) idle and the turn
                // runs inline as before, and a bounded-queue overflow surfaces as an exception
                // rather than a silent drop. The user's message has already been written to the
                // transcript by the write-ahead save above, so it is retained either way.
                if (await handle.TryFollowUpWhileRunningAsync(
                        BuildUserMessage(message, processedParts ?? originalParts, agentDescriptor),
                        cancellationToken))
                {
                    _logger.LogInformation(
                        "Agent '{AgentId}' session '{SessionId}' was mid-turn; inbound message queued as a " +
                        "follow-up and will be delivered when the current turn completes (#2388).",
                        agentId, sessionId);

                    await _activity.PublishAsync(new GatewayActivity
                    {
                        Type = GatewayActivityType.FollowUpQueued,
                        AgentId = agentId,
                        SessionId = sessionId,
                        Message = "Agent is busy; your message was queued and will be delivered when the current turn completes."
                    }, cancellationToken);

                    continue;
                }

                if (resolvedChannel is { } channel && shouldStream)
                {
                    // Streaming uses the message's opaque ChannelAddress as the stream key.
                    // Adapters that need to disambiguate native sub-addresses (e.g. Telegram
                    // forum topics) already fold them into the address itself.
                    var streamingSource = resolvedSource;

                    // Resolve SignalR observer bindings for cross-channel live update (#332).
                    // When the originating channel is not SignalR (e.g. Telegram), any SignalR
                    // bindings on the conversation receive stream events so connected web
                    // clients update in real-time without a page reload.
                    //
                    // #2073: deduplicate by the primary destination's *resolved delivery identity*,
                    // not by the inbound message.ChannelType. Internal/sub-agent completion turns
                    // arrive as `internal` but the primary streaming channel resolves to the
                    // originating session's channel - which is itself SignalR whenever the parent
                    // session was a portal/web session. In that case the primary path already
                    // streams into the SignalR conversation group, so fanning the same deltas out
                    // to the SignalR observer binding would deliver every delta twice to the same
                    // group ("TheThe independent independent..."). The terminal channel the primary
                    // path delivers to is the session's own channel: the direct path streams via
                    // message.ChannelType, and the internal adapter resolves the target adapter from
                    // session.ChannelType (falling back to signalr). We therefore suppress SignalR
                    // observer fan-out whenever that terminal channel is SignalR, regardless of the
                    // model or the inbound channel type.
                    var primaryTerminalChannel = message.ChannelType.Value == "internal"
                        ? (session.ChannelType?.Value ?? "signalr")
                        : message.ChannelType.Value;
                    var primaryDeliversToSignalR = string.Equals(
                        primaryTerminalChannel, "signalr", StringComparison.Ordinal);

                    IReadOnlyList<(IStreamEventChannelAdapter Adapter, ChannelStreamTarget Target)> signalRObservers = [];
                    if (_conversationRouter is not null && !primaryDeliversToSignalR)
                    {
                        try
                        {
                            var observerBindings = await _conversationRouter.GetOutboundBindingsAsync(
                                typedSessionId,
                                message.BindingId,
                                cancellationToken);

                            signalRObservers = observerBindings
                                .Where(b => b.ChannelType.Value == "signalr")
                                .Select(b =>
                                {
                                    var adapter = ResolveChannelAdapter(b.ChannelType) as IStreamEventChannelAdapter;
                                    var observerTarget = new ChannelStreamTarget(
                                        session.ConversationId,
                                        typedSessionId,
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
                    // #2794: register the run with operator context so /api/stats can explain the count.
                    var streamingLoopRegistration = _activeLoopTracker?.TrackStart(
                        typedAgentId.Value,
                        session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
                        typedSessionId.Value) ?? ActiveLoopRegistration.None;
                    try
                    {
                    // #1598: write-time cap on per-tool-result size persisted to history.
                    // Reads gateway:toolResultPersistence. The cap defaults ON (16 KiB) when the
                    // section is absent (matching ToolResultPersistenceConfig defaults); it is
                    // disabled (0) only when Enabled=false or MaxBytes<=0.
                    var toolResultCfg = _platformConfig?.Value.Gateway?.ToolResultPersistence ?? new ToolResultPersistenceConfig();
                    var maxPersistedToolResultBytes = toolResultCfg is { Enabled: true, MaxBytes: > 0 }
                        ? toolResultCfg.MaxBytes
                        : 0;
                    await StreamingSessionHelper.ProcessAndSaveAsync(
                        handle.StreamAsync(userMessage, cancellationToken),
                        session,
                        _sessions,
                        new StreamingSessionOptions(
                            IncludeErrorsInHistory: true,
                            AssistantMessageKind: responseKind,
                            // #2921: surface a run that terminates on an empty assistant completion.
                            Logger: _logger,
                            // #3535: and when that emptiness is context exhaustion, tell the user.
                            ContextExhaustionNotifier: _contextExhaustionNotifier,
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
                                    AgentId = evt.AgentId ?? typedAgentId,
                                    SessionId = evt.SessionId ?? typedSessionId,
                                    ConversationId = evt.ConversationId ?? session.ConversationId
                                };

                                // Build the typed stream target the channel adapter uses to
                                // route this delta or event. Each adapter consumes the field
                                // that matches its routing semantics — see ChannelStreamTarget.
                                var streamTarget = new ChannelStreamTarget(
                                    session.ConversationId,
                                    typedSessionId,
                                    message.ChannelAddress,
                                    message.BindingId,
                                    message.ChannelRequestId);

                                if (enriched.Type == AgentStreamEventType.UserInputRequired)
                                {
                                    await HandleUserInputRequiredAsync(
                                        message,
                                        typedSessionId,
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
                            },
                        MaxPersistedToolResultBytes: maxPersistedToolResultBytes),
                        _sessionLifecycleEvents,
                        finalSaveFence: runFence,
                        cancellationToken: cancellationToken);
                    }
                    finally
                    {
                        _activeLoopTracker?.TrackEnd(streamingLoopRegistration);
                    }
                    sessionSaved = true;
                }
                else
                {
                    var userMessage = BuildUserMessage(message, processedParts ?? originalParts, agentDescriptor);
                    // #2794: same contextual registration on the non-streaming branch.
                    var blockingLoopRegistration = _activeLoopTracker?.TrackStart(
                        typedAgentId.Value,
                        session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
                        typedSessionId.Value) ?? ActiveLoopRegistration.None;
                    AgentResponse response;
                    try
                    {
                        response = await handle.PromptAsync(userMessage, cancellationToken);

                        // #2522 residual: the blocking branch must stamp the provider's reported
                        // prompt-token count just as the streaming branch does at MessageEnd,
                        // otherwise a non-streamed turn leaves the compactor with no provider
                        // measurement and the unit normalisation degrades to a no-op.
                        ProviderTokenUsageRecorder.Record(session, response.Usage);
                    }
                    finally
                    {
                        _activeLoopTracker?.TrackEnd(blockingLoopRegistration);
                    }
                    if (IsHeartbeatAck(response.Content))
                    {
                        _logger.LogDebug("Heartbeat ack from agent '{AgentId}' session '{SessionId}'", agentId, sessionId);
                    }
                    else if (ResolveChannelAdapter(message.ChannelType) is { } ch)
                    {
                        // Detect thinking-only responses: when the model returns only a
                        // reasoning/thinking block with no user-visible content, StripThinkingTags
                        // produces an empty string. Silently skip delivery (#1198).
                        // Always strip leaked tool-call XML (#1698): some models serialise tool
                        // calls as <invoke>/<tool_use> markup in the text channel. When thinking is
                        // displayed, keep reasoning but drop leaked tool XML; otherwise strip both.
                        var outboundContent = ch.SupportsThinkingDisplay
                            ? AssistantTextSanitizer.StripLeakedToolCalls(response.Content)
                            : AssistantTextSanitizer.Sanitize(response.Content);

                        if (!ch.SupportsThinkingDisplay &&
                            string.IsNullOrWhiteSpace(outboundContent) &&
                            AssistantTextSanitizer.IsThinkingOnlyResponse(response.Content))
                        {
                            _logger.LogInformation(
                                "Agent '{AgentId}' session '{SessionId}' returned a thinking-only response " +
                                "(no user-visible content after stripping reasoning blocks). " +
                                "Closing turn silently.",
                                agentId, sessionId);
                            // Don't send anything to the user — thinking-only responses are
                            // normal model behaviour, not an error (#1198).
                        }
                        else
                        {
                            await ch.SendAsync(new OutboundMessage
                            {
                                ChannelType = message.ChannelType,
                                ChannelAddress = message.ChannelAddress,
                                Content = outboundContent,
                                SessionId = sessionId,
                                // Binding-aware fields from originating binding fix #126:
                                // ensure replies carry the binding's decoration. Native sub-addresses
                                // (e.g. Telegram forum topics) are already encoded in ChannelAddress.
                                BindingId = resolvedSource.BindingId,
                                DisplayPrefix = resolvedSource.DisplayPrefix,
                                ChannelRequestId = message.ChannelRequestId,
                                // #2149: expose the typed kind to the adapter so it can decide whether
                                // to suppress/collapse/specially render a subagent-response.
                                Kind = StampKind(responseKind)
                            }, cancellationToken);
                        }
                    }

                    // #2616 AC3: the blocking branch must leave the SAME tool timeline the
                    // streaming branch leaves, or the portal/history shape a turn renders with
                    // depends on which transport happened to serve it. StreamingSessionHelper
                    // persists sink-produced tool rows as events arrive; this is the blocking twin,
                    // ordered before the assistant row exactly as every other blocking boundary
                    // orders it. Written OUTSIDE the NO_REPLY guard on purpose: a turn that ran
                    // side-effecting tools and then deliberately stayed silent is precisely the one
                    // whose tool record must survive.
                    foreach (var toolEntry in _toolAudit.ProjectBlockingRun(_toolAudit.CaptureBlockingRun(response)))
                        session.AddEntry(toolEntry);

                    // NO_REPLY responses are intentional silences — do not persist them
                    // in the session store. Channel adapters already suppress delivery (#1237).
                    if (!IsNoReply(response.Content))
                        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content, Kind = StampKind(responseKind) });
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
                        // #1518: fenced finalizer write. If the session was deleted, sealed by a
                        // reset, or rebound while this (non-streaming) turn ran, the save no-ops
                        // instead of resurrecting/clobbering the row.
                        var finalizerOutcome = await _sessions.SaveAsync(session, runFence, cancellationToken);
                        if (finalizerOutcome == SessionSaveOutcome.Rebound)
                        {
                            _logger.LogInformation(
                                "Session transcript save skipped as rebound for session '{SessionId}': the session was " +
                                "deleted or reset while the turn was in flight; the completed turn was not persisted (#1518).",
                                sessionId);
                        }
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

                // Outbound fan-out: deliver response to other bindings in the conversation.
                // The assistant content + conversation id are already in hand from the in-memory
                // session we just saved — pass them in so fan-out does not re-read the session from
                // the store purely to recover the last assistant entry (#1394).
                var lastAssistantContent = session.GetHistorySnapshot()
                    .LastOrDefault(e => e.Role == MessageRole.Assistant)?.Content;
                await _deliverer.FanOutAsync(message, typedSessionId, lastAssistantContent, session.ConversationId, cancellationToken);

                // Bump UpdatedAt on the conversation so the portal list ordering and
                // retention thresholds reflect the time of the last message, not the
                // last explicit metadata edit (#890). Best-effort: failures must not
                // surface as turn failures.
                await TouchConversationAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);

                // Auto-generate conversation title after the first user+assistant exchange
                // if the title is still the default value (#739). Best-effort fire-and-forget.
                TryTriggerAutoTitle(session, typedAgentId);

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
            catch (Exception ex) when (
                TurnCancellationClassifier.IsCancellation(ex) && cancellationToken.IsCancellationRequested)
            {
                // #3230: the same caller-initiated cancellation as the branch above, but arriving
                // WRAPPED out of StreamingSessionHelper.ProcessAndSaveAsync. Archiving a
                // conversation used to fall through to the general handler below and be recorded as
                // outcome=error with an [ERR] line, inflating the gateway's own error rate for an
                // ordinary user action. The general catch is unchanged - the guard is on the
                // cancellation condition AND the token state, so a real fault still faults.
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
                            SessionId = sessionId,
                            ChannelRequestId = message.ChannelRequestId
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

    /// <summary>
    /// Resolves the conversation/session routing for a single target agent. This is the
    /// 3-branch resolution lifted out of <see cref="ProcessAsync"/> (#1381 increment 1):
    /// <list type="number">
    /// <item>internal sub-agent wake-ups target an already-known parent session and bypass
    ///       conversation resolution (routing them through it creates synthetic internal
    ///       conversations and can misroute the user-visible response stream);</item>
    /// <item>the conversation dispatcher (preferred runtime path) resolves a full
    ///       <see cref="DispatchResult"/>;</item>
    /// <item>the back-compat conversation router (used while runtime callers migrate to
    ///       dispatcher injection) resolves an inbound and synthesises an equivalent
    ///       <see cref="DispatchResult"/> so the orchestrator outcome uniformly reflects
    ///       every routed agent regardless of branch.</item>
    /// </list>
    /// It performs no session mutation and no I/O beyond the resolver call — it is a pure
    /// transform of the inputs into the resolution outputs the caller then applies, so the
    /// extraction preserves the exact prior behaviour and mutation order.
    /// </summary>
    private async Task<ResolvedConversationSession> ResolveConversationSessionAsync(
        InboundMessage message,
        AgentId typedAgentId,
        string? requestedSessionIdValue,
        InboundMessageRoutingHints hints,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var resolvedSource = new ChannelSource(
            message.ChannelType,
            message.ChannelAddress,
            message.SenderId,
            message.BindingId,
            DisplayPrefix: null);
        ConversationSessionResolution? resolution = null;
        DispatchResult? dispatch = null;
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
                InboundMessageContext.FromInboundMessage(typedAgentId, message),
                cancellationToken);
            sessionId = dispatchResult.Resolution.SessionId.Value;
            resolution = dispatchResult.Resolution;
            resolvedSource = dispatchResult.Source;
            message = message with
            {
                BindingId = message.BindingId ?? resolvedSource.BindingId
            };
            dispatch = dispatchResult;
        }
        else if (_conversationRouter is not null)
        {
            // Back-compat path while runtime callers migrate to dispatcher injection.
            var routingResult = await _conversationRouter.ResolveInboundAsync(
                typedAgentId,
                message.ChannelType,
                message.ChannelAddress,
                conversationId: hints.RequestedConversationId,
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
            var routerContext = InboundMessageContext.FromInboundMessage(typedAgentId, message);
            dispatch = new DispatchResult(routerContext, resolvedSource, resolution);
        }

        return new ResolvedConversationSession(sessionId, resolution, resolvedSource, message, dispatch);
    }

    /// <summary>
    /// Outputs of <see cref="ResolveConversationSessionAsync"/>: the resolved session id,
    /// the (nullable) conversation resolution, the resolved channel source, the
    /// possibly-rewritten inbound message (binding id back-filled), and an optional
    /// <see cref="DispatchResult"/> the caller appends to the dispatch list.
    /// </summary>
    private readonly record struct ResolvedConversationSession(
        string SessionId,
        ConversationSessionResolution? Resolution,
        ChannelSource ResolvedSource,
        InboundMessage Message,
        DispatchResult? Dispatch);

    /// <summary>
    /// #2896: resolves the compaction options for one turn, with ContextWindowTokens narrowed to
    /// the scoped context window for this agent/conversation (conversation override, then agent
    /// descriptor, then resolved model window). When no resolver is registered, or no layer supplies
    /// a window, the configured global options are returned unchanged so behaviour is byte-identical
    /// to the pre-#2896 path.
    /// </summary>
    private async Task<CompactionOptions> ResolveScopedCompactionOptionsAsync(
        AgentId agentId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var options = _compactionOptions.CurrentValue;
        if (_contextWindowResolver is null)
        {
            return options;
        }

        var scoped = await _contextWindowResolver
            .ResolveAsync(agentId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        return ScopedCompactionWindow.Apply(options, scoped);
    }

    /// <summary>
    /// Runs the pre-execution "prepare turn" steps for a single agent dispatch, extracted from
    /// <see cref="ProcessAsync"/> (#1503 increment 2, follow-up to #1381). In strict order:
    /// (1) auto-compaction when <see cref="ISessionCompactor.ShouldCompact"/> is true (best-effort:
    /// a compaction failure is logged and swallowed so the turn still proceeds, #363/#602),
    /// (2) abandoned-turn detection (#790) -- if the previous turn stalled mid-tool, stop the stale
    /// handle so the supervisor rebuilds clean context and append a user-visible notification, and
    /// (3) the write-ahead crash sentinel (#363) persisted before the LLM call. Every step mutates
    /// and persists <paramref name="session"/> exactly as the inlined block did; no value produced
    /// here is consumed by the caller, so the extraction is behaviour-preserving.
    /// </summary>
    private async Task PrepareTurnAsync(
        GatewaySession session,
        InboundMessage message,
        AgentId typedAgentId,
        SessionId typedSessionId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // #2896: narrow the compaction budget to this agent/conversation's own context window
        // before asking ShouldCompact. The compactor's arithmetic is unchanged - only the base
        // window it multiplies TokenThresholdRatio against becomes scope-aware, so an agent with
        // no scoped window behaves exactly as before.
        var turnCompactionOptions = await ResolveScopedCompactionOptionsAsync(
            session.AgentId,
            session.ConversationId,
            cancellationToken).ConfigureAwait(false);

        if (_compactor.ShouldCompact(session.Session, turnCompactionOptions))
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

        // Abandoned turn detection (#790): before starting the new turn, check if
        // the previous turn stalled mid-tool-execution. If so, destroy the existing
        // agent handle (forces fresh context on recreate) and notify the user.
        var abandonedTurnResult = SessionContextProjector.DetectAbandonedTurn(session.History);
        if (abandonedTurnResult.HasAbandonedTurn)
        {
            _logger.LogWarning(
                "Abandoned turn detected for session '{SessionId}': {Count} dangling tool call(s). " +
                "Destroying stale agent handle to prevent context replay.",
                sessionId, abandonedTurnResult.AbandonedEntryCount);

            // Kill the existing handle so the supervisor creates a fresh one with
            // clean context from ProjectForResume (which drops tool entries).
            var stopTask = _supervisor.StopAsync(typedAgentId, typedSessionId, cancellationToken);
            if (stopTask is not null)
                await stopTask;

            // Add a notification entry so the user knows the prior turn was incomplete.
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Notification,
                Content = $"[Previous turn did not complete — {abandonedTurnResult.AbandonedEntryCount} tool call(s) were abandoned. Starting fresh.]"
            });
            await _sessions.SaveAsync(session, cancellationToken);
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
    }

    private async Task SendSessionStatusRejectedAsync(
        InboundMessage message,
        AgentId typedAgentId,
        SessionId typedSessionId,
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
                SessionId = typedSessionId.Value
            }, cancellationToken);
        }

        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.Error,
            AgentId = typedAgentId.Value,
            SessionId = typedSessionId.Value,
            Message = statusMessage
        }, cancellationToken);
    }

    /// <summary>
    /// #3609: reports an undeliverable steer back to the originating channel and as an
    /// <see cref="GatewayActivityType.Error"/> activity, so a discarded control message is
    /// user-visible rather than a silent accept.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="SendSessionStatusRejectedAsync"/> deliberately: the two are the same
    /// concern (an inbound message the gateway will not process) and must not diverge in how the
    /// user learns about it. Best-effort on the channel leg - a channel that refuses the send must
    /// not turn a discarded steer into a faulted turn - but the Error activity and the caller's
    /// Warning are unconditional, so the event is never lost even when no adapter can deliver it.
    /// </remarks>
    private async Task SendSteeringUndeliverableAsync(
        InboundMessage message,
        AgentId typedAgentId,
        SessionId typedSessionId,
        CancellationToken cancellationToken)
    {
        const string StatusMessage =
            "Your steering message could not be delivered: this session has no running agent turn to steer. "
            + "Send it as a normal message to start a new turn.";

        if (ResolveChannelAdapter(message.ChannelType) is { } channel)
        {
            try
            {
                await channel.SendAsync(new OutboundMessage
                {
                    ChannelType = message.ChannelType,
                    ChannelAddress = message.ChannelAddress,
                    Content = StatusMessage,
                    SessionId = typedSessionId.Value
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to deliver the undeliverable-steer notice for session '{SessionId}' over channel "
                    + "'{ChannelType}'; the Error activity below still records it (#3609).",
                    typedSessionId.Value, message.ChannelType);
            }
        }

        await _activity.PublishAsync(new GatewayActivity
        {
            Type = GatewayActivityType.Error,
            AgentId = typedAgentId.Value,
            SessionId = typedSessionId.Value,
            Message = StatusMessage
        }, cancellationToken);
    }

    /// <summary>
    /// Injects a steering message into a running turn and publishes the corresponding
    /// conversation-scoped activity event.
    /// </summary>
    /// <param name="conversationId">
    /// #3065: the conversation this steer belongs to, taken from the already-resolved
    /// <c>ConversationSessionResolution</c> stamped onto the session by the caller. Both
    /// <c>SteeringQueued</c> and <c>SteeringInjected</c> are conversation-scoped events -
    /// <c>SteeringSignalRBridge</c> routes them to <c>conversation:{id}</c> - so emitting them
    /// without an id forced the bridge onto its <c>session-id-as-conversation-key</c> synonym and
    /// the portal onto an ambient fallback. Passing the resolved id through recovers information
    /// that already existed rather than re-deriving it.
    /// </param>
    private async Task<bool> HandleSteeringAsync(
        InboundMessage message,
        AgentId typedAgentId,
        SessionId typedSessionId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(typedAgentId, typedSessionId);
        IAgentHandle? handle = null;

        if (instance is not null)
        {
            try
            {
                handle = await _supervisor.GetOrCreateAsync(typedAgentId, typedSessionId, cancellationToken);
            }
            catch { /* instance exists but handle creation failed */ }
        }

        // If no handle exists at all, we can't inject. Return false so the
        // caller discards the control message (it can't be delivered).
        //
        // #3609 (clause 7 of #3600): this branch is reachable on ANY long-running conversation,
        // because SessionCompactionCoordinator.CompactAsync step 4 evicts the cached handle via
        // IAgentSupervisor.StopAsync on every applied compaction. That eviction is a CACHE drop,
        // not a teardown: DefaultAgentSupervisor.StopAsync touches no ISessionStore, so the store
        // legitimately still reports sessions.status = Active with a live
        // conversations.active_session_id, and the ordinary data path
        // (GatewayHost -> IAgentSupervisor.GetOrCreateAsync -> CreateEntryAsync, which reloads
        // existingSession.History) rehydrates from exactly that state. The control path did not.
        //
        // Rehydrating HERE would be worse than discarding, and deliberately is not done:
        // DefaultInboundDeliveryResolver documents that a steer injected into a freshly minted,
        // idle handle lands in a PendingMessageQueue that nothing will ever drain - a silent loss
        // one layer further down. So this keeps the discard (AC2's "fail loudly" branch) and makes
        // it observable instead of silent: a Warning naming the session, and a user-visible message
        // through the originating channel. The previous LogInformation + SteeringQueued activity
        // was invisible to the user and below most production log thresholds, which is precisely
        // the "permanently write-only conversation" symptom #3600 was filed for.
        if (handle is null)
        {
            _logger.LogWarning(
                "Steering discarded for agent '{AgentId}' session '{SessionId}': no agent handle exists. "
                + "The handle is an in-memory cache and is evicted after compaction; the control path "
                + "cannot rehydrate it because a steer injected into an idle handle would never be "
                + "drained. Send the message as a normal (non-steer) message to rehydrate and run it (#3609).",
                typedAgentId.Value, typedSessionId.Value);

            await _activity.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.SteeringQueued,
                AgentId = typedAgentId.Value,
                SessionId = typedSessionId.Value,
                ConversationId = conversationId.Value
            }, cancellationToken);

            await SendSteeringUndeliverableAsync(message, typedAgentId, typedSessionId, cancellationToken)
                .ConfigureAwait(false);

            return false;
        }

        // Record steering message in session history
        var session = await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, cancellationToken);
        IReadOnlyList<MessageContentPart>? originalParts = message.ContentParts;
        IReadOnlyList<MessageContentPart>? processedParts = null;

        if (originalParts is { Count: > 0 } && _mediaPipeline is not null)
        {
            var mediaContext = new MediaProcessingContext
            {
                SessionId = typedSessionId.Value,
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
            AgentId = typedAgentId.Value,
            SessionId = typedSessionId.Value,
            ConversationId = conversationId.Value
        }, cancellationToken);

        // #1903: the steering/portal follow-up path adds a User entry above but historically
        // never invoked titling, so portal conversations whose first turn was still on the
        // default title could never title on a Steer turn. Fire best-effort titling here the
        // same way the main ProcessAsync tail does, reading the freshest session.History.
        TryTriggerAutoTitle(session, typedAgentId);

        _logger.LogInformation("Steering message injected for agent {AgentId} session {SessionId}", typedAgentId.Value, typedSessionId.Value);
        return true;
    }

    private async Task HandleCompactionAsync(
        InboundMessage message,
        GatewaySession session,
        SessionId typedSessionId,
        CancellationToken cancellationToken)
    {
        var outcome = await _compactionCoordinator.CompactAsync(session.AgentId, session, cancellationToken, force: true).ConfigureAwait(false);
        // Always notify on this path — channel-driven /compact callers expect feedback
        // even on failure so the user knows the command landed. Use the canonical
        // text (including the FailureReason when applicable).
        await _compactionCoordinator.TrySendChannelNotificationAsync(
            outcome,
            message.ChannelType,
            message.ChannelAddress,
            typedSessionId.Value,
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

    private static bool IsNoReply(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        return response.Trim().Equals("NO_REPLY", StringComparison.Ordinal);
    }

    // #2149: normalise the default kind to null so ordinary/legacy entries and outbound
    // messages stay unstamped (the persistence layer stores MessageKind.Message as NULL). A
    // non-default kind (subagent-completion / subagent-response) is carried through verbatim.
    private static MessageKind? StampKind(MessageKind kind)
        => kind.Equals(MessageKind.Message) ? null : kind;

    // #2149: resolve the inbound message's typed kind. Prefer the typed InboundMessage.Kind;
    // for back-compat, fall back to the legacy Metadata["messageType"] token so a producer that
    // only stamped the string metadata (e.g. before it was updated to set Kind) still yields the
    // correct typed kind rather than silently degrading to the ordinary message default.
    private static MessageKind ResolveInboundKind(InboundMessage message)
    {
        if (message.Kind is { } kind)
            return kind;

        if (message.Metadata.TryGetValue("messageType", out var raw) && raw is string token &&
            !string.IsNullOrWhiteSpace(token))
            return MessageKind.FromString(token);

        return MessageKind.Message;
    }

    private async Task HandleUserInputRequiredAsync(
        InboundMessage message,
        SessionId typedSessionId,
        ConversationId conversationId,
        ChannelSource source,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        var request = streamEvent.UserInputRequest;

        if (ResolveChannelAdapter(message.ChannelType) is { } sourceAdapter)
            await SendAskUserToBindingAsync(sourceAdapter, source, typedSessionId, conversationId, streamEvent, request, cancellationToken);

        if (_conversationRouter is null)
            return;

        var outboundBindings = await _conversationRouter.GetOutboundBindingsAsync(
            typedSessionId,
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
            await SendAskUserToBindingAsync(adapter, bindingSource, typedSessionId, conversationId, streamEvent, request, cancellationToken);
        }
    }

    private static async Task SendAskUserToBindingAsync(
        IChannelAdapter adapter,
        ChannelSource source,
        SessionId typedSessionId,
        ConversationId conversationId,
        AgentStreamEvent streamEvent,
        AskUserRequest? request,
        CancellationToken cancellationToken)
    {
        if (adapter is IStreamEventChannelAdapter streamAdapter)
        {
            var target = new ChannelStreamTarget(
                conversationId,
                typedSessionId,
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
            SessionId = typedSessionId.Value,
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

    /// <summary>
    /// #3065: guarantees the session carries a real <see cref="ConversationId"/> before any
    /// conversation-scoped event is emitted for this turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every conversation-scoped emit site in the gateway - the streaming <c>OnEventAsync</c>
    /// stamp, <see cref="HandleUserInputRequiredAsync"/>, and the steering activity events -
    /// reads its id from <c>session.ConversationId</c>, which is set from the resolved
    /// <c>ConversationSessionResolution</c>. The resolution is authoritative when it exists; this
    /// method only covers the one case it cannot: a channel with no conversation yet, where
    /// neither the dispatcher nor the back-compat router ran (or ran without a store).
    /// </para>
    /// <para>
    /// The mint uses <see cref="ConversationFactory.CreateForChannel"/> with a server-generated
    /// id - byte-for-byte the same seam the REST <c>POST /conversations</c> endpoint uses - so the
    /// id is still minted server-side and the client never supplies one. When no conversation
    /// store is wired (legacy unit-test compositions) the session is still stamped with a real id
    /// so the emitted events are attributable; only the persisted row is skipped.
    /// </para>
    /// <para>
    /// Deliberately NOT a fallback to any "active" or "last used" conversation: guessing is the
    /// exact misattribution class this issue removes. A fresh conversation is an honest answer;
    /// somebody else's conversation is not.
    /// </para>
    /// </remarks>
    private async Task EnsureConversationForEmissionAsync(
        GatewaySession session,
        AgentId typedAgentId,
        InboundMessage message,
        CancellationToken cancellationToken)
    {
        if (session.ConversationId.IsInitialized())
            return;

        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            typedAgentId,
            title: null,
            initiator: message.Sender.IsValid ? message.Sender : null);

        if (_conversationStore is not null)
        {
            try
            {
                var created = await _conversationStore
                    .CreateAsync(conversation, cancellationToken)
                    .ConfigureAwait(false);
                session.ConversationId = created.ConversationId;
                _logger.LogInformation(
                    "Minted conversation {ConversationId} for session '{SessionId}' on channel {ChannelType}: "
                    + "the channel had no conversation, and a conversation-scoped event must name one (#3065).",
                    created.ConversationId.Value,
                    session.SessionId.Value,
                    message.ChannelType.Value);
                return;
            }
            catch (Exception ex)
            {
                // Persisting the row is best-effort; stamping the session is not. An unattributed
                // event is the defect being fixed, so fall through and stamp the minted id anyway
                // rather than letting a store failure reopen the ambient-fallback hole.
                _logger.LogWarning(
                    ex,
                    "Failed to persist minted conversation {ConversationId} for session '{SessionId}'; "
                    + "stamping the session with it regardless so emitted events stay attributable (#3065).",
                    conversation.ConversationId.Value,
                    session.SessionId.Value);
            }
        }

        session.ConversationId = conversation.ConversationId;
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

    /// <summary>
    /// Best-effort bump of <see cref="Conversation.UpdatedAt"/> after a completed turn.
    /// Failures are logged at debug level and swallowed so turn delivery is never impacted (#890).
    /// No-ops when no conversation store is wired (legacy test compositions).
    /// </summary>
    private async Task TouchConversationAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        if (!conversationId.IsInitialized() || _conversationStore is null)
            return;
        try
        {
            await _conversationStore.TouchAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to touch conversation UpdatedAt for {ConversationId}; portal list ordering may be stale.",
                conversationId);
        }
    }

    /// <summary>
    /// Fires auto-title generation when the session has at least one user+assistant exchange and
    /// the conversation title is still at its default value (#1695: no longer one-shot, so a
    /// conversation that was busy on its first turn can still get titled on a later turn). No-op
    /// when auto-title is not wired or the conversation is not initialised.
    /// </summary>
    /// <summary>
    /// Fires PROVISIONAL title generation (#2126) when the first live user message has just been
    /// persisted for a still-default-titled, interactive human-agent conversation. Titles from the
    /// user message alone, before the assistant turn completes, so the conversation stops showing
    /// "New conversation" almost immediately. No-op when auto-title is disabled/unwired, the
    /// conversation is not human-agent/interactive, the sender is not a human, the conversation is
    /// cron-owned or already non-default, or an assistant entry already exists. Best-effort and
    /// fire-and-forget: never delays or fails the foreground turn.
    /// </summary>
    private void TryTriggerProvisionalTitle(GatewaySession session, AgentId typedAgentId, InboundMessage message)
    {
        if (_autoTitleService is null)
            return;
        if (!session.ConversationId.IsInitialized())
            return;

        var titling = _platformConfig?.Value?.Gateway?.Auxiliary?.Titling;
        if (titling is { Enabled: false })
            return;

        // Only human-initiated, interactive user-agent conversations are provisionally titled.
        // Excludes cron-owned turns (Session.IsInteractive is false for the cron channel),
        // agent-agent / sub-agent turns, and any agent-authored channel post (webhook/service-
        // address relays, cross-channel agent posts) whose derived role is Assistant.
        if (!session.IsInteractive)
            return;
        if (message.Sender.Kind != CitizenKind.User)
            return;
        if (message.DeriveChannelPostRole() != MessageRole.User)
            return;

        var userText = ConversationAutoTitleService.ShouldTriggerProvisionalTitle(session.History);
        if (userText is null)
            return;

        _autoTitleService.TriggerProvisionalBestEffort(
            session.ConversationId,
            typedAgentId,
            userText,
            titling?.Model,
            titling?.TimeoutSeconds ?? 30,
            // #3417: background LLM calls carry the originating session identity, exactly as every
            // interactive provider path does, so the Copilot prompt-cache key is emitted.
            session.SessionId);
    }

    private void TryTriggerAutoTitle(GatewaySession session, AgentId typedAgentId)
    {
        // Diagnostic: previously these guards no-op'd silently, so a conversation stuck on the
        // default title gave no signal about why auto-titling never ran (#739 live no-fire: 82
        // portal conversations sat on the default title with zero log evidence). These states are
        // wiring/config conditions, not per-turn hot-path noise, so log at Information to make the
        // no-fire observable on a standard INFO gateway without needing Debug enabled.
        if (_autoTitleService is null)
        {
            _logger.LogInformation(
                "Auto-title not triggered: service not wired (LLM client or conversation store missing at gateway construction).");
            return;
        }
        if (!session.ConversationId.IsInitialized())
        {
            _logger.LogInformation(
                "Auto-title not triggered: session '{SessionId}' has no resolved conversation.",
                session.SessionId);
            return;
        }

        var titling = _platformConfig?.Value?.Gateway?.Auxiliary?.Titling;
        if (titling is { Enabled: false })
        {
            _logger.LogInformation(
                "Auto-title not triggered: disabled via gateway.auxiliary.titling.enabled.");
            return;
        }

        var (userText, assistantText) = ConversationAutoTitleService.ShouldTriggerAutoTitle(session.History, _logger);
        // #1903: userText may legitimately be null for an agent-initiated conversation (the
        // assistant-only titling case). Only a null assistantText means there is genuinely
        // nothing to title yet, so gate solely on the assistant seed being present.
        if (assistantText is null)
        {
            // ShouldTriggerAutoTitle already logs its own guard-skip (nothing to title yet).
            // No-op here; the title stays default until a later turn.
            return;
        }

        _autoTitleService.TriggerBestEffort(
            session.ConversationId,
            typedAgentId,
            userText,
            assistantText,
            titling?.Model,
            titling?.TimeoutSeconds ?? 30,
            // #3417: see TryTriggerProvisionalTitle - same session-identity threading.
            session.SessionId);
    }

    private SessionType ResolveSessionType(GatewaySession session, InboundMessage message, bool isNewSession)
    {
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
    /// Channel types that are not deliverable (no adapter exists by design).
    /// Fan-out skips these silently at DEBUG level instead of logging a WARNING.
    /// Delegates to <see cref="OutboundResponseDeliverer"/>, the owner of the outbound
    /// fan-out cluster (#1811); kept here as the stable classification surface for callers/tests.
    /// </summary>
    internal static IReadOnlySet<string> NonDeliverableChannels => OutboundResponseDeliverer.NonDeliverableChannels;

    internal static bool IsNonDeliverableChannel(ChannelKey channelType) =>
        OutboundResponseDeliverer.IsNonDeliverableChannel(channelType);

    private IChannelAdapter? ResolveChannelAdapter(ChannelKey channelType, string? adapterId = null)
    {
        var adapter = _channelManager.Get(channelType, adapterId);
        if (adapter is not null)
            return adapter;

        // #3541: #3167 taught only the fan-out path (OutboundResponseDeliverer.DeliverToBindingAsync)
        // that some channel types have no adapter BY DESIGN. This path — 8 call sites — kept warning,
        // so the by-design condition still produced ~54 WARNINGs/day and drowned genuine adapter
        // outages, which use the same message. The classification is reached through the single
        // existing seam (IsNonDeliverableChannel -> OutboundResponseDeliverer) rather than copied.
        if (IsNonDeliverableChannel(channelType))
        {
            _logger.LogDebug(
                "Skipping non-deliverable channel type '{ChannelType}' (adapterId: '{AdapterId}'); no adapter is registered by design.",
                channelType,
                adapterId ?? "<any>");
            return null;
        }

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
        // #2484: compose through the ONE shared seam (AgentUserMessageComposer) instead of a
        // GatewayHost-private copy. #2294 fixed non-image attachment loss inline here, but the
        // fix was reachable only from this call site, so steer/redirect/follow-up kept dropping
        // attachments. Datetime injection runs first so the injected prefix stays at the head of
        // the message and the attachment blocks are appended after it, exactly as before.
        var content = InjectDateTimeIfEnabled(message.Content, descriptor);
        return AgentUserMessageComposer.Compose(content, contentParts);
    }

    /// <summary>
    /// Delegates to the shared <see cref="AgentUserMessageComposer.AppendNonImageAttachments"/>
    /// seam. Retained as the gateway-facing name used by existing regression coverage (#2294).
    /// </summary>
    /// <param name="content">The raw user message text.</param>
    /// <param name="contentParts">Optional transport content parts (attachments).</param>
    /// <returns>The message text with non-image attachments folded in.</returns>
    internal static string AppendNonImageAttachments(
        string content,
        IReadOnlyList<MessageContentPart>? contentParts)
        => AgentUserMessageComposer.AppendNonImageAttachments(content, contentParts);

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

}

