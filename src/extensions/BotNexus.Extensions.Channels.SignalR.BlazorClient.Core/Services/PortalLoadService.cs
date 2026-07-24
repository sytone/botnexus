namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Owns the portal startup sequence: REST first, SignalR second.
/// </summary>
public sealed class PortalLoadService : IPortalLoadService
{
    private readonly IGatewayRestClient _restClient;
    private readonly GatewayHubConnection _hub;
    private readonly IClientStateStore _store;
    private readonly IGatewayEventHandler _eventHandler;
    private readonly IAgentInteractionService? _agentInteraction;

    // Serialises app-resume resets and enforces the liveness-probe-then-rebuild algorithm (#1838).
    private readonly HubResumeCoordinator _resumeCoordinator = new();

    private string? _hubUrl;

    public bool IsReady { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LoadError { get; private set; }
    public bool IsSignalRConnected => _hub.IsConnected;

    /// <summary>
    /// The connecting client kind ("desktop" or "mobile") forwarded to the hub connection as a
    /// <c>client</c> query parameter so the gateway can distinguish device classes per SignalR
    /// connection (#1209). Set by the per-app caller before <see cref="InitializeAsync"/>;
    /// defaults to "desktop" so the historical desktop-portal path is unchanged (AC#5).
    /// </summary>
    public string ClientKind { get; set; } = "desktop";

    /// <summary>
    /// Optional per-connection keep-alive/timeout and reconnect tuning forwarded to every hub
    /// build (initial connect, refresh reconnect, and resume rebuild) (#1840). Null on desktop so
    /// the framework defaults are preserved; the mobile client sets a populated instance before
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public HubConnectionTuning? Tuning { get; set; }
    public event Action? OnReadyChanged;
    public event Action? OnConnectionStateChanged;

    public PortalLoadService(
        IGatewayRestClient restClient,
        GatewayHubConnection hub,
        IClientStateStore store,
        IGatewayEventHandler eventHandler,
        IAgentInteractionService? agentInteraction = null)
    {
        _restClient = restClient;
        _hub = hub;
        _store = store;
        _eventHandler = eventHandler;
        _agentInteraction = agentInteraction;  // optional — null when not wired (e.g. in tests)
    }

    public async Task InitializeAsync(string hubUrl, CancellationToken cancellationToken = default)
    {
        if (IsReady || IsLoading)
            return;

        _hubUrl = hubUrl;
        IsLoading = true;
        LoadError = null;
        OnReadyChanged?.Invoke();

        try
        {
            var apiBaseUrl = new Uri(new Uri(hubUrl), "/api/").ToString();
            _restClient.Configure(apiBaseUrl);

            var agents = await _restClient.GetAgentsAsync(cancellationToken);
            foreach (var agent in agents)
            {
                _store.UpsertAgent(new AgentState
                {
                    AgentId = agent.AgentId,
                    DisplayName = agent.DisplayName,
                    Emoji = agent.Emoji,
                    Description = agent.Description,
                    IsBuiltIn = agent.IsBuiltIn,
                    IsConnected = true
                });
            }

            var conversationTasks = agents.Select(async agent =>
            {
                var conversations = await _restClient.GetConversationsAsync(agent.AgentId, cancellationToken);
                _store.SeedConversations(agent.AgentId, conversations);
            });
            await Task.WhenAll(conversationTasks);

            var sessions = await _restClient.GetSessionsAsync(cancellationToken: cancellationToken);
            foreach (var session in sessions)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);

            var selectedAgentId = agents.OrderBy(a => a.DisplayName).FirstOrDefault()?.AgentId;
            if (selectedAgentId is not null)
            {
                _store.SelectView(selectedAgentId, string.Empty, SelectionSource.Bootstrap);

                var selectedAgent = _store.GetAgent(selectedAgentId);
                while (selectedAgent is not null)
                {
                    var selectedConversation = selectedAgent.Conversations.Values
                        .OrderByDescending(c => c.IsDefault)
                        .ThenByDescending(c => c.UpdatedAt)
                        .FirstOrDefault();

                    if (selectedConversation is null)
                        break;

                    _store.SetActiveConversation(selectedAgentId, selectedConversation.ConversationId);
                    await LoadInitialHistoryAsync(selectedAgent!, selectedConversation, cancellationToken);

                    if (selectedAgent.Conversations.ContainsKey(selectedConversation.ConversationId))
                        break;
                }
            }

            // Wire conversation-refresh delegate so ConversationChanged SignalR events
            // trigger a REST re-fetch of the conversation list for the affected agent.
            if (_eventHandler is GatewayEventHandler concreteHandler)
            {
                var agentInteractionRef = _agentInteraction;
                if (agentInteractionRef is not null)
                    concreteHandler.ConversationRefreshDelegate = agentId => agentInteractionRef.RefreshConversationsAsync(agentId);
            }
            await _hub.ConnectAsync(hubUrl, ClientKind, Tuning);

            // Track SignalR connection state for UI indicators and reconnect flows.
            _hub.OnReconnecting += () => OnConnectionStateChanged?.Invoke();
            _hub.OnReconnected += () => OnConnectionStateChanged?.Invoke();
            _hub.OnDisconnected += () => OnConnectionStateChanged?.Invoke();

            var subscribeResult = await _hub.SubscribeAllAsync();
            foreach (var session in subscribeResult.Sessions)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);

            _ = _eventHandler; // force construction so hub event subscriptions are active

            IsReady = true;
            IsLoading = false;
            _store.NotifyChanged();
            OnReadyChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LoadError = $"Portal failed to load: {ex.Message}";
            IsLoading = false;
            OnReadyChanged?.Invoke();
            Console.Error.WriteLine($"PortalLoadService.InitializeAsync failed: {ex}");
        }
    }

    /// <summary>
    /// Loads history for the initially selected conversation. Virtual cron sessions
    /// use the session history endpoint; real conversations use conversation history.
    /// A 404 from a stale/deleted cron projection is handled gracefully — the projection
    /// is removed and initialization continues.
    /// </summary>
    private async Task LoadInitialHistoryAsync(AgentState agent, ConversationState conversation, CancellationToken cancellationToken)
    {
        const int historyLimit = AgentInteractionService.DefaultHistoryPageSize;

        try
        {
            if (conversation.IsVirtualSession && conversation.ActiveSessionId is { Length: > 0 } sessionId)
            {
                var sessionHistory = await _restClient.GetSessionHistoryAsync(sessionId, historyLimit, 0, cancellationToken);
                if (sessionHistory?.Entries is { Count: > 0 })
                {
                    foreach (var entry in sessionHistory.Entries)
                    {
                        var role = MapRole(entry.Role ?? "system");
                        conversation.AppendMessage(new ChatMessage(role, entry.Content ?? string.Empty, entry.Timestamp)
                        {
                            ToolName = entry.ToolName,
                            ToolCallId = entry.ToolCallId,
                            ToolArgs = entry.ToolArgs,
                            ToolIsError = entry.ToolIsError,
                            IsToolCall = entry.ToolName is not null,
                            ToolResult = entry.ToolName is not null ? AnsiStripper.Strip(entry.Content) : null
                        });
                    }
                }

                conversation.HistoryLoaded = true;
                return;
            }

            var history = await _restClient.GetHistoryAsync(conversation.ConversationId, historyLimit, 0, cancellationToken);
            conversation.LoadedHistoryRows = 0;
            conversation.HasMoreHistory = false;
            if (history?.Entries is { Count: > 0 } historyEntries)
            {
                foreach (var entry in historyEntries)
                {
                    if (entry.Kind == "boundary")
                    {
                        conversation.AppendMessage(new ChatMessage("System", string.Empty, entry.Timestamp)
                        {
                            Kind = "boundary",
                            BoundaryLabel = $"Session · {entry.Timestamp.ToLocalTime():MMM d HH:mm} · {entry.SessionId}",
                            BoundarySessionId = entry.SessionId
                        });
                    }
                    else
                    {
                        var isTool = entry.ToolName is not null;
                        conversation.AppendMessage(new ChatMessage(MapRole(entry.Role ?? "system"), entry.Content ?? string.Empty, entry.Timestamp)
                        {
                            ToolName = entry.ToolName,
                            ToolCallId = entry.ToolCallId,
                            ToolArgs = entry.ToolArgs,
                            ToolIsError = entry.ToolIsError,
                            ToolResult = isTool ? AnsiStripper.Strip(entry.Content) : null,
                            IsToolCall = isTool
                        });
                    }
                }

                // Open on the most-recent 20 and allow scroll-up paging when a full page came back (#1691).
                conversation.LoadedHistoryRows = historyEntries.Count;
                conversation.HasMoreHistory = historyEntries.Count >= historyLimit;
                conversation.HistoryLoaded = true;
            }
            else
            {
                conversation.HistoryLoaded = true;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Conversation no longer exists on the backend (e.g. archived/deleted concurrently).
            Console.Error.WriteLine(
                $"[PortalLoadService] History 404 for conversation '{conversation.ConversationId}': {ex.Message}");
        }
    }

    private static string MapRole(string role) => role.ToLowerInvariant() switch
    {
        "user" => "User",
        "assistant" => "Assistant",
        "tool" => "Tool",
        "error" => "Error",
        "system" => "System",
        _ => role
    };

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_hubUrl is null || IsLoading)
            return;

        try
        {
            // Re-fetch agent and conversation lists from REST
            var agents = await _restClient.GetAgentsAsync(cancellationToken);
            foreach (var agent in agents)
            {
                _store.UpsertAgent(new AgentState
                {
                    AgentId = agent.AgentId,
                    DisplayName = agent.DisplayName,
                    Emoji = agent.Emoji,
                    Description = agent.Description,
                    IsBuiltIn = agent.IsBuiltIn,
                    IsConnected = true
                });
            }

            var conversationTasks = agents.Select(async agent =>
            {
                var conversations = await _restClient.GetConversationsAsync(agent.AgentId, cancellationToken);
                _store.SeedConversations(agent.AgentId, conversations);
            });
            await Task.WhenAll(conversationTasks);

            // Reconnect SignalR if needed
            if (!_hub.IsConnected)
            {
                await _hub.ConnectAsync(_hubUrl, ClientKind, Tuning);
                var subscribeResult = await _hub.SubscribeAllAsync();
                foreach (var session in subscribeResult.Sessions)
                    _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);
            }

            _store.NotifyChanged();
            OnConnectionStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PortalLoadService] RefreshAsync failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<HubResumeOutcome> ResumeAsync(CancellationToken cancellationToken = default)
    {
        // Nothing to reset before the initial load has established a hub URL.
        if (_hubUrl is null)
            return HubResumeOutcome.Skipped;

        return await _resumeCoordinator.ResumeAsync(
            probe: token => _hub.ProbeAsync(token),
            rebuild: RebuildConnectionAsync,
            refresh: () => RefreshAsync(cancellationToken));
    }

    /// <summary>
    /// Tears down the (zombie) hub connection and rebuilds it through the shared
    /// <see cref="GatewayHubConnection.ConnectAsync"/> path -- fresh negotiate, handler
    /// re-registration -- then re-subscribes so the connection resumes receiving streaming and
    /// conversation events. Hub construction logic is not forked: the same ConnectAsync used at
    /// initial load rebuilds here (#1838).
    /// </summary>
    private async Task RebuildConnectionAsync()
    {
        await _hub.StopAndDisposeAsync();
        await _hub.ConnectAsync(_hubUrl!, ClientKind, Tuning);

        // Re-wire connection-state notifications on the fresh connection so the UI keeps
        // reflecting reconnecting/reconnected/disconnected transitions after the reset.
        _hub.OnReconnecting += () => OnConnectionStateChanged?.Invoke();
        _hub.OnReconnected += () => OnConnectionStateChanged?.Invoke();
        _hub.OnDisconnected += () => OnConnectionStateChanged?.Invoke();

        var subscribeResult = await _hub.SubscribeAllAsync();
        foreach (var session in subscribeResult.Sessions)
            _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);

        OnConnectionStateChanged?.Invoke();
    }
}
