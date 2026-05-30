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
using SessionParticipant = BotNexus.Domain.Primitives.SessionParticipant;
using BotNexus.Domain.World;
using SessionType = BotNexus.Domain.Primitives.SessionType;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.SignalR;

#pragma warning disable CS1591 // Hub methods are self-documenting SignalR contracts

/// <summary>
/// SignalR hub for real-time agent communication. Replaces the raw WebSocket infrastructure.
/// Clients join session groups and receive streaming output for all active sessions simultaneously.
/// </summary>
public sealed class GatewayHub : Hub<IGatewayHubClient>
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly ISessionStore _sessions;
    private readonly IChannelDispatcher _dispatcher;
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
        IChannelDispatcher dispatcher,
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
        _dispatcher = dispatcher;
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
    /// Executes subscribe all.
    /// </summary>
    /// <returns>The subscribe all result.</returns>
    public async Task<SubscribeAllResult> SubscribeAll()
    {
        var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);

        foreach (var session in sessions)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                GetSessionGroup(SessionId.From(session.SessionId)),
                Context.ConnectionAborted);
        }

        _logger.LogInformation(
            "Hub SubscribeAll: connection={ConnectionId} sessions={Count}",
            Context.ConnectionId,
            sessions.Count);

        return new SubscribeAllResult(sessions);
    }

    /// <summary>
    /// Executes join session.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The join session result.</returns>
    [Obsolete("JoinSession is deprecated. Use SubscribeAll and SendMessage(agentId, channelType, content).")]
    public async Task<JoinSessionResult> JoinSession(string agentId, string? sessionId)
    {
        var typedAgentId = ParseAgentId(agentId);
        var typedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? SessionId.Create()
            : ParseSessionId(sessionId);

        _logger.LogInformation("Hub JoinSession: agent={AgentId} session={SessionId} connection={ConnectionId} group={Group}",
            typedAgentId, typedSessionId, Context.ConnectionId, GetSessionGroup(typedSessionId));
        await Groups.AddToGroupAsync(Context.ConnectionId, GetSessionGroup(typedSessionId), Context.ConnectionAborted);

        var session = await _sessions.GetOrCreateAsync(typedSessionId, typedAgentId, Context.ConnectionAborted);

        var needsSave = false;
        if (session.Status == SessionStatus.Expired)
        {
            _logger.LogInformation("Reactivating expired session {SessionId} on join", typedSessionId);
            session.Status = SessionStatus.Active;
            session.ExpiresAt = null;
            needsSave = true;
        }

        if (session.ChannelType is null)
        {
            session.ChannelType = ChannelKey.From("signalr");
            needsSave = true;
        }

        session.SessionType = SessionType.UserAgent;
        if (needsSave)
        {
            await _sessions.SaveAsync(session, Context.ConnectionAborted);
        }

        // P9-F: Participants live on the Conversation, not the Session. The user-citizen
        // for this SignalR connection is registered against the conversation pinned to the
        // session. Skipped when the session has not yet been pinned (rare; resolution
        // usually fires before JoinSession).
        if (_conversationStore is not null && session.ConversationId.IsInitialized())
        {
            await _conversationStore.AddParticipantsAsync(
                session.ConversationId,
                [new SessionParticipant
                {
                    CitizenId = CitizenId.Of(UserId.From(Context.ConnectionId))
                }],
                Context.ConnectionAborted);
        }

        return new JoinSessionResult(
            session.SessionId.Value,
            session.AgentId.Value,
            Context.ConnectionId,
            session.History.Count,
            session.History.Count > 0,
            session.Status.ToString(),
            session.ChannelType?.Value,
            session.CreatedAt,
            session.UpdatedAt);
    }

    /// <summary>
    /// Executes leave session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The leave session result.</returns>
    [Obsolete("LeaveSession is deprecated. Clients remain subscribed via SubscribeAll.")]
    public Task LeaveSession(string sessionId)
        => Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            GetSessionGroup(ParseSessionId(sessionId)),
            Context.ConnectionAborted);

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
            : conversationId.Trim();

        // Resolve/create the session using the same conversation context as the inbound message
        // so non-default conversations stay aligned between hub subscriptions and gateway routing.
        var session = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType, normalizedConversationId);
        await SubscribeInternalAsync(session.SessionId);

        var connectionId = Context.ConnectionId;
        _logger.LogInformation("Hub SendMessage: agent={AgentId} channel={ChannelType} session={SessionId} connection={ConnectionId} content={Content}",
            typedAgentId, typedChannelType, session.SessionId, connectionId, content.Length > 50 ? content[..50] + "..." : content);

        _ = SafeDispatchAsync(
            () => DispatchMessageAsync(typedAgentId, session.SessionId, content, "message", connectionId, normalizedConversationId),
            typedAgentId,
            session.SessionId);

        return new SendMessageResult(
            session.SessionId.Value,
            session.AgentId.Value,
            session.ChannelType?.Value);
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

        var normalizedConversationId = ConversationId.From(conversationId.Trim());
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

        var session = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType);
        await SubscribeInternalAsync(session.SessionId);

        _logger.LogInformation(
            "Hub SendMessageWithMedia: agent={AgentId} channel={ChannelType} session={SessionId} parts={PartCount}",
            typedAgentId, typedChannelType, session.SessionId, contentParts.Count);

        var connectionId = Context.ConnectionId;
        var parts = contentParts.Select(ConvertToDomainContentPart).ToList();

        _ = SafeDispatchAsync(
            () => _dispatcher.DispatchAsync(
                new InboundMessage
                {
                    ChannelType = ChannelKey.From("signalr"),
                    SenderId = connectionId,
                    Sender = CitizenId.Of(UserId.From(connectionId)),
                    ChannelAddress = ChannelAddress.From(typedAgentId.Value), // stable per-agent address — one portal conversation per agent
                    RoutingHints = new InboundMessageRoutingHints(
                        RequestedAgentId: typedAgentId,
                        RequestedSessionId: session.SessionId,
                        RequestedConversationId: null),
                    Content = content,
                    ContentParts = parts,
                    Metadata = new Dictionary<string, object?> { ["messageType"] = "message-with-media" }
                },
                CancellationToken.None),
            typedAgentId,
            session.SessionId);

        return new SendMessageResult(
            session.SessionId.Value,
            session.AgentId.Value,
            session.ChannelType?.Value);
    }

    private Task DispatchMessageAsync(AgentId typedAgentId, SessionId typedSessionId, string content,
        string messageType, string senderId, string? conversationId = null)
        => _dispatcher.DispatchAsync(
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

        // Steering must target the caller-provided session so the control message
        // reaches the active conversation the user is currently steering.
        await SubscribeInternalAsync(typedSessionId);

        var connectionId = Context.ConnectionId;
        var normalizedConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();

        _ = SafeDispatchAsync(
            () => _dispatcher.DispatchAsync(
                new InboundMessage
                {
                    ChannelType = typedChannelType,
                    SenderId = connectionId,
                    Sender = CitizenId.Of(UserId.From(connectionId)),
                    ChannelAddress = ChannelAddress.From(typedAgentId.Value),
                    RoutingHints = new InboundMessageRoutingHints(
                        RequestedAgentId: typedAgentId,
                        RequestedSessionId: typedSessionId,
                        RequestedConversationId: normalizedConversationId is null ? null : ConversationId.From(normalizedConversationId)),
                    Content = content,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["messageType"] = "steer",
                        ["control"] = "steer"
                    }
                },
                CancellationToken.None),
            typedAgentId,
            typedSessionId);

        return new SendMessageResult(typedSessionId.Value, typedAgentId.Value, typedChannelType.Value);
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
            () => DispatchMessageAsync(typedAgentId, typedSessionId, content, "message", connectionId),
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

        await Clients.Caller.SessionReset(new SessionResetPayload(typedAgentId.Value, typedSessionId.Value));
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

        var outcome = await coordinator.CompactAsync(typedAgentId, session, CancellationToken.None).ConfigureAwait(false);

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

    private static string GetSessionGroup(SessionId sessionId) => $"session:{sessionId.Value}";

    private static AgentId ParseAgentId(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new HubException("Agent ID is required.");

        return AgentId.From(agentId);
    }

    private static SessionId ParseSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new HubException("Session ID is required.");

        return SessionId.From(sessionId);
    }
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

    private async Task SubscribeInternalAsync(SessionId sessionId)
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetSessionGroup(sessionId),
            Context.ConnectionAborted);
    }

    private async Task<GatewaySession> ResolveOrCreateSessionAsync(
        AgentId agentId,
        ChannelKey channelType,
        string? conversationId = null)
    {
        // Conversation-first routing: resolve/create via IConversationDispatcher.
        // Use agentId as the channel address so every connection from the same agent
        // routes to the same portal conversation, regardless of SignalR connection ID.
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
                // carries the typed RequestedAgentId + RequestedConversationId explicitly;
                // the positional-record constructor does not re-derive hints from the message
                // (only FromInboundMessage does that). Sub-PR 6.3 (#586) deleted the legacy
                // string override fields, so the inner message carries no routing payload.
            },
            new ChannelSource(
                channelType,
                channelAddress,
                Context.ConnectionId),
            RequestedConversationId: typedConversationId,
            RequestedAgentId: agentId);
        var dispatchResult = await _conversationDispatcher.DispatchAsync(context, Context.ConnectionAborted);

        var session = await _sessions.GetOrCreateAsync(dispatchResult.Resolution.SessionId, agentId, Context.ConnectionAborted);

        var needsSave = false;
        if (session.Status == SessionStatus.Expired)
        {
            session.Status = SessionStatus.Active;
            session.ExpiresAt = null;
            needsSave = true;
        }

        if (!session.ChannelType.HasValue || !ChannelMatches(session.ChannelType.Value, channelType))
        {
            session.ChannelType = channelType;
            needsSave = true;
        }

        if (!session.SessionType.Equals(SessionType.UserAgent))
        {
            session.SessionType = SessionType.UserAgent;
            needsSave = true;
        }

        if (needsSave)
            await _sessions.SaveAsync(session, Context.ConnectionAborted);

        // P9-F: Participants live on the Conversation, not the Session — register the
        // user-citizen against the conversation pinned to this session. Skipped when the
        // session has not yet been pinned or the conversation store isn't wired (legacy
        // composition roots).
        if (_conversationStore is not null && session.ConversationId.IsInitialized())
        {
            await _conversationStore.AddParticipantsAsync(
                session.ConversationId,
                [new SessionParticipant
                {
                    CitizenId = CitizenId.Of(UserId.From(Context.ConnectionId))
                }],
                Context.ConnectionAborted);
        }

        return session;
    }

    private static bool ChannelMatches(ChannelKey? candidate, ChannelKey target)
    {
        if (!candidate.HasValue)
            return target.Equals(ChannelKey.From("signalr"));

        return ChannelMatches(candidate.Value, target);
    }

    private static bool ChannelMatches(ChannelKey candidate, ChannelKey target)
        => candidate.Equals(target);
}

