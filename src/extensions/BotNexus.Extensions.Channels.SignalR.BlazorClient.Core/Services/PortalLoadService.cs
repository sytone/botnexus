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

    public bool IsReady { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LoadError { get; private set; }
    public event Action? OnReadyChanged;

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
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType);

            foreach (var group in sessions
                         .Where(s => string.Equals(s.SessionType, "cron", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(s => s.AgentId))
            {
                var agent = _store.GetAgent(group.Key);
                if (agent is null) continue;

                foreach (var session in group)
                {
                    var conversationId = $"cron-session:{session.SessionId}";
                    agent.Conversations[conversationId] = new ConversationState
                    {
                        ConversationId = conversationId,
                        Title = $"Cron · {session.SessionId[..Math.Min(8, session.SessionId.Length)]}",
                        Status = session.Status ?? "Active",
                        ActiveSessionId = session.SessionId,
                        CreatedAt = session.CreatedAt ?? DateTimeOffset.UtcNow,
                        UpdatedAt = session.UpdatedAt ?? DateTimeOffset.UtcNow,
                        IsVirtualSession = true,
                        VirtualSessionKind = "cron"
                    };
                }
            }

            foreach (var agentSummary in agents)
            {
                var agent = _store.GetAgent(agentSummary.AgentId);
                if (agent is not null)
                    ReconcileVirtualCronConversations(agent, sessions);
            }

            var selectedAgentId = agents.OrderBy(a => a.DisplayName).FirstOrDefault()?.AgentId;
            if (selectedAgentId is not null)
            {
                _store.ActiveAgentId = selectedAgentId;

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
            await _hub.ConnectAsync(hubUrl);

            var subscribeResult = await _hub.SubscribeAllAsync();
            foreach (var session in subscribeResult.Sessions)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType);

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
        const int historyLimit = 200;

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
                        conversation.Messages.Add(new ChatMessage(role, entry.Content ?? string.Empty, entry.Timestamp)
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
            if (history?.Entries is { Count: > 0 })
            {
                foreach (var entry in history.Entries)
                {
                    if (entry.Kind == "boundary")
                    {
                        conversation.Messages.Add(new ChatMessage("System", string.Empty, entry.Timestamp)
                        {
                            Kind = "boundary",
                            BoundaryLabel = $"Session · {entry.Timestamp.ToLocalTime():MMM d HH:mm} · {entry.SessionId}",
                            BoundarySessionId = entry.SessionId
                        });
                    }
                    else
                    {
                        var isTool = entry.ToolName is not null;
                        conversation.Messages.Add(new ChatMessage(MapRole(entry.Role ?? "system"), entry.Content ?? string.Empty, entry.Timestamp)
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

                conversation.HistoryLoaded = true;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Stale virtual cron projection — the backing session/conversation no longer exists.
            // Remove the orphaned projection so it doesn't block other conversations.
            if (IsVirtualCronConversation(conversation))
            {
                agent.Conversations.Remove(conversation.ConversationId);
                Console.Error.WriteLine(
                    $"[PortalLoadService] Removed stale virtual cron projection '{conversation.ConversationId}' (404 from gateway).");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[PortalLoadService] History 404 for conversation '{conversation.ConversationId}': {ex.Message}");
            }
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

    private static bool IsVirtualCronConversation(ConversationState conversation)
        => (conversation.IsVirtualSession &&
            string.Equals(conversation.VirtualSessionKind, "cron", StringComparison.OrdinalIgnoreCase))
           || conversation.ConversationId.StartsWith("cron-session:", StringComparison.Ordinal);

    private static void ReconcileVirtualCronConversations(AgentState agent, IReadOnlyList<SessionSummary> sessions)
    {
        var activeCronConversations = sessions
            .Where(s => string.Equals(s.AgentId, agent.AgentId, StringComparison.Ordinal) &&
                        string.Equals(s.SessionType, "cron", StringComparison.OrdinalIgnoreCase))
            .Select(s => $"cron-session:{s.SessionId}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var staleConversationId in agent.Conversations
                     .Where(kv => kv.Value.IsVirtualSession &&
                                  string.Equals(kv.Value.VirtualSessionKind, "cron", StringComparison.OrdinalIgnoreCase) &&
                                  !activeCronConversations.Contains(kv.Key))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            agent.Conversations.Remove(staleConversationId);
        }
    }
}
