using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
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
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using MessageRole = BotNexus.Domain.Primitives.MessageRole;
using ParticipantType = BotNexus.Domain.Primitives.ParticipantType;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
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
public sealed class GatewayHost : BackgroundService, IChannelDispatcher, IAsyncDisposable
{
    private const int DefaultSessionQueueCapacity = 64;
    private const string BusyMessage = "Session is busy processing messages. Please retry shortly.";
    private const string ControlSteer = "steer";
    private const string ControlCompact = "compact";
    private const string SystemPromptInitializedMetadataKey = "systemPromptInitialized";

    private readonly IAgentSupervisor _supervisor;
    private readonly IMessageRouter _router;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly IChannelManager _channelManager;
    private readonly ISessionCompactor _compactor;
    private readonly IOptionsMonitor<CompactionOptions> _compactionOptions;
    private readonly ILogger<GatewayHost> _logger;
    private readonly IMediaPipeline? _mediaPipeline;
    private readonly IConversationDispatcher? _conversationDispatcher;
    private readonly IConversationRouter? _conversationRouter;
    private readonly SessionLifecycleEvents? _sessionLifecycleEvents;
    private readonly ConcurrentDictionary<string, SessionQueueState> _sessionQueues = new(StringComparer.OrdinalIgnoreCase);

