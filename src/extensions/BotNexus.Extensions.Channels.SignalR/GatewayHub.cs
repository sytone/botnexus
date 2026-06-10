using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Abstractions.Services;
using AgentId = BotNexus.Domain.Primitives.AgentId;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using SessionId = BotNexus.Domain.Primitives.SessionId;
using UserId = BotNexus.Domain.Primitives.UserId;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using BotNexus.Domain.World;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.SignalR;

#pragma warning disable CS1591 // Hub methods are self-documenting SignalR contracts

/// <summary>
/// SignalR hub for real-time agent communication.
/// Clients join session groups and receive streaming output for all active sessions simultaneously.
/// </summary>
public sealed class GatewayHub : Hub<IGatewayHubClient>
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly ISessionStore _sessions;
    private readonly IInboundMessageOrchestrator _orchestrator;
    private readonly IActivityBroadcaster _activity;
    private readonly ISessionCompactor _compactor;
    private readonly ISessionWarmupService _warmup;
    private readonly IConversationDispatcher _conversationDispatcher;
    private readonly IConversationRouter _conversationRouter;
    private readonly IConversationStore? _conversationStore;
    private readonly IAskUserResponseRegistry? _askUserResponseRegistry;
    private readonly IOptionsMonitor<CompactionOptions> _compactionOptions;
    private readonly IConversationResetService? _resetService;
    private readonly ISessionCompactionCoordinator _compactionCoordinator;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        ISessionStore sessions,
        IInboundMessageOrchestrator orchestrator,
        IActivityBroadcaster activity,
        ISessionCompactor compactor,
        ISessionWarmupService warmup,
        IConversationDispatcher conversationDispatcher,
        IConversationRouter conversationRouter,
        IOptionsMonitor<CompactionOptions> compactionOptions,
        ILogger<GatewayHub> logger,
        IConversationStore? conversationStore = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        IConversationResetService? resetService = null,
        ISessionCompactionCoordinator? compactionCoordinator = null)
    {
        _supervisor = supervisor;
        _registry = registry;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _activity = activity;
        _compactor = compactor;
        _warmup = warmup;
        _conversationDispatcher = conversationDispatcher;
        _conversationRouter = conversationRouter;
        _compactionOptions = compactionOptions;
        _logger = logger;
        _conversationStore = conversationStore;
        _askUserResponseRegistry = askUserResponseRegistry;
        _resetService = resetService;
        // Required for the /compact RPC path. Tests constructing the hub directly
        // must pass one (see SignalRHubTests.CreateHub which uses TestSessionCompactionCoordinator).
        _compactionCoordinator = compactionCoordinator
            ?? throw new ArgumentNullException(nameof(compactionCoordinator),
                "ISessionCompactionCoordinator is required. Production hosts get it from DI; tests must inject one.");
    }

    /// <summary>
    /// Subscribes the connection to every conversation it currently has access to, so it
    /// receives streaming and event payloads for any session in those conversations —
    /// including sessions that are created later (e.g. after compaction within the
    /// conversation). Conversation-keyed groups are stable across compaction; the
    /// previous session-keyed groups dropped post-compaction deliveries (#682).
    /// </summary>
    /// <returns>The available sessions at subscribe time, for UI initialisation.</returns>
    public async Task<SubscribeAllResult> SubscribeAll()
    {
        var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);

        // Distinct conversation group keys across the returned sessions. Each session
        // contributes:
        //   1) the real "conversation:{conversationId}" group (production routing key —
        //      the conversation survives compaction so the connection keeps receiving),
        //   2) a back-compat "conversation:{sessionId}" synonym, so any caller that
        //      builds a ChannelStreamTarget from the sessionId only (legacy code paths
        //      and tests) still reaches the connection.
        var groupKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!string.IsNullOrWhiteSpace(session.ConversationId))
                groupKeys.Add(SignalRChannelAdapter.GetConversationGroup(session.ConversationId));
            groupKeys.Add(SignalRChannelAdapter.GetConversationGroup(session.SessionId));
        }

        foreach (var groupKey in groupKeys)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                groupKey,
                Context.ConnectionAborted);
        }

        _logger.LogInformation(
            "Hub SubscribeAll: connection={ConnectionId} sessions={SessionCount} groups={GroupCount}",
            Context.ConnectionId,
            sessions.Count,
            groupKeys.Count);

        return new SubscribeAllResult(sessions);
    }

    /// <summary>
    /// Sends a message to an agent, optionally routing into a specific conversation.
    /// When conversationId is provided, the router looks up the conversation directly
    /// without a binding scan. When null, routes via the default (agentId, channelType) binding.
    /// </summary>
    /// <param name="agentId">The target agent.</param>
    /// <param name="channelType">The channel type.</param>
    /// <param name="content">The message content.</param>
    /// <param name="conversationId">Optional explicit conversation ID. When set, routes directly to that conversation.</param>
    /// <returns>The send message result.</returns>
    public Task<SendMessageResult> SendMessage(AgentId agentId, ChannelKey channelType, string content, string? conversationId = null)
        => SendMessageCore(agentId, channelType, content, conversationId);

    private async Task<SendMessageResult> SendMessageCore(AgentId agentId, ChannelKey channelType, string content, string? conversationId)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedChannelType = NormalizeChannelKey(channelType);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var normalizedConversationId = string.IsNullOrWhiteSpace(conversationId)
            ? null
            : conversationId;

        // Resolve the (sessionId, conversationId) the inbound message will land on so the
        // caller is in the conversation group before the orchestrator's worker emits stream
        // events. Session-row mutation (status / channel / participants) is owned by
        // GatewayHost.ProcessAsync downstream — see #721.
        var resolution = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType, normalizedConversationId);
        // The router's GetOrCreate+SaveAsync (called inside the dispatcher) pins ConversationId
        // on the session row; subscribe by conversation so streams reach this connection even
        // if the active session id later changes (compaction). #682
        await SubscribeConversationInternalAsync(resolution.ConversationId);

        var connectionId = Context.ConnectionId;
        _logger.LogInformation("Hub SendMessage: agent={AgentId} channel={ChannelType} session={SessionId} connection={ConnectionId} content={Content}",
            typedAgentId, typedChannelType, resolution.SessionId, connectionId, content.Length > 50 ? content[..50] + "..." : content);

        _ = SafeDispatchAsync(
            () => DispatchMessageAsync(typedAgentId, resolution.SessionId, content, "message", connectionId, normalizedConversationId),
            typedAgentId,
            resolution.SessionId);

        return new SendMessageResult(
            resolution.SessionId.Value,
            resolution.AgentId.Value,
            resolution.ChannelType.Value);
    }

    /// <summary>
    /// Completes a pending <c>ask_user</c> request for the active conversation without
    /// entering the normal session dispatch queue.
    /// </summary>
    public async Task RespondToAskUser(
        string conversationId,
        string requestId,
        string? freeFormText,
        string[]? selectedValues,
        bool cancelled)
    {
        if (_askUserResponseRegistry is null || _conversationStore is null)
            throw new HubException("ask_user response handling is not available.");

        if (string.IsNullOrWhiteSpace(conversationId))
            throw new HubException("Conversation ID is required.");
        if (string.IsNullOrWhiteSpace(requestId))
            throw new HubException("Request ID is required.");

        var normalizedConversationId = ConversationId.From(conversationId);
        var conversation = await _conversationStore.GetAsync(normalizedConversationId, Context.ConnectionAborted);
        if (conversation is null)
            throw new HubException($"Conversation '{normalizedConversationId.Value}' not found.");

        var hasSignalRBinding = conversation.ChannelBindings.Any(binding =>
            binding.ChannelType.Equals(ChannelKey.From("signalr")));
        if (!hasSignalRBinding)
            throw new HubException("Caller does not have access to this conversation.");

        var response = new AskUserResponse
        {
            RequestId = requestId.Trim(),
            FreeFormText = string.IsNullOrWhiteSpace(freeFormText) ? null : freeFormText.Trim(),
            SelectedValues = selectedValues is { Length: > 0 }
                ? selectedValues
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToArray()
                : null,
            WasCancelled = cancelled
        };

        if (!_askUserResponseRegistry.TryComplete(normalizedConversationId, response.RequestId, response))
            throw new HubException("No matching ask_user request is pending for this conversation.");
    }

    /// <summary>
    /// Sends a message with media content parts to an agent.
    /// </summary>
    /// <param name="agentId">The target agent.</param>
    /// <param name="channelType">The channel type.</param>
    /// <param name="content">Text content of the message.</param>
    /// <param name="contentParts">Media content parts (base64-encoded binary data).</param>
    /// <returns>The send message result including session info.</returns>
    public async Task<SendMessageResult> SendMessageWithMedia(
        AgentId agentId,
        ChannelKey channelType,
        string content,
        IReadOnlyList<MediaContentPartDto> contentParts)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedChannelType = NormalizeChannelKey(channelType);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var resolution = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType);
        await SubscribeConversationInternalAsync(resolution.ConversationId);

        _logger.LogInformation(
            "Hub SendMessageWithMedia: agent={AgentId} channel={ChannelType} session={SessionId} parts={PartCount}",
            typedAgentId, typedChannelType, resolution.SessionId, contentParts.Count);

        var connectionId = Context.ConnectionId;
        var parts = contentParts.Select(ConvertToDomainContentPart).ToList();

        _ = SafeDispatchAsync(
            () => _orchestrator.AcceptAsync(
                new InboundMessage
                {
                    ChannelType = ChannelKey.From("signalr"),
                    SenderId = connectionId,
                    Sender = CitizenId.Of(UserId.From(connectionId)),
                    ChannelAddress = ChannelAddress.From(typedAgentId.Value), // stable per-agent address — one portal conversation per agent
                    RoutingHints = new InboundMessageRoutingHints(
                        RequestedAgentId: typedAgentId,
                        RequestedSessionId: resolution.SessionId,
                        RequestedConversationId: null),
                    Content = content,
                    ContentParts = parts,
                    Metadata = new Dictionary<string, object?> { ["messageType"] = "message-with-media" }
                },
                CancellationToken.None),
            typedAgentId,
            resolution.SessionId);

        return new SendMessageResult(
            resolution.SessionId.Value,
            resolution.AgentId.Value,
            resolution.ChannelType.Value);
    }

    private Task DispatchMessageAsync(AgentId typedAgentId, SessionId typedSessionId, string content,
        string messageType, string senderId, string? conversationId = null)
        => _orchestrator.AcceptAsync(
            new InboundMessage
            {
                ChannelType = ChannelKey.From("signalr"),
                SenderId = senderId,
                Sender = CitizenId.Of(UserId.From(senderId)),
                ChannelAddress = ChannelAddress.From(typedAgentId.Value), // stable per-agent address — one portal conversation per agent
                RoutingHints = new InboundMessageRoutingHints(
                    RequestedAgentId: typedAgentId,
                    RequestedSessionId: typedSessionId,
                    RequestedConversationId: string.IsNullOrWhiteSpace(conversationId) ? null : ConversationId.From(conversationId)),
                Content = content,
                Metadata = new Dictionary<string, object?> { ["messageType"] = messageType }
            },
            CancellationToken.None);

    /// <summary>
    /// Fire-and-forget dispatch wrapper. Catches exceptions and publishes them
    /// via the activity stream so the client receives error events.
    /// </summary>
    private async Task SafeDispatchAsync(Func<Task> dispatch, AgentId agentId, SessionId sessionId)
    {
        try
        {
            await dispatch();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Dispatch failed for agent '{AgentId}' session '{SessionId}'",
                agentId,
                sessionId);

            try
            {
                await _activity.PublishAsync(
                    new GatewayActivity
                    {
                        Type = GatewayActivityType.Error,
                        AgentId = agentId.Value,
                        SessionId = sessionId.Value,
                        Message = $"Agent dispatch failed: {ex.Message}"
                    },
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort error notification
            }
        }
    }

    private static MessageContentPart ConvertToDomainContentPart(MediaContentPartDto dto)
    {
        if (dto.Text is not null)
            return new TextContentPart { MimeType = dto.MimeType, Text = dto.Text };

        if (dto.Base64Data is not null)
            return new BinaryContentPart
            {
                MimeType = dto.MimeType,
                Data = Convert.FromBase64String(dto.Base64Data),
                FileName = dto.FileName
            };

        throw new ArgumentException("MediaContentPartDto must have either Text or Base64Data");
    }

    /// <summary>
    /// Executes steer.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="content">The content.</param>
    /// <returns>The steer result.</returns>
    public async Task<SendMessageResult> Steer(AgentId agentId, SessionId sessionId, string content, string? conversationId)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);
        var typedChannelType = ChannelKey.From("signalr");
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        // Subscribe so post-compaction streams continue to arrive. #682
        await SubscribeInternalAsync(typedSessionId);

        // Steering bypasses the per-session orchestrator queue and calls the handle
        // directly. The old design routed steer through AcceptAsync → FIFO queue,
        // which serialized behind the running dispatch — by the time the steer was
        // dequeued, the agent had already finished and the steer was discarded.
        //
        // New design: get the live handle, call SteerAsync regardless of IsRunning.
        // The agent's PendingMessageQueue accepts steers at any time; they are
        // drained at the next turn boundary (mid-run) or at the start of the next
        // run (if agent is idle). This matches how InterruptAndSteer already works.
        var handle = _supervisor.GetHandle(typedAgentId, typedSessionId);

        if (handle is not null)
        {
            // Record steering message in session history
            var session = await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, Context.ConnectionAborted);
            session.AddEntry(new SessionEntry
            {
                Role = BotNexus.Domain.Primitives.MessageRole.User,
                Content = content
            });
            await _sessions.SaveAsync(session, Context.ConnectionAborted);

            // Inject into agent's steering queue (works whether running or idle)
            await handle.SteerAsync(content, Context.ConnectionAborted);

            await _activity.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.SteeringInjected,
                AgentId = typedAgentId.Value,
                SessionId = typedSessionId.Value,
                ConversationId = conversationId
            }, Context.ConnectionAborted);

            _logger.LogInformation(
                "Steering injected for agent {AgentId} session {SessionId} (running={IsRunning})",
                typedAgentId.Value, typedSessionId.Value, handle.IsRunning);
        }
        else
        {
            // No handle exists — agent has never been started for this session,
            // or was disposed. Queue the steer as a normal message so it triggers
            // a new agent run (the orchestrator will create the handle on dispatch).
            _logger.LogInformation(
                "Steering queued as message for agent {AgentId} session {SessionId} (no active handle)",
                typedAgentId.Value, typedSessionId.Value);

            _ = SafeDispatchAsync(
                () => _orchestrator.AcceptAsync(
                    new InboundMessage
                    {
                        ChannelType = typedChannelType,
                        SenderId = Context.ConnectionId,
                        Sender = CitizenId.Of(UserId.From(Context.ConnectionId)),
                        ChannelAddress = ChannelAddress.From(typedAgentId.Value),
                        RoutingHints = new InboundMessageRoutingHints(
                            RequestedAgentId: typedAgentId,
                            RequestedSessionId: typedSessionId,
                            RequestedConversationId: string.IsNullOrWhiteSpace(conversationId) ? null : ConversationId.From(conversationId)),
                        Content = content,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["messageType"] = "steer"
                        }
                    },
                    CancellationToken.None),
                typedAgentId,
                typedSessionId);

            await _activity.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.SteeringQueued,
                AgentId = typedAgentId.Value,
                SessionId = typedSessionId.Value,
                ConversationId = conversationId
            }, Context.ConnectionAborted);
        }

        return new SendMessageResult(typedSessionId.Value, typedAgentId.Value, typedChannelType.Value);
    }

    /// <summary>
    /// Interrupts the active agent turn and redirects it with a new message.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="message">The new direction message to steer the agent toward.</param>
    /// <returns><c>true</c> if a running handle was found and interrupted; <c>false</c> if no active handle exists.</returns>
    public async Task<bool> InterruptAndSteer(AgentId agentId, SessionId sessionId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);

        var handle = _supervisor.GetHandle(typedAgentId, typedSessionId);
        if (handle is null)
            return false;

        await handle.InterruptAndSteerAsync(message, Context.ConnectionAborted);
        return true;
    }

    /// <summary>
    /// Executes follow up.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="content">The content.</param>
    /// <returns>The follow up result.</returns>
    public Task FollowUp(AgentId agentId, SessionId sessionId, string content)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);
        var connectionId = Context.ConnectionId;
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        _ = SafeDispatchAsync(
            async () =>
            {
                // Late-subscribe by conversation so stream events emitted by the
                // follow-up reach the connection even if SubscribeAll has not been
                // invoked recently. #682
                await SubscribeInternalAsync(typedSessionId);
                await DispatchMessageAsync(typedAgentId, typedSessionId, content, "message", connectionId);
            },
            typedAgentId,
            typedSessionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes abort.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The abort result.</returns>
    public async Task Abort(AgentId agentId, SessionId sessionId)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);
        var instance = _supervisor.GetInstance(typedAgentId, typedSessionId);
        if (instance is null)
            return;

        var handle = await _supervisor.GetOrCreateAsync(typedAgentId, typedSessionId, CancellationToken.None);
        await handle.AbortAsync(CancellationToken.None);
    }

    /// <summary>
    /// Resets the caller's active session: stops the agent, flushes session-end memory,
    /// cancels any pending ask-user prompts, seals the session, and clears the
    /// conversation's <see cref="Conversation.ActiveSessionId"/> so the next inbound
    /// message creates a fresh session inside the same conversation.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="IConversationResetService"/> when a conversation is bound,
    /// guarding against stale <paramref name="sessionId"/> values by passing it as the
    /// expected active session. Sessions without a conversation (legacy/orphans) are
    /// sealed in place — no transcript-destroying <see cref="ISessionStore.ArchiveAsync"/>.
    /// </remarks>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id known to the caller.</param>
    /// <returns>A task that completes when the caller has been notified.</returns>
    public async Task ResetSession(AgentId agentId, SessionId sessionId)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);

        var gatewaySession = await _sessions.GetAsync(typedSessionId, CancellationToken.None);

        if (gatewaySession is not null && gatewaySession.ConversationId.IsInitialized() && _resetService is not null)
        {
            await _resetService.ResetActiveSessionAsync(
                gatewaySession.ConversationId,
                expectedActiveSessionId: typedSessionId,
                CancellationToken.None).ConfigureAwait(false);
        }
        else if (gatewaySession is not null)
        {
            // Orphan/legacy session with no conversation link: stop the agent and seal
            // the session in place (Status=Sealed + SaveAsync). Deliberately avoids
            // ArchiveAsync because the InMemory implementation deletes the row and the
            // file store renames files out of normal lookup — both destroy transcript
            // readability for any UI that lists historical sessions.
            try
            {
                await _supervisor.StopAsync(typedAgentId, typedSessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Supervisor stop failed for orphan session {SessionId}; reset will proceed.", typedSessionId);
            }

            gatewaySession.Status = GatewaySessionStatus.Sealed;
            gatewaySession.UpdatedAt = DateTimeOffset.UtcNow;
            await _sessions.SaveAsync(gatewaySession, CancellationToken.None).ConfigureAwait(false);
        }

        await Clients.Caller.SessionReset(new SessionResetPayload(
            typedAgentId.Value,
            typedSessionId.Value,
            gatewaySession is not null && gatewaySession.ConversationId.IsInitialized()
                ? gatewaySession.ConversationId.Value
                : null));
    }

    /// <summary>
    /// Executes compact session.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The compact session result.</returns>
    public async Task<CompactSessionResult> CompactSession(AgentId agentId, SessionId sessionId)
    {
        var typedAgentId = NormalizeAgentId(agentId);
        var typedSessionId = NormalizeSessionId(sessionId);
        var session = await _sessions.GetAsync(typedSessionId, CancellationToken.None);
        if (session is null)
            throw new HubException($"Session '{typedSessionId.Value}' not found.");

        // Resolve through the request services so test hosts can substitute the
        // coordinator. Falls back to the singleton injected at hub construction.
        var requestServices = Context.GetHttpContext()?.RequestServices;
        var coordinator = requestServices?.GetService<ISessionCompactionCoordinator>() ?? _compactionCoordinator;

        var outcome = await coordinator.CompactAsync(typedAgentId, session, CancellationToken.None, force: true).ConfigureAwait(false);

        return new CompactSessionResult(
            outcome.EntriesSummarized,
            outcome.EntriesPreserved,
            outcome.TokensBefore,
            outcome.TokensAfter);
    }

    /// <summary>
    /// Executes get agents.
    /// </summary>
    /// <returns>The get agents result.</returns>
    public Task<IReadOnlyList<AgentDescriptor>> GetAgents()
        => Task.FromResult(_registry.GetAll());

    /// <summary>
    /// Executes get agent status.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The get agent status result.</returns>
    public AgentInstance? GetAgentStatus(AgentId agentId, SessionId sessionId)
        => _supervisor.GetInstance(NormalizeAgentId(agentId), NormalizeSessionId(sessionId));

    /// <summary>
    /// Executes on connected async.
    /// </summary>
    /// <returns>The on connected async result.</returns>
    public override async Task OnConnectedAsync()
    {
        var clientVersion = Context.GetHttpContext()?.Request.Query["clientVersion"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation("Hub OnConnected: connection={ConnectionId} clientVersion={ClientVersion}",
            Context.ConnectionId, clientVersion);

        await Clients.Caller.Connected(new ConnectedPayload(
            Context.ConnectionId,
            _registry.GetAll().Select(a => new AgentSummary(a.AgentId.Value, a.DisplayName, a.Emoji, a.Description)),
            typeof(GatewayHub).Assembly.GetName().Version?.ToString() ?? "dev",
            new HubCapabilities(MultiSession: true)));

        await _activity.PublishAsync(
            new GatewayActivity
            {
                Type = GatewayActivityType.System,
                ChannelType = ChannelKey.From("signalr"),
                Message = "Web Chat client connected.",
                Data = new Dictionary<string, object?> { ["connectionId"] = Context.ConnectionId }
            },
            Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Executes on disconnected async. Mutes all channel bindings for this SignalR connection
    /// so fan-out stops delivering to dead connections on future requests.
    /// </summary>
    /// <param name="exception">The disconnect exception, if any.</param>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null && IsBenignConnectionException(exception))
            _logger.LogDebug(exception, "Hub OnDisconnected: benign pre-handshake close for connection {ConnectionId}", Context.ConnectionId);
        else if (exception is not null)
            _logger.LogWarning(exception, "Hub OnDisconnected: unexpected exception for connection {ConnectionId}", Context.ConnectionId);
        else
            _logger.LogInformation("Hub OnDisconnected: connection={ConnectionId}", Context.ConnectionId);

        // Best-effort: mute any Interactive/NotifyOnly bindings keyed to this connection ID.
        // If the store lookup fails, fan-out self-healing via StaleChannelConnectionException
        // will catch remaining deliveries on the next send attempt.
        try
        {
            await _conversationRouter.MuteBindingByAddressAsync(
                // Pass null to search all agents' conversations for bindings keyed to this connection.
                agentId: null,
                ChannelKey.From("signalr"),
                ChannelAddress.From(Context.ConnectionId),
                Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hub OnDisconnected: failed to mute bindings for connection {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // GetSessionGroup is gone — the hub now subscribes/unsubscribes via SignalRChannelAdapter.GetConversationGroup.
    // Removing this prevents future regressions back to session-keyed routing (#682).

    // AgentId is a Vogen value object; the parameter cannot be default(AgentId) (compile-time
    // VOG009) and is already validated and trimmed at construction. The defensive re-construction
    // step is therefore a pass-through. SessionId and ChannelKey are still hand-rolled and need
    // the try/catch below until they migrate to Vogen.
    private static AgentId NormalizeAgentId(AgentId agentId) => agentId;

    private static SessionId NormalizeSessionId(SessionId sessionId)
    {
        try
        {
            return SessionId.From(sessionId.Value);
        }
        catch (ArgumentException)
        {
            throw new HubException("Session ID is required.");
        }
    }

    private static ChannelKey NormalizeChannelKey(ChannelKey channelType)
    {
        try
        {
            return ChannelKey.From(channelType.Value);
        }
        catch (ArgumentException)
        {
            throw new HubException("Channel type is required.");
        }
    }

    /// <summary>
    /// Adds the connection to the conversation group for the given session, looking up the
    /// session's conversation id if needed. Conversation-keyed groups survive session
    /// compaction; the previous session-keyed equivalent did not (#682).
    /// </summary>
    private async Task SubscribeInternalAsync(SessionId sessionId)
    {
        var session = await _sessions.GetAsync(sessionId, Context.ConnectionAborted);
        if (session is null)
            return;

        await SubscribeConversationInternalAsync(session.ConversationId);
    }

    /// <summary>
    /// Adds the connection to the conversation group when the conversation id is already
    /// resolved. No-op when the conversation id is not initialised (legacy/orphan path).
    /// </summary>
    private async Task SubscribeConversationInternalAsync(ConversationId conversationId)
    {
        if (!conversationId.IsInitialized())
            return;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SignalRChannelAdapter.GetConversationGroup(conversationId.Value),
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Resolves the (sessionId, conversationId) pair the inbound message will land on by
    /// delegating to the shared <see cref="IConversationDispatcher"/>. Returns a lightweight
    /// <see cref="HubInboundResolution"/> that carries just the IDs the hub needs to
    /// (a) populate the synchronous <see cref="SendMessageResult"/> contract and
    /// (b) join the caller connection to the conversation group before the orchestrator's
    /// background worker emits stream events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a pure resolution call. Session row materialisation, ChannelType / SessionType
    /// stamping, Expired→Active reactivation, transcript SaveAsync, and caller participant
    /// registration are owned by <c>GatewayHost.ProcessAsync</c> inside the orchestrator's
    /// per-session worker (#721). The router invoked by the dispatcher already calls
    /// <c>SessionStore.GetOrCreateAsync</c> + <c>SaveAsync</c> as a side-effect of binding
    /// resolution, so the session row exists by the time this method returns.
    /// </para>
    /// <para>
    /// One eventual-consistency window survives this slim-down: a reused Expired session
    /// stays Expired until the orchestrator's worker reactivates it. SignalR streaming is
    /// unaffected because the agent only emits after reactivation; polling REST callers
    /// that fire within the small worker-wake window may observe the pre-reactivation row.
    /// </para>
    /// </remarks>
    private async Task<HubInboundResolution> ResolveOrCreateSessionAsync(
        AgentId agentId,
        ChannelKey channelType,
        string? conversationId = null)
    {
        // Conversation-first routing: use agentId as the channel address so every connection
        // from the same agent routes to the same portal conversation, regardless of SignalR
        // connection ID.
        var channelAddress = ChannelAddress.From(agentId.Value);
        var typedConversationId = string.IsNullOrWhiteSpace(conversationId)
            ? (ConversationId?)null
            : ConversationId.From(conversationId);
        var context = new InboundMessageContext(
            agentId,
            new InboundMessage
            {
                ChannelType = channelType,
                ChannelAddress = channelAddress,
                SenderId = Context.ConnectionId,
                Sender = CitizenId.Of(UserId.From(Context.ConnectionId)),
                Content = string.Empty,
                // RoutingHints intentionally omitted — the outer InboundMessageContext below
                // carries the typed RequestedAgentId + RequestedConversationId explicitly.
            },
            new ChannelSource(
                channelType,
                channelAddress,
                Context.ConnectionId),
            RequestedConversationId: typedConversationId,
            RequestedAgentId: agentId);

        var dispatchResult = await _conversationDispatcher.DispatchAsync(context, Context.ConnectionAborted);
        return new HubInboundResolution(
            dispatchResult.Resolution.SessionId,
            dispatchResult.Resolution.ConversationId,
            agentId,
            channelType);
    }

    /// <summary>
    /// Resolved identifiers returned by <see cref="ResolveOrCreateSessionAsync"/>. Carries
    /// only what the hub needs synchronously to subscribe the caller connection to the
    /// conversation group and to return <see cref="SendMessageResult"/>. Heavier session
    /// mutation (status, channel stamp, participant add) is owned by
    /// <c>GatewayHost.ProcessAsync</c> downstream.
    /// </summary>
    private readonly record struct HubInboundResolution(
        SessionId SessionId,
        ConversationId ConversationId,
        AgentId AgentId,
        ChannelKey ChannelType);

    /// <summary>
    /// Returns <see langword="true"/> for exceptions that are expected side-effects of a
    /// WebSocket client disconnecting before (or during) the SignalR handshake — e.g.
    /// the browser navigating away, a network blip, or a rapid reconnect cycle. These
    /// are logged at Debug level instead of Warning to avoid Application Insights noise.
    /// </summary>
    public static bool IsBenignConnectionException(Exception ex)
    {
        // Unwrap IOException wrappers (ASP.NET Kestrel sometimes wraps WebSocketException
        // in an IOException when the underlying stream is torn down).
        var inner = ex is System.IO.IOException ioEx ? ioEx.InnerException ?? ioEx : ex;

        if (inner is OperationCanceledException)
            return true;

        if (inner is System.Net.WebSockets.WebSocketException wse)
        {
            return wse.WebSocketErrorCode == System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely
                || wse.Message.Contains("closed before the connection was established", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

