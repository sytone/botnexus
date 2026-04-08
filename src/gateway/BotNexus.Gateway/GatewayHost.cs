using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway;

/// <summary>
/// The central Gateway orchestration service. Manages the lifecycle of channel adapters,
/// listens for inbound messages, routes them to agents, and streams responses back.
/// </summary>
public sealed class GatewayHost : BackgroundService, IChannelDispatcher
{
    private const int DefaultSessionQueueCapacity = 64;
    private const string BusyMessage = "Session is busy processing messages. Please retry shortly.";
    private const string ControlSteer = "steer";
    private const string SystemPromptInitializedMetadataKey = "systemPromptInitialized";

    private readonly IAgentSupervisor _supervisor;
    private readonly IMessageRouter _router;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<GatewayHost> _logger;
    private readonly SessionLifecycleEvents? _sessionLifecycleEvents;
    private readonly ConcurrentDictionary<string, SessionQueueState> _sessionQueues = new(StringComparer.OrdinalIgnoreCase);

    public GatewayHost(
        IAgentSupervisor supervisor,
        IMessageRouter router,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IChannelManager channelManager,
        ILogger<GatewayHost> logger,
        int sessionQueueCapacity = DefaultSessionQueueCapacity,
        SessionLifecycleEvents? sessionLifecycleEvents = null)
    {
        _supervisor = supervisor;
        _router = router;
        _sessions = sessions;
        _activity = activity;
        _channelManager = channelManager;
        _logger = logger;
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
            var sessionId = message.SessionId ?? $"{message.ChannelType}:{message.ConversationId}:{agentId}";
            using var agentActivity = GatewayDiagnostics.Source.StartActivity("gateway.agent_process", ActivityKind.Internal);
            agentActivity?.SetTag("botnexus.agent.id", agentId);
            agentActivity?.SetTag("botnexus.session.id", sessionId);
            agentActivity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());

            using var getOrCreateActivity = GatewayDiagnostics.Source.StartActivity("session.get_or_create", ActivityKind.Internal);
            getOrCreateActivity?.SetTag("botnexus.session.id", sessionId);
            getOrCreateActivity?.SetTag("botnexus.agent.id", agentId);
            var existingSessionTask = _sessions.GetAsync(sessionId, cancellationToken);
            var existingSession = existingSessionTask is null ? null : await existingSessionTask;
            var session = existingSession ?? await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
            session.ChannelType ??= message.ChannelType;
            session.CallerId ??= message.SenderId;
            if (ShouldInitializeSystemPrompt(session))
            {
                session.Metadata[SystemPromptInitializedMetadataKey] = true;
                // Force fresh handle creation so isolation strategy rebuilds system prompt from workspace files.
                var stopTask = _supervisor.StopAsync(agentId, sessionId, cancellationToken);
                if (stopTask is not null)
                    await stopTask;
            }

            if (session.Status != SessionStatus.Active)
            {
                await SendSessionStatusRejectedAsync(message, agentId, sessionId, session.Status, cancellationToken);
                continue;
            }

            if (TryGetControlCommand(message, out var controlCommand) &&
                string.Equals(controlCommand, ControlSteer, StringComparison.OrdinalIgnoreCase))
            {
                await HandleSteeringAsync(message, agentId, sessionId, cancellationToken);
                continue;
            }

            session.AddEntry(new SessionEntry { Role = "user", Content = message.Content });