    public GatewayHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager,
        ISessionCompactor compactor,
        IOptionsMonitor<CompactionOptions> compactionOptions,
        ILogger<GatewayHost> logger,
        int sessionQueueCapacity = DefaultSessionQueueCapacity,
        SessionLifecycleEvents? sessionLifecycleEvents = null,
        IMediaPipeline? mediaPipeline = null,
        IConversationDispatcher? conversationDispatcher = null,
        IConversationRouter? conversationRouter = null)
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
        SessionQueueCapacity = Math.Max(sessionQueueCapacity, 1);
    }

    private int SessionQueueCapacity { get; }

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

        await CompleteSessionQueuesAsync();
    }

    /// <inheritdoc />
    public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var queueKey = GetQueueKey(message);
        var queueState = _sessionQueues.GetOrAdd(queueKey, CreateSessionQueueState);
        var queueItem = new QueuedInboundMessage(message, cancellationToken);

        if (!queueState.Queue.Writer.TryWrite(queueItem))
        {
            await SendBusyAsync(message, cancellationToken);
            return;
        }

        try
        {
            await queueItem.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await queueItem.Completion.Task;
            }
            catch
            {
                // Preserve previous dispatcher behavior for canceled callers.
            }
        }
    }

    private SessionQueueState CreateSessionQueueState(string queueKey)
    {
        var queue = Channel.CreateBounded<QueuedInboundMessage>(new BoundedChannelOptions(SessionQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var workerTask = ProcessSessionQueueAsync(queueKey, queue.Reader);
        return new SessionQueueState(queue, workerTask);
    }

    private async Task ProcessSessionQueueAsync(string queueKey, ChannelReader<QueuedInboundMessage> queueReader)
    {
        try
        {
            await foreach (var item in queueReader.ReadAllAsync())
            {
                try
                {
                    // Use a detached token for agent processing so client disconnect
                    // doesn't kill in-progress agent work. The agent continues in the
                    // background even if the WebSocket closes.
                    await ProcessInboundMessageAsync(item.Message, CancellationToken.None);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing queued inbound message for queue '{QueueKey}'", queueKey);
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    await CleanupQueueIfClosedSessionAsync(queueKey, item.Message, CancellationToken.None);
                }
            }
        }
        finally
        {
            _sessionQueues.TryRemove(queueKey, out _);
        }
    }

    private async Task ProcessInboundMessageAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("gateway.dispatch", ActivityKind.Server);
        activity?.SetTag("botnexus.channel.type", message.ChannelType);
        activity?.SetTag("botnexus.session.id", message.SessionId);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());
        GatewayTelemetry.MessagesProcessed.Add(1,
            new KeyValuePair<string, object?>("botnexus.channel.type", message.ChannelType),
            new KeyValuePair<string, object?>("botnexus.session.id", message.SessionId));

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
            var sessionId = message.SessionId ?? $"{message.ChannelType}:{message.ChannelAddress}:{agentId}";
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
                message.ThreadId,
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
                var internalTargetSessionId = !string.IsNullOrWhiteSpace(message.SessionId)
                    ? message.SessionId
                    : message.ChannelAddress.Value;

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
                    BindingId = message.BindingId ?? resolvedSource.BindingId,
                    ThreadId = message.ThreadId ?? resolvedSource.ThreadId
                };
            }
            else if (_conversationRouter is not null)
            {
                // Back-compat path while runtime callers migrate to dispatcher injection.
                var routingResult = await _conversationRouter.ResolveInboundAsync(
                    AgentId.From(agentId),
                    message.ChannelType,
                    message.ChannelAddress,
                    threadId: message.ThreadId,
                    conversationId: message.ConversationId,
                    cancellationToken);
                sessionId = routingResult.SessionId.Value;
                var originatingBinding = routingResult.OriginatingBinding;
                resolvedSource = originatingBinding is null
                    ? resolvedSource
                    : resolvedSource with
                    {
                        BindingId = originatingBinding.BindingId,
                        ThreadId = originatingBinding.ThreadId,
                        DisplayPrefix = originatingBinding.DisplayPrefix
                    };
                resolution = new ConversationSessionResolution(
                    routingResult.Conversation.ConversationId,
                    routingResult.SessionId,
                    IsNewConversation: false,
                    IsNewSession: routingResult.IsNewSession,
                    OriginatingBindingId: resolvedSource.BindingId,
                    ThreadId: resolvedSource.ThreadId,
                    DisplayPrefix: resolvedSource.DisplayPrefix);
                message = message with
                {
                    BindingId = message.BindingId ?? resolvedSource.BindingId,
                    ThreadId = message.ThreadId ?? resolvedSource.ThreadId
                };
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
            // Stamp ConversationId from dispatch resolution when not already set on the session object.
            if (resolution is not null && session.Session.ConversationId is null)
                session.Session.ConversationId = resolution.ConversationId;
            session.CallerId ??= message.SenderId;
            session.SessionType = ResolveSessionType(session, message);
            EnsureCallerParticipant(session, message.SenderId);
            if (ShouldInitializeSystemPrompt(session))
            {
                session.Metadata[SystemPromptInitializedMetadataKey] = true;
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
                    // Agent not running — fall through to normal message processing.
                }
                else if (string.Equals(controlCommand, ControlCompact, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCompactionAsync(message, session, sessionId, cancellationToken);
                    continue;
                }
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

            if (_compactor.ShouldCompact(session.Session, _compactionOptions.CurrentValue))
            {
                _logger.LogInformation("Auto-compacting session {SessionId}", sessionId);
                try
                {
                    var result = await _compactor.CompactAsync(session.Session, _compactionOptions.CurrentValue, cancellationToken);
                    if (result.Succeeded && result.CompactedHistory is not null)
                    {
                        session.ReplaceHistory(result.CompactedHistory);
                        session.Session.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                    await _sessions.SaveAsync(session, cancellationToken);
                    _logger.LogInformation(
                        "Session {SessionId} compacted: {Summarized} entries summarized, {Preserved} preserved",
                        sessionId, result.EntriesSummarized, result.EntriesPreserved);
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
                var resolvedChannel = ResolveChannelAdapter(message.ChannelType);
                _logger.LogInformation("Channel resolution: type='{ChannelType}' found={Found} streaming={Streaming} streamEvents={StreamEvents}",
                    message.ChannelType,
                    resolvedChannel is not null,
                    resolvedChannel?.SupportsStreaming,
                    resolvedChannel is IStreamEventChannelAdapter);

                if (resolvedChannel is { SupportsStreaming: true } channel)
                {
                    // Capture the resolved source for the lambda closure so the
                    // streaming conversationId includes the ThreadId (fixes #125).
                    var streamingSource = resolvedSource;
                    var userMessage = BuildUserMessage(message, processedParts ?? originalParts);
                    await StreamingSessionHelper.ProcessAndSaveAsync(
                        handle.StreamAsync(userMessage, cancellationToken),
                        session,
                        _sessions,
                        new StreamingSessionOptions(
                            IncludeErrorsInHistory: true,
                            OnEventAsync: (evt, ct) =>
                            {
                                // Enrich with agentId so the client can route events
                                // even before session registration completes.
                                var enriched = evt.AgentId is null || evt.SessionId is null
                                    ? evt with 
                                    { 
                                        AgentId = evt.AgentId ?? Domain.Primitives.AgentId.From(agentId),
                                        SessionId = evt.SessionId ?? Domain.Primitives.SessionId.From(sessionId)
                                    }
                                    : evt;

                                // Build the conversationId that the channel adapter uses to
                                // route the stream to the correct chat thread/topic.
                                // For channels like Telegram that support forum topics, the
                                // bare chatId is not enough — we encode threadId into the key
                                // so the adapter can split them apart (fixes #125).
                                var streamConversationId = streamingSource.ThreadId is not null
                                    ? $"{message.ChannelAddress}:{streamingSource.ThreadId}"
                                    : message.ChannelAddress.Value;

                                if (channel is IStreamEventChannelAdapter streamEventChannel)
                                    return new ValueTask(streamEventChannel.SendStreamEventAsync(streamConversationId, enriched, ct));

                                if (evt.Type == AgentStreamEventType.ContentDelta && evt.ContentDelta is not null)
                                    return new ValueTask(channel.SendStreamDeltaAsync(streamConversationId, evt.ContentDelta, ct));

                                return ValueTask.CompletedTask;
                            }),
                        _sessionLifecycleEvents,
                        cancellationToken);
                    sessionSaved = true;
                }
                else
                {
                    var userMessage = BuildUserMessage(message, processedParts ?? originalParts);
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
                            // ensure replies go to the correct thread/topic and carry decoration.
                            ThreadId = resolvedSource.ThreadId ?? message.ThreadId,
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
                    using var saveActivity = GatewayDiagnostics.Source.StartActivity("session.save", ActivityKind.Internal);
                    saveActivity?.SetTag("botnexus.session.id", session.SessionId);
                    saveActivity?.SetTag("botnexus.agent.id", session.AgentId);
                    await _sessions.SaveAsync(session, cancellationToken);
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
                "Steering received but agent is not running (instance={HasInstance}, running={IsRunning}). Falling through to normal processing for session {SessionId}",
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
        var result = await _compactor.CompactAsync(session.Session, _compactionOptions.CurrentValue, cancellationToken);
        if (result.Succeeded && result.CompactedHistory is not null)
        {
            session.ReplaceHistory(result.CompactedHistory);
            session.Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _sessions.SaveAsync(session, cancellationToken);

        var feedback = result.Succeeded
            ? $"Session compacted: {result.EntriesSummarized} entries summarized, {result.EntriesPreserved} preserved."
            : "Compaction aborted: the summarization model returned an empty response. Session history was not modified.";
        if (ResolveChannelAdapter(message.ChannelType) is { } channel)
        {
            await channel.SendAsync(new OutboundMessage
            {
                ChannelType = message.ChannelType,
                ChannelAddress = message.ChannelAddress,
                Content = feedback,
                SessionId = sessionId
            }, cancellationToken);
        }
    }

    private static bool TryGetControlCommand(InboundMessage message, out string? command)
    {
        command = null;
        if (!message.Metadata.TryGetValue("control", out var controlValue))
            return false;

        command = controlValue?.ToString();
        return !string.IsNullOrWhiteSpace(command);
    }

    private static bool ShouldInitializeSystemPrompt(GatewaySession session)
    {
        if (!session.Metadata.TryGetValue(SystemPromptInitializedMetadataKey, out var value) || value is null)
            return true;

        return value switch
        {
            bool boolValue => !boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => !parsed,
            _ => true
        };
    }

    private static bool IsHeartbeatAck(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var trimmed = response.Trim();
        return trimmed.Equals("HEARTBEAT_OK", StringComparison.Ordinal)
               || trimmed.StartsWith("HEARTBEAT_OK", StringComparison.Ordinal);
    }

    private static string GetQueueKey(InboundMessage message)
        => !string.IsNullOrWhiteSpace(message.SessionId)
            ? message.SessionId
            : $"{message.ChannelType}:{message.ChannelAddress}";

    private async Task SendBusyAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (ResolveChannelAdapter(message.ChannelType) is not { } channel)
            return;

        await channel.SendAsync(new OutboundMessage
        {
            ChannelType = message.ChannelType,
            ChannelAddress = message.ChannelAddress,
            Content = BusyMessage,
            SessionId = message.SessionId
        }, cancellationToken);
    }

    private async Task CleanupQueueIfClosedSessionAsync(string queueKey, InboundMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId))
            return;

        using var sessionActivity = GatewayDiagnostics.Source.StartActivity("session.get", ActivityKind.Internal);
        sessionActivity?.SetTag("botnexus.session.id", message.SessionId);

        var session = await _sessions.GetAsync(SessionId.From(message.SessionId), cancellationToken);
        if (session?.Status is not SessionStatus.Sealed)
            return;

        if (_sessionQueues.TryRemove(queueKey, out var state))
            state.Queue.Writer.TryComplete();
    }

    private async Task CompleteSessionQueuesAsync()
    {
        foreach (var state in _sessionQueues.Values)
            state.Queue.Writer.TryComplete();

        var workers = _sessionQueues.Values.Select(state => state.WorkerTask).ToArray();
        if (workers.Length == 0)
            return;

        try
        {
            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "One or more session queue workers completed with errors during shutdown.");
        }
    }

    /// <summary>
    /// Drains any session queue workers that were started by DispatchAsync but never
    /// cleaned up via the BackgroundService shutdown path (e.g., in unit tests).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await CompleteSessionQueuesAsync();
        base.Dispose();
    }

    private static void EnsureCallerParticipant(GatewaySession session, string? callerId)
    {
        if (string.IsNullOrWhiteSpace(callerId))
            return;

        if (session.Participants.Any(p => p.Type == ParticipantType.User && string.Equals(p.Id, callerId, StringComparison.Ordinal)))
            return;

        session.Participants.Add(new SessionParticipant
        {
            Type = ParticipantType.User,
            Id = callerId
        });
    }

    private static SessionType ResolveSessionType(GatewaySession session, InboundMessage message)
    {
        if (session.SessionId.IsSubAgent)
            return SessionType.AgentSubAgent;

        if (session.SessionId.IsSoul)
            return SessionType.Soul;

        if (message.ChannelType.Equals(ChannelKey.From("cron")))
            return SessionType.Cron;

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
                    var adapter = ResolveChannelAdapter(binding.ChannelType);
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
                        // Binding-aware fields: let the adapter deliver into the right thread/topic
                        // and render prefix decoration when configured.
                        ThreadId = binding.ThreadId,
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

                    if (session?.Session.ConversationId is { } convId)
                        await _conversationRouter.MuteBindingAsync(convId, ex.BindingId, cancellationToken);
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

    private IChannelAdapter? ResolveChannelAdapter(ChannelKey channelType)
    {
        var adapter = _channelManager.Get(channelType);
        if (adapter is not null)
            return adapter;

        _logger.LogWarning("No channel adapter found for type '{ChannelType}'. Available: {Available}",
            channelType,
            string.Join(", ", _channelManager.Adapters.Select(a => a.ChannelType)));
        return null;
    }

    /// <summary>
    /// Builds a <see cref="AgentUserMessage"/> from an inbound message, attaching any image
    /// content parts as <see cref="AgentImageContent"/> so vision-capable models receive them.
    /// Falls back to a plain text <see cref="AgentUserMessage"/> when no image parts are present.
    /// </summary>
    private static AgentUserMessage BuildUserMessage(
        InboundMessage message,
        IReadOnlyList<MessageContentPart>? contentParts)
    {
        var images = BuildImageContent(contentParts);
        return images is { Count: > 0 }
            ? new AgentUserMessage(message.Content, images)
            : new AgentUserMessage(message.Content);
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

    private sealed class SessionQueueState(Channel<QueuedInboundMessage> queue, Task workerTask)
    {
        public Channel<QueuedInboundMessage> Queue { get; } = queue;
        public Task WorkerTask { get; } = workerTask;
    }

    private sealed class QueuedInboundMessage(InboundMessage message, CancellationToken cancellationToken)
    {
        public InboundMessage Message { get; } = message;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
