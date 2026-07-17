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
using BotNexus.Domain;
using BotNexus.Gateway.Abstractions.Citizens;
using BotNexus.Gateway.Abstractions.Configuration;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

#pragma warning disable CS1591 // Hub methods are self-documenting SignalR contracts

/// <summary>
/// SignalR hub for real-time agent communication.
/// Clients join session groups and receive streaming output for all active sessions simultaneously.
/// When JWT Bearer authentication is configured, unauthenticated connections are rejected.
/// When no authentication scheme is registered, the hub permits anonymous access
/// for backward compatibility during the Phase 1 transition.
/// </summary>
/// <remarks>
/// The hub is a thin transport adapter: SignalR-context-bound collaborators (supervisor,
/// session store, activity broadcaster, conversation router/store, ask-user registry) are
/// injected directly, while the gateway's inbound-dispatch, warmup, conversation-resolution,
/// compaction, and conversation-reset operations are grouped behind
/// <see cref="IGatewayHubApplicationService"/>. Every client-callable control method resolves
/// its ids once through <see cref="ResolveCallContext"/> and publishes activity through the
/// shared <see cref="PublishActivityAsync"/> path so id-normalisation and envelope construction
/// live in one place.
/// </remarks>
[Authorize(Policy = SignalRAuthPolicy.PolicyName)]
public sealed class GatewayHub : Hub<IGatewayHubClient>
{
    private readonly IAgentSupervisor _supervisor;
    private readonly IAgentRegistry _registry;
    private readonly ISessionStore _sessions;
    private readonly IActivityBroadcaster _activity;
    private readonly IConversationRouter _conversationRouter;
    private readonly IConversationStore? _conversationStore;
    private readonly IAskUserResponseRegistry? _askUserResponseRegistry;
    private readonly IGatewayHubApplicationService _app;
    private readonly ILogger<GatewayHub> _logger;
    // Phase 2 (#568): optional so existing test hubs and any host that has not yet wired the
    // citizen registry keep working. When present, the connection lifecycle upserts the caller's
    // claims-derived User and attaches/detaches a (signalr, connectionId) ChannelIdentity so the
    // same UserId survives reconnects and conversation history stays keyed to a stable identity.
    private readonly IUserRegistry? _userRegistry;
    private readonly IWorldContext? _worldContext;

    public GatewayHub(
        IAgentSupervisor supervisor,
        IAgentRegistry registry,
        ISessionStore sessions,
        IActivityBroadcaster activity,
        IConversationRouter conversationRouter,
        IGatewayHubApplicationService app,
        ILogger<GatewayHub> logger,
        IConversationStore? conversationStore = null,
        IAskUserResponseRegistry? askUserResponseRegistry = null,
        IUserRegistry? userRegistry = null,
        IWorldContext? worldContext = null)
    {
        _supervisor = supervisor;
        _registry = registry;
        _sessions = sessions;
        _activity = activity;
        _conversationRouter = conversationRouter;
        _app = app;
        _logger = logger;
        _conversationStore = conversationStore;
        _askUserResponseRegistry = askUserResponseRegistry;
        _userRegistry = userRegistry;
        _worldContext = worldContext;
    }

