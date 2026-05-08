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

    public bool IsReady { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LoadError { get; private set; }
    public event Action? OnReadyChanged;

    public PortalLoadService(
        IGatewayRestClient restClient,
        GatewayHubConnection hub,
        IClientStateStore store,
        IGatewayEventHandler eventHandler)
    {
        _restClient = restClient;
        _hub = hub;
        _store = store;
        _eventHandler = eventHandler;
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
                    IsConnected = true
                });
            }

            var conversationTasks = agents.Select(async agent =>
            {
                var conversations = await _restClient.GetConversationsAsync(agent.AgentId, cancellationToken);
                _store.SeedConversations(agent.AgentId, conversations);
            });
            await Task.WhenAll(conversationTasks);

            var selectedAgentId = agents.OrderBy(a => a.DisplayName).FirstOrDefault()?.AgentId;
            if (selectedAgentId is not null)
            {
                _store.ActiveAgentId = selectedAgentId;

                var selectedAgent = _store.GetAgent(selectedAgentId);
                var selectedConversation = selectedAgent?.Conversations.Values
                    .OrderByDescending(c => c.IsDefault)
                    .ThenByDescending(c => c.UpdatedAt)
                    .FirstOrDefault();

                if (selectedConversation is not null)
                {
                    _store.SetActiveConversation(selectedAgentId, selectedConversation.ConversationId);

                    const int historyLimit = 200;
                    var history = await _restClient.GetHistoryAsync(selectedConversation.ConversationId, historyLimit, 0, cancellationToken);

                    if (history?.Entries is { Count: > 0 })
                    {
                        foreach (var entry in history.Entries)
                        {
                            if (entry.Kind == "boundary")
                            {
                                selectedConversation.Messages.Add(new ChatMessage("System", string.Empty, entry.Timestamp)
                                {
                                    Kind = "boundary",
                                    BoundaryLabel = $"Session · {entry.Timestamp.ToLocalTime():MMM d HH:mm} · {entry.SessionId}",
                                    BoundarySessionId = entry.SessionId
                                });
                            }
                            else
                            {
                                var isTool = entry.ToolName is not null;
                                selectedConversation.Messages.Add(new ChatMessage(MapRole(entry.Role ?? "system"), entry.Content ?? string.Empty, entry.Timestamp)
                                {
                                    ToolName = entry.ToolName,
                                    ToolCallId = entry.ToolCallId,
                                    ToolArgs = entry.ToolArgs,
                                    ToolIsError = entry.ToolIsError,
                                    ToolResult = isTool ? entry.Content : null,
                                    IsToolCall = isTool
                                });
                            }
                        }

                        selectedConversation.HistoryLoaded = true;
                    }
                }
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

    private static string MapRole(string role) => role.ToLowerInvariant() switch
    {
        "user" => "User",
        "assistant" => "Assistant",
        "tool" => "Tool",
        "error" => "Error",
        "system" => "System",
        _ => role
    };
}