            try
            {
                var handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);

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
                    await StreamingSessionHelper.ProcessAndSaveAsync(
                        handle.StreamAsync(message.Content, cancellationToken),
                        session,
                        _sessions,
                        new StreamingSessionOptions(
                            IncludeErrorsInHistory: true,
                            OnEventAsync: (evt, ct) =>
                            {
                                if (channel is IStreamEventChannelAdapter streamEventChannel)
                                    return new ValueTask(streamEventChannel.SendStreamEventAsync(message.ConversationId, evt, ct));

                                if (evt.Type == AgentStreamEventType.ContentDelta && evt.ContentDelta is not null)
                                    return new ValueTask(channel.SendStreamDeltaAsync(message.ConversationId, evt.ContentDelta, ct));

                                return ValueTask.CompletedTask;
                            }),
                        _sessionLifecycleEvents,
                        cancellationToken);
                    sessionSaved = true;
                }
                else
                {
                    var response = await handle.PromptAsync(message.Content, cancellationToken);
                    if (ResolveChannelAdapter(message.ChannelType) is { } ch)
                    {
                        await ch.SendAsync(new OutboundMessage
                        {
                            ChannelType = message.ChannelType,
                            ConversationId = message.ConversationId,
                            Content = response.Content,
                            SessionId = sessionId
                        }, cancellationToken);
                    }

                    session.AddEntry(new SessionEntry { Role = "assistant", Content = response.Content });
                }

                if (!sessionSaved)
                {
                    using var saveActivity = GatewayDiagnostics.Source.StartActivity("session.save", ActivityKind.Internal);
                    saveActivity?.SetTag("botnexus.session.id", session.SessionId);
                    saveActivity?.SetTag("botnexus.agent.id", session.AgentId);
                    await _sessions.SaveAsync(session, cancellationToken);
                }

                await _activity.PublishAsync(new GatewayActivity
                {
                    Type = GatewayActivityType.AgentCompleted,
                    AgentId = agentId,
                    SessionId = sessionId
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Processing cancelled for agent '{AgentId}' session '{SessionId}' (client disconnected)", agentId, sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message for agent '{AgentId}' session '{SessionId}'", agentId, sessionId);

                try
                {
                    if (ResolveChannelAdapter(message.ChannelType) is { } errorChannel)
                    {
                        await errorChannel.SendAsync(new OutboundMessage
                        {
                            ChannelType = message.ChannelType,
                            ConversationId = message.ConversationId,
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
        SessionStatus status,
        CancellationToken cancellationToken)
    {
        var statusMessage = status == SessionStatus.Suspended
            ? "Session is suspended. Resume the session before sending new messages."
            : $"Session is in '{status}' state and cannot accept messages.";

        if (ResolveChannelAdapter(message.ChannelType) is { } channel)
        {
            await channel.SendAsync(new OutboundMessage
            {
                ChannelType = message.ChannelType,
                ConversationId = message.ConversationId,
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

    private async Task HandleSteeringAsync(
        InboundMessage message,
        string agentId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var instance = _supervisor.GetInstance(agentId, sessionId);
        IAgentHandle? handle = null;

        if (instance is not null)
        {
            try
            {
                handle = await _supervisor.GetOrCreateAsync(agentId, sessionId, cancellationToken);
            }
            catch { /* instance exists but handle creation failed */ }
        }

        // If no handle or agent is not actively running, steering can't be
        // injected mid-turn. Re-dispatch as a normal message to start a new run.
        if (handle is null || !handle.IsRunning)
        {
            _logger.LogInformation(
                "Steering received but agent is not running (instance={HasInstance}, running={IsRunning}). Re-dispatching as normal message for session {SessionId}",
                instance is not null, handle?.IsRunning ?? false, sessionId);

            var normalMessage = new InboundMessage
            {
                ChannelType = message.ChannelType,
                SenderId = message.SenderId,
                ConversationId = message.ConversationId,
                SessionId = message.SessionId,
                TargetAgentId = message.TargetAgentId,
                Content = message.Content,
                Metadata = new Dictionary<string, object?>()
            };

            await DispatchAsync(normalMessage, cancellationToken);
            return;
        }

        // Record steering message in session history
        var session = await _sessions.GetOrCreateAsync(sessionId, agentId, cancellationToken);
        session.AddEntry(new SessionEntry { Role = "user", Content = message.Content });
        await _sessions.SaveAsync(session, cancellationToken);

        await handle.SteerAsync(message.Content, cancellationToken);

        _logger.LogInformation("Steering message injected for agent {AgentId} session {SessionId}", agentId, sessionId);
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

    private static string GetQueueKey(InboundMessage message)
        => !string.IsNullOrWhiteSpace(message.SessionId)
            ? message.SessionId
            : $"{message.ChannelType}:{message.ConversationId}";

    private async Task SendBusyAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (ResolveChannelAdapter(message.ChannelType) is not { } channel)
            return;

        await channel.SendAsync(new OutboundMessage
        {
            ChannelType = message.ChannelType,
            ConversationId = message.ConversationId,
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

        var session = await _sessions.GetAsync(message.SessionId, cancellationToken);
        if (session?.Status is not SessionStatus.Closed)
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

    private IChannelAdapter? ResolveChannelAdapter(string channelType)
    {
        var adapter = _channelManager.Get(channelType);
        if (adapter is not null)
            return adapter;

        _logger.LogWarning("No channel adapter found for type '{ChannelType}'. Available: {Available}",
            channelType,
            string.Join(", ", _channelManager.Adapters.Select(a => a.ChannelType)));
        return null;
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