    /// <summary>
    /// Subscribes the connection to every conversation it currently has access to, so it
    /// receives streaming and event payloads for any session in those conversations --
    /// including sessions that are created later (e.g. after compaction within the
    /// conversation). Conversation-keyed groups are stable across compaction; the
    /// previous session-keyed groups dropped post-compaction deliveries (#682).
    /// </summary>
    /// <returns>The available sessions at subscribe time, for UI initialisation.</returns>
    public async Task<SubscribeAllResult> SubscribeAll()
    {
        var sessions = await _app.GetAvailableSessionsAsync(Context.ConnectionAborted);

        // Distinct conversation group keys across the returned sessions. Each session
        // contributes:
        //   1) the real "conversation:{conversationId}" group (production routing key --
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
    {
        EnsureControlScope(nameof(SendMessage));
        return SendMessageCore(agentId, channelType, content, conversationId);
    }

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
        // GatewayHost.ProcessAsync downstream -- see #721.
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
        EnsureControlScope(nameof(RespondToAskUser));

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
        IReadOnlyList<MediaContentPartDto> contentParts,
        string? conversationId = null)
    {
        EnsureControlScope(nameof(SendMessageWithMedia));
        var typedAgentId = NormalizeAgentId(agentId);
        var typedChannelType = NormalizeChannelKey(channelType);
        if (string.IsNullOrWhiteSpace(content) && contentParts.Count == 0)
            throw new ArgumentException("A message must contain text or at least one attachment.", nameof(content));
        var normalizedContent = content ?? string.Empty;
        var normalizedConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId;
        var resolution = await ResolveOrCreateSessionAsync(typedAgentId, typedChannelType, normalizedConversationId);
        await SubscribeConversationInternalAsync(resolution.ConversationId);

        _logger.LogInformation(
            "Hub SendMessageWithMedia: agent={AgentId} channel={ChannelType} session={SessionId} parts={PartCount}",
            typedAgentId, typedChannelType, resolution.SessionId, contentParts.Count);

        var connectionId = Context.ConnectionId;
        var parts = contentParts.Select(ConvertToDomainContentPart).ToList();

        _ = SafeDispatchAsync(
            () => _app.AcceptAsync(
                BuildInboundMessage(
                    typedAgentId, connectionId, normalizedContent, "message-with-media",
                    new InboundMessageRoutingHints(
                        RequestedAgentId: typedAgentId,
                        RequestedSessionId: resolution.SessionId,
                        RequestedConversationId: normalizedConversationId is null ? null : ConversationId.From(normalizedConversationId)),
                    parts),
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
        => _app.AcceptAsync(
            BuildInboundMessage(
                typedAgentId, senderId, content, messageType,
                new InboundMessageRoutingHints(
                    RequestedAgentId: typedAgentId,
                    RequestedSessionId: typedSessionId,
                    RequestedConversationId: string.IsNullOrWhiteSpace(conversationId) ? null : ConversationId.From(conversationId))),
            CancellationToken.None);

    // Centralizes the channel-invariant InboundMessage fields shared by the SignalR hub dispatch
    // paths: signalr type, authenticated sender, stable per-agent address, and clientKind metadata.
    private InboundMessage BuildInboundMessage(
        AgentId typedAgentId, string senderId, string content, string messageType,
        InboundMessageRoutingHints routingHints, IReadOnlyList<MessageContentPart>? contentParts = null)
        => new()
        {
            ChannelType = ChannelKey.From("signalr"),
            SenderId = senderId,
            Sender = CitizenId.Of(UserId.From(GetAuthenticatedUserId())),
            ChannelAddress = ChannelAddress.From(typedAgentId.Value),
            RoutingHints = routingHints,
            Content = content,
            ContentParts = contentParts,
            Metadata = new Dictionary<string, object?>
            {
                ["messageType"] = messageType,
                ["clientKind"] = ResolveClientKind()
            }
        };

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
                await PublishActivityAsync(
                    agentId,
                    sessionId,
                    GatewayActivityType.Error,
                    CancellationToken.None,
                    message: $"Agent dispatch failed: {ex.Message}");
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
        EnsureControlScope(nameof(Steer));
        var ctx = ResolveCallContext(agentId, sessionId);
        var typedChannelType = ChannelKey.From("signalr");
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        // Subscribe so post-compaction streams continue to arrive. #682
        await SubscribeInternalAsync(ctx.SessionId);

        // Steering bypasses the per-session orchestrator queue entirely and injects
        // directly into the agent's PendingMessageQueue via handle.SteerAsync().
        //
        // Uses GetOrCreateAsync instead of GetHandle to eliminate the race window
        // between SendMessage (fire-and-forget to orchestrator) and Steer: the
        // orchestrator may not have finished routing/registering the handle yet.
        // GetOrCreateAsync waits for or creates the handle, avoiding a null lookup
        // that would trigger the old fallback dispatch (which caused "Agent is
        // already running" when the queued steer reached ProcessAsync on the same
        // handle that was already streaming).
        //
        // SteerAsync works regardless of IsRunning: if running, the steer is drained
        // at the next turn boundary; if idle, it's drained at the start of the next run.
        //
        // Dead-letter guard: a steer only makes sense against a RUNNING turn. Previously this
        // always called GetOrCreateAsync, which would conjure a fresh idle handle for a session
        // with no live run and "inject" a steer that never drains (the agent loop already ended,
        // so its PendingMessageQueue is never read again). Combined with a client-side session
        // mis-route, that silently swallowed the user's steer into an unrelated, idle session.
        //
        // First try GetHandle (no phantom creation). If there's no live handle, fall back to a
        // single GetOrCreateAsync to win the race against an in-flight SendMessage that may still
        // be routing/registering the handle (the original #-rationale for GetOrCreate). After that,
        // if the resolved handle still isn't running, treat it as a non-delivery rather than
        // dead-lettering the message.
        IAgentHandle? handle = _supervisor.GetHandle(ctx.AgentId, ctx.SessionId);
        if (handle is null || !handle.IsRunning)
        {
            try
            {
                handle = await _supervisor.GetOrCreateAsync(ctx.AgentId, ctx.SessionId, Context.ConnectionAborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve agent handle for steering: agent {AgentId} session {SessionId}",
                    ctx.AgentId.Value, ctx.SessionId.Value);

                await PublishActivityAsync(
                    ctx.AgentId,
                    ctx.SessionId,
                    GatewayActivityType.Error,
                    Context.ConnectionAborted,
                    conversationId: conversationId,
                    message: $"Steering failed: {ex.Message}");

                return new SendMessageResult(ctx.SessionId.Value, ctx.AgentId.Value, typedChannelType.Value);
            }
        }

        // If the agent isn't running, there is no in-flight turn to steer. Do NOT persist the
        // message into the session or inject it into an idle handle's queue (it would never drain).
        // Surface a clear, user-visible signal so the steer isn't silently lost.
        if (!handle.IsRunning)
        {
            await PublishActivityAsync(
                ctx.AgentId,
                ctx.SessionId,
                GatewayActivityType.Error,
                Context.ConnectionAborted,
                conversationId: conversationId,
                message: "Steering not applied: the agent isn't currently running in this conversation. Send the message instead to start a new turn.");

            _logger.LogInformation(
                "Steering skipped (agent not running) for agent {AgentId} session {SessionId}",
                ctx.AgentId.Value, ctx.SessionId.Value);

            return new SendMessageResult(ctx.SessionId.Value, ctx.AgentId.Value, typedChannelType.Value);
        }

        // Record steering message in session history
        var session = await _sessions.GetOrCreateAsync(ctx.SessionId, ctx.AgentId, Context.ConnectionAborted);
        session.AddEntry(new SessionEntry
        {
            Role = BotNexus.Domain.Primitives.MessageRole.User,
            Content = content
        });
        await _sessions.SaveAsync(session, Context.ConnectionAborted);

        // Inject into agent's steering queue
        await handle.SteerAsync(content, Context.ConnectionAborted);

        await PublishActivityAsync(
            ctx.AgentId,
            ctx.SessionId,
            GatewayActivityType.SteeringInjected,
            Context.ConnectionAborted,
            conversationId: conversationId);

        _logger.LogInformation(
            "Steering injected for agent {AgentId} session {SessionId} (running={IsRunning})",
            ctx.AgentId.Value, ctx.SessionId.Value, handle.IsRunning);

        return new SendMessageResult(ctx.SessionId.Value, ctx.AgentId.Value, typedChannelType.Value);
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
        EnsureControlScope(nameof(InterruptAndSteer));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var ctx = ResolveCallContext(agentId, sessionId);

        var handle = _supervisor.GetHandle(ctx.AgentId, ctx.SessionId);
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
        EnsureControlScope(nameof(FollowUp));
        var ctx = ResolveCallContext(agentId, sessionId);
        var connectionId = Context.ConnectionId;
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        _ = SafeDispatchAsync(
            async () =>
            {
                // Late-subscribe by conversation so stream events emitted by the
                // follow-up reach the connection even if SubscribeAll has not been
                // invoked recently. #682
                await SubscribeInternalAsync(ctx.SessionId);
                await DispatchMessageAsync(ctx.AgentId, ctx.SessionId, content, "message", connectionId);
            },
            ctx.AgentId,
            ctx.SessionId);
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
        EnsureControlScope(nameof(Abort));
        var ctx = ResolveCallContext(agentId, sessionId);
        var instance = _supervisor.GetInstance(ctx.AgentId, ctx.SessionId);
        if (instance is null)
            return;

        var handle = await _supervisor.GetOrCreateAsync(ctx.AgentId, ctx.SessionId, CancellationToken.None);
        await handle.AbortAsync(CancellationToken.None);
    }

    /// <summary>
    /// Resets the caller's active session: stops the agent, flushes session-end memory,
    /// cancels any pending ask-user prompts, seals the session, and clears the
    /// conversation's <see cref="Conversation.ActiveSessionId"/> so the next inbound
    /// message creates a fresh session inside the same conversation.
    /// </summary>
    /// <remarks>
    /// Delegates to the conversation-reset operation on <see cref="IGatewayHubApplicationService"/>
    /// when a conversation is bound, guarding against stale <paramref name="sessionId"/> values by
    /// passing it as the expected active session. Sessions without a conversation (legacy/orphans)
    /// are sealed in place -- no transcript-destroying <see cref="ISessionStore.ArchiveAsync"/>.
    /// </remarks>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id known to the caller.</param>
    /// <returns>A task that completes when the caller has been notified.</returns>
    public async Task ResetSession(AgentId agentId, SessionId sessionId)
    {
        EnsureControlScope(nameof(ResetSession));
        var ctx = ResolveCallContext(agentId, sessionId);

        var gatewaySession = await _sessions.GetAsync(ctx.SessionId, CancellationToken.None);

        var resetViaService = gatewaySession is not null
            && gatewaySession.ConversationId.IsInitialized()
            && await _app.TryResetActiveSessionAsync(
                gatewaySession.ConversationId,
                expectedActiveSessionId: ctx.SessionId,
                CancellationToken.None);

        if (!resetViaService && gatewaySession is not null)
        {
            // Orphan/legacy session with no conversation link (or no reset service configured):
            // stop the agent and seal the session in place (Status=Sealed + SaveAsync).
            // Deliberately avoids ArchiveAsync because the InMemory implementation deletes the
            // row and the file store renames files out of normal lookup -- both destroy transcript
            // readability for any UI that lists historical sessions.
            try
            {
                await _supervisor.StopAsync(ctx.AgentId, ctx.SessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Supervisor stop failed for orphan session {SessionId}; reset will proceed.", ctx.SessionId);
            }

            gatewaySession.Status = GatewaySessionStatus.Sealed;
            gatewaySession.UpdatedAt = DateTimeOffset.UtcNow;
            await _sessions.SaveAsync(gatewaySession, CancellationToken.None);
        }

        await Clients.Caller.SessionReset(new SessionResetPayload(
            ctx.AgentId.Value,
            ctx.SessionId.Value,
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
        EnsureControlScope(nameof(CompactSession));
        var ctx = ResolveCallContext(agentId, sessionId);
        var session = await _sessions.GetAsync(ctx.SessionId, CancellationToken.None);
        if (session is null)
            throw new HubException($"Session '{ctx.SessionId.Value}' not found.");

        // Resolve through the request services so test hosts can substitute the
        // coordinator. Falls back to the application-service facade composed at hub
        // construction when the request scope does not override it.
        var requestServices = Context.GetHttpContext()?.RequestServices;
        var coordinator = requestServices?.GetService<ISessionCompactionCoordinator>();

        var outcome = coordinator is not null
            ? await coordinator.CompactAsync(ctx.AgentId, session, CancellationToken.None, force: true)
            : await _app.CompactAsync(ctx.AgentId, session, CancellationToken.None, force: true);

        return new CompactSessionResult(
            outcome.Succeeded && outcome.Applied,
            outcome.EntriesSummarized,
            outcome.EntriesPreserved,
            outcome.TokensBefore,
            outcome.TokensAfter,
            outcome.FailureReason);
    }

    /// <summary>
    /// Executes get agents.
    /// </summary>
    /// <returns>The get agents result.</returns>
    public Task<IReadOnlyList<AgentDescriptor>> GetAgents()
        => Task.FromResult(SelectableAgents());

    /// <summary>
    /// Returns the registry descriptors that are appropriate to surface to portal clients:
    /// user-facing top-level agents only. Runtime-spawned sub-agents
    /// (<see cref="AgentKind.SubAgent"/>) and built-in platform archetypes
    /// (researcher/coder/planner/reviewer/writer/analyst) are excluded because they are
    /// spawn/converse infrastructure, not selectable agents. Mirrors the default filtering in
    /// <c>AgentsController.List</c> so the SignalR and REST surfaces agree (#1940 follow-up).
    /// </summary>
    private IReadOnlyList<AgentDescriptor> SelectableAgents()
        => _registry.GetAll()
            .Where(a => a.Kind != AgentKind.SubAgent && !a.IsBuiltIn)
            .ToList();

    /// <summary>
    /// Lightweight liveness probe: a no-op server round-trip used by clients to verify the
    /// hub connection is actually alive rather than a zombie socket (#1838). iOS silently
    /// recycles background WebSockets, leaving the client-side state reporting Connected on a
    /// dead socket; a short-timeout Ping that completes proves the transport is live end-to-end.
    /// Returns the server UTC ticks so the payload is non-trivial and cannot be short-circuited.
    /// </summary>
    /// <returns>The server''s current UTC tick count at the time the ping was handled.</returns>
    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>
    /// Executes get agent status.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The get agent status result.</returns>
    public AgentInstance? GetAgentStatus(AgentId agentId, SessionId sessionId)
    {
        EnsureReadScope(nameof(GetAgentStatus));
        var ctx = ResolveCallContext(agentId, sessionId);
        return _supervisor.GetInstance(ctx.AgentId, ctx.SessionId);
    }

    /// <summary>
    /// Executes on connected async.
    /// </summary>
    /// <returns>The on connected async result.</returns>
    public override async Task OnConnectedAsync()
    {
        var clientVersion = Context.GetHttpContext()?.Request.Query["clientVersion"].FirstOrDefault() ?? "unknown";
        // Read the connect-time client-kind hint and stash it for the lifetime of the
        // connection so the per-message dispatch path can stamp it onto InboundMessage.Metadata
        // without re-reading the query each time (#1209). Absent hint -> "desktop" (AC#5).
        var clientKind = ResolveClientKind();
        Context.Items["clientKind"] = clientKind;
        // Both clientVersion and clientKind originate from the connect-time query string and are
        // fully attacker-controlled. Sanitize them before logging so an embedded CR/LF (or other
        // control character) cannot forge additional log lines (CodeQL cs/log-forging).
        _logger.LogInformation("Hub OnConnected: connection={ConnectionId} clientVersion={ClientVersion} clientKind={ClientKind}",
            Context.ConnectionId, SanitizeForLog(clientVersion), SanitizeForLog(clientKind));

        await Clients.Caller.Connected(new ConnectedPayload(
            Context.ConnectionId,
            SelectableAgents().Select(a => new AgentSummary(a.AgentId.Value, a.DisplayName, a.Emoji, a.Description)),
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

        // Phase 2 (#568): bind the resolved claims-based identity into the user registry so the
        // same UserId is reused across reconnects (conversation history stays keyed to it) and a
        // (signalr, connectionId) ChannelIdentity is attached for this live connection.
        AttachChannelIdentity();

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

        // Phase 2 (#568): detach the (signalr, connectionId) ChannelIdentity for this connection.
        // The User record itself is retained in the registry so its stable UserId (and the
        // conversation history keyed to it) survives the disconnect/reconnect cycle.
        DetachChannelIdentity();

        await base.OnDisconnectedAsync(exception);
    }

    // GetSessionGroup is gone -- the hub now subscribes/unsubscribes via SignalRChannelAdapter.GetConversationGroup.
    // Removing this prevents future regressions back to session-keyed routing (#682).

    /// <summary>
    /// Resolves the caller-supplied agent and session ids into a single normalized
    /// <see cref="HubCallContext"/> so every client-callable control method runs the same
    /// id-normalisation and reserved-namespace guard once, rather than repeating the
    /// <see cref="NormalizeAgentId"/> / <see cref="NormalizeClientSessionId"/> pair inline.
    /// </summary>
    /// <summary>
    /// Enforces that the calling connection carries the write/control scope before a
    /// mutation method runs. Delegates to <see cref="HubScopeGuard"/>, which no-ops for
    /// legacy connections that present no scope claims (backward compatible) and throws a
    /// <see cref="HubException"/> for a scope-restricted connection (e.g. read-only).
    /// </summary>
    private void EnsureControlScope(string methodName)
        => HubScopeGuard.EnsureScope(Context.User, HubScope.Control, methodName);

    /// <summary>
    /// Enforces that the calling connection carries at least read scope before a passive
    /// inspection method runs. Control scope satisfies this too.
    /// </summary>
    private void EnsureReadScope(string methodName)
        => HubScopeGuard.EnsureScope(Context.User, HubScope.Read, methodName);

    private static HubCallContext ResolveCallContext(AgentId agentId, SessionId sessionId)
        => new(NormalizeAgentId(agentId), NormalizeClientSessionId(sessionId));

    /// <summary>
    /// Publishes a control-path <see cref="GatewayActivity"/> for the given call context through
    /// the single activity path so the envelope shape (agent id, session id, optional conversation
    /// id and message) is constructed in one place. Callers decide <em>whether</em> to publish;
    /// this centralises only the construction and dispatch.
    /// </summary>
    private ValueTask PublishActivityAsync(
        AgentId agentId,
        SessionId sessionId,
        GatewayActivityType type,
        CancellationToken cancellationToken,
        string? conversationId = null,
        string? message = null)
        => _activity.PublishAsync(
            new GatewayActivity
            {
                Type = type,
                AgentId = agentId.Value,
                SessionId = sessionId.Value,
                ConversationId = conversationId,
                Message = message
            },
            cancellationToken);

    /// <summary>
    /// Normalized caller ids shared by the control methods. Produced once per call by
    /// <see cref="ResolveCallContext"/> after the reserved-namespace guard has run.
    /// </summary>
    private readonly record struct HubCallContext(AgentId AgentId, SessionId SessionId);

    // AgentId is a Vogen value object; the parameter cannot be default(AgentId) (compile-time
    // VOG009) and is already validated and trimmed at construction. The defensive re-construction
    // step is therefore a pass-through. SessionId and ChannelKey are still hand-rolled and need
    // the try/catch below until they migrate to Vogen.
    private static AgentId NormalizeAgentId(AgentId agentId) => agentId;

    /// <summary>
    /// Resolves the authenticated user identifier from claims. The <see cref="ClaimsUserIdProvider"/>
    /// populates <see cref="HubCallerContext.UserIdentifier"/> from the <c>oid</c> or <c>sub</c>
    /// claim. When no authenticated identity is available (should not happen with [Authorize]),
    /// falls back to <see cref="HubCallerContext.ConnectionId"/> for backward compatibility
    /// during the transition period where auth may be configured but not enforced.
    /// </summary>
    private string GetAuthenticatedUserId()
        => Context.UserIdentifier ?? Context.ConnectionId;

    // The channel key for SignalR-originated identities. Matches the ChannelKey stamped on
    // inbound messages and channel bindings so the User's ChannelIdentities line up with routing.
    private static readonly ChannelKey SignalRChannel = ChannelKey.From("signalr");

    /// <summary>
    /// Registers or upserts the caller's <see cref="User"/> from the resolved claims identity and
    /// attaches a <see cref="ChannelIdentity"/> of <c>(signalr, connectionId)</c> to it (#568).
    /// Idempotent: a reconnect re-registers the same <see cref="UserId"/> (so conversation history
    /// keyed to that id is preserved) and simply refreshes the connection's channel identity. No-op
    /// when the citizen registry / world context are not wired (legacy hosts and unit-test hubs).
    /// </summary>
    private void AttachChannelIdentity()
    {
        if (_userRegistry is null || _worldContext is null)
            return;

        // The claims-derived user id is the stable identity that must survive reconnects. When no
        // authenticated identity is present it falls back to the connection id (transition period),
        // which is not stable across reconnects but keeps the lifecycle coherent.
        UserId userId;
        try
        {
            userId = UserId.From(GetAuthenticatedUserId());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            _logger.LogWarning(ex, "Hub OnConnected: could not resolve a valid user id for connection {ConnectionId}; skipping channel-identity attach", Context.ConnectionId);
            return;
        }

        var connectionIdentity = new ChannelIdentity(SignalRChannel, ChannelAddress.From(Context.ConnectionId));

        try
        {
            var existing = _userRegistry.Get(userId);
            if (existing is null)
            {
                var user = new User
                {
                    Id = userId,
                    DisplayName = userId.Value,
                    World = _worldContext.Current,
                    ChannelIdentities = [connectionIdentity]
                };
                _userRegistry.Register(user);
            }
            else
            {
                // Reconnect (or a second connection): keep the same UserId and merge the new
                // connection's identity in. De-dupe on the exact (channel, address) pair.
                if (!existing.ChannelIdentities.Contains(connectionIdentity))
                {
                    var merged = new User
                    {
                        Id = existing.Id,
                        DisplayName = existing.DisplayName,
                        World = existing.World,
                        ChannelIdentities = [.. existing.ChannelIdentities, connectionIdentity]
                    };
                    _userRegistry.Update(existing.Id, merged);
                }
            }
        }
        catch (Exception ex)
        {
            // Identity binding is best-effort: a registry hiccup must not fail the connection.
            _logger.LogWarning(ex, "Hub OnConnected: failed to attach channel identity for connection {ConnectionId}", Context.ConnectionId);
        }
    }

    /// <summary>
    /// Detaches the <c>(signalr, connectionId)</c> <see cref="ChannelIdentity"/> for this connection
    /// on disconnect (#568). The <see cref="User"/> record and its stable <see cref="UserId"/> are
    /// retained so conversation history and identity survive the reconnect. No-op when the registry
    /// is not wired.
    /// </summary>
    private void DetachChannelIdentity()
    {
        if (_userRegistry is null)
            return;

        UserId userId;
        try
        {
            userId = UserId.From(GetAuthenticatedUserId());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return;
        }

        var connectionIdentity = new ChannelIdentity(SignalRChannel, ChannelAddress.From(Context.ConnectionId));

        try
        {
            var existing = _userRegistry.Get(userId);
            if (existing is null || !existing.ChannelIdentities.Contains(connectionIdentity))
                return;

            var remaining = existing.ChannelIdentities.Where(ci => ci != connectionIdentity).ToArray();
            var updated = new User
            {
                Id = existing.Id,
                DisplayName = existing.DisplayName,
                World = existing.World,
                ChannelIdentities = remaining
            };
            _userRegistry.Update(existing.Id, updated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hub OnDisconnected: failed to detach channel identity for connection {ConnectionId}", Context.ConnectionId);
        }
    }
    /// <summary>
    /// Resolves the connecting client kind ("mobile" vs "desktop") for this connection (#1209).
    /// Reads the value cached in <see cref="HubCallerContext.Items"/> by
    /// <see cref="OnConnectedAsync"/> first, then falls back to re-reading the connect-time
    /// <c>client</c> query parameter so dispatch still works if the cache was not populated.
    /// An absent or blank hint normalizes to "desktop" so existing desktop clients that send no
    /// hint keep working (backward compatible -- AC#5). The value is lower-cased for a stable key.
    /// </summary>
    /// <returns>The normalized client kind; never null or empty.</returns>
    private string ResolveClientKind()
    {
        if (Context.Items.TryGetValue("clientKind", out var cached)
            && cached is string cachedKind
            && !string.IsNullOrWhiteSpace(cachedKind))
        {
            return cachedKind;
        }

        var raw = Context.GetHttpContext()?.Request.Query["client"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(raw) ? "desktop" : raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Strips CR, LF and other control characters from a value before it is written to a log so
    /// that attacker-controlled connect-time query values (client kind, client version) cannot
    /// inject newlines and forge additional log lines (CodeQL cs/log-forging). Control characters
    /// are replaced with a single space and the result is trimmed; an empty/whitespace result
    /// collapses to a stable placeholder so the log line keeps its shape.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            chars[i] = char.IsControl(value[i]) ? ' ' : value[i];
        }

        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "(empty)" : sanitized;
    }

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

    /// <summary>
    /// Normalizes a caller-supplied session id and rejects any that targets a reserved internal
    /// session namespace (sub-agent / cron). External clients address their own portal sessions;
    /// they must not be able to steer, abort, compact, reset, or inspect a gateway-internal
    /// session by hand-crafting its id. Throws <see cref="HubException"/> for a reserved target
    /// instead of silently routing/creating a phantom internal session.
    /// </summary>
    private static SessionId NormalizeClientSessionId(SessionId sessionId)
    {
        var normalized = NormalizeSessionId(sessionId);
        if (normalized.IsReservedInternalNamespace)
            throw new HubException("Session ID targets a reserved internal namespace and cannot be addressed by a client.");

        return normalized;
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
    /// delegating to the shared conversation-resolution operation on
    /// <see cref="IGatewayHubApplicationService"/>. Returns a lightweight
    /// <see cref="HubInboundResolution"/> that carries just the IDs the hub needs to
    /// (a) populate the synchronous <see cref="SendMessageResult"/> contract and
    /// (b) join the caller connection to the conversation group before the orchestrator's
    /// background worker emits stream events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a pure resolution call. Session row materialisation, ChannelType / SessionType
    /// stamping, Expired->Active reactivation, transcript SaveAsync, and caller participant
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
                Sender = CitizenId.Of(UserId.From(GetAuthenticatedUserId())),
                Content = string.Empty,
                // RoutingHints intentionally omitted -- the outer InboundMessageContext below
                // carries the typed RequestedAgentId + RequestedConversationId explicitly.
            },
            new ChannelSource(
                channelType,
                channelAddress,
                Context.ConnectionId),
            RequestedConversationId: typedConversationId,
            RequestedAgentId: agentId);

        var dispatchResult = await _app.ResolveSessionAsync(context, Context.ConnectionAborted);
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
    /// WebSocket client disconnecting before (or during) the SignalR handshake -- e.g.
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
