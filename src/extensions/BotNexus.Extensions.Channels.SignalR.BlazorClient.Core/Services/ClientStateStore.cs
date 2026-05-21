namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Scoped in-memory state store for the portal. Single source of truth for all agent,
/// conversation, and message state. UI components subscribe to <see cref="OnChanged"/> to re-render.
/// </summary>
public sealed class ClientStateStore : IClientStateStore
{
    private readonly Dictionary<string, AgentState> _agents = new();
    private readonly Dictionary<string, string> _sessionToAgent = new(); // sessionId → agentId
    private readonly Dictionary<string, AskUserPromptState> _pendingAskUserByConversation = new();

    // ── IClientStateStore ────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action? OnChanged;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AgentState> Agents => _agents;

    /// <inheritdoc />
    public string? ActiveAgentId { get; set; }

    /// <inheritdoc />
    public void SeedAgents(IEnumerable<AgentSummary> agents)
    {
        foreach (var a in agents)
        {
            if (!_agents.TryGetValue(a.AgentId, out var existing))
            {
                _agents[a.AgentId] = new AgentState
                {
                    AgentId = a.AgentId,
                    DisplayName = a.DisplayName,
                    Emoji = a.Emoji,
                    IsConnected = true
                };
            }
            else
            {
                existing.DisplayName = a.DisplayName;
                existing.Emoji = a.Emoji;
            }
        }

        NotifyChanged();
    }

    /// <inheritdoc />
    public AgentState? GetAgent(string agentId) =>
        _agents.GetValueOrDefault(agentId);

    /// <inheritdoc />
    public void UpsertAgent(AgentState agent)
    {
        _agents[agent.AgentId] = agent;
        NotifyChanged();
    }

    /// <inheritdoc />
    public void RemoveAgent(string agentId)
    {
        _agents.Remove(agentId);
        if (ActiveAgentId == agentId)
            ActiveAgentId = _agents.Keys.FirstOrDefault();
        NotifyChanged();
    }

    /// <inheritdoc />
    public void SeedConversations(string agentId, IEnumerable<ConversationSummaryDto> conversations)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;

        var incoming = conversations.ToList();

        foreach (var dto in incoming)
        {
            if (agent.Conversations.TryGetValue(dto.ConversationId, out var existing))
            {
                // Preserve local-only state; update server-sourced fields
                existing.Title = dto.Title;
                existing.IsDefault = dto.IsDefault;
                existing.Status = dto.Status;
                existing.ActiveSessionId = dto.ActiveSessionId;
                existing.CreatedAt = dto.CreatedAt;
                existing.UpdatedAt = dto.UpdatedAt;
            }
            else
            {
                agent.Conversations[dto.ConversationId] = new ConversationState
                {
                    ConversationId = dto.ConversationId,
                    Title = dto.Title,
                    IsDefault = dto.IsDefault,
                    Status = dto.Status,
                    ActiveSessionId = dto.ActiveSessionId,
                    CreatedAt = dto.CreatedAt,
                    UpdatedAt = dto.UpdatedAt
                };
            }
        }

        // Remove conversations no longer in the list
        var incomingIds = incoming.Select(d => d.ConversationId).ToHashSet();
        foreach (var id in agent.Conversations
                     .Where(kv => !incomingIds.Contains(kv.Key) && !kv.Value.IsVirtualSession)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            agent.Conversations.Remove(id);
        }

        // Auto-select a conversation if none is selected
        if (agent.ActiveConversationId is null && agent.Conversations.Count > 0)
        {
            var defaultConv = agent.Conversations.Values.FirstOrDefault(c => c.IsDefault)
                ?? agent.Conversations.Values.OrderByDescending(c => c.UpdatedAt).First();
            agent.ActiveConversationId = defaultConv.ConversationId;
            if (defaultConv.ActiveSessionId is not null)
                agent.SessionId = defaultConv.ActiveSessionId;
        }

        agent.ConversationsLoaded = true;
        agent.IsLoadingConversations = false;

        NotifyChanged();
    }

    /// <inheritdoc />
    public ConversationState? GetConversation(string conversationId)
    {
        foreach (var agent in _agents.Values)
        {
            if (agent.Conversations.TryGetValue(conversationId, out var conv))
                return conv;
        }

        return null;
    }

    /// <inheritdoc />
    public void SetActiveConversation(string agentId, string conversationId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;

        agent.ActiveConversationId = conversationId;

        if (agent.Conversations.TryGetValue(conversationId, out var conv))
        {
            conv.UnreadCount = 0;
            agent.SessionId = conv.ActiveSessionId;
        }

        NotifyChanged();
    }

    /// <inheritdoc />
    public string? ActiveConversationId =>
        ActiveAgentId is not null && _agents.TryGetValue(ActiveAgentId, out var a)
            ? a.ActiveConversationId
            : null;

    // ── Message operations ───────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> GetMessages(string conversationId)
    {
        var conv = GetConversation(conversationId);
        return conv?.Messages ?? (IReadOnlyList<ChatMessage>)[];
    }

    /// <inheritdoc />
    public void AppendMessage(string conversationId, ChatMessage message)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.Messages.Add(message);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void PrependMessages(string conversationId, IEnumerable<ChatMessage> messages)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.Messages.InsertRange(0, messages);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void ClearMessages(string conversationId)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.Messages.Clear();
        conv.HistoryLoaded = false;
        NotifyChanged();
    }

    // ── Streaming state ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public ConversationStreamState GetStreamState(string conversationId)
    {
        var conv = GetConversation(conversationId);
        return conv?.StreamState ?? new ConversationStreamState();
    }

    /// <inheritdoc />
    public void SetStreaming(string conversationId, bool streaming)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.StreamState.IsStreaming = streaming;

        // Only update agent-level IsStreaming if this is the active conversation
        // — prevents streaming state from bleeding into inactive conversations
        foreach (var agent in _agents.Values)
        {
            if (agent.Conversations.ContainsKey(conversationId))
            {
                if (agent.ActiveConversationId == conversationId)
                    agent.IsStreaming = streaming;
                else if (streaming == false)
                    agent.IsStreaming = agent.Conversations.Values.Any(c => c.StreamState.IsStreaming);
                break;
            }
        }

        NotifyChanged();
    }

    /// <inheritdoc />
    public void AppendStreamBuffer(string conversationId, string delta)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.StreamState.Buffer += delta;
        NotifyChanged();
    }

    /// <inheritdoc />
    public void CommitStreamBuffer(string conversationId, string? thinkingContent = null)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        var buffer = conv.StreamState.Buffer;
        var thinking = thinkingContent ?? (string.IsNullOrEmpty(conv.StreamState.ThinkingBuffer)
            ? null
            : conv.StreamState.ThinkingBuffer);

        if (!string.IsNullOrEmpty(buffer) || thinking is not null)
        {
            conv.Messages.Add(new ChatMessage("Assistant", buffer ?? "", DateTimeOffset.UtcNow)
            {
                ThinkingContent = thinking
            });
        }

        conv.StreamState.Buffer = "";
        conv.StreamState.ThinkingBuffer = "";
        conv.StreamState.IsStreaming = false;

        // Keep agent-level state in sync
        foreach (var agent in _agents.Values)
        {
            if (agent.Conversations.ContainsKey(conversationId))
            {
                agent.IsStreaming = false;
                break;
            }
        }

        NotifyChanged();
    }

    // ── ask_user prompt state ────────────────────────────────────────────────

    /// <inheritdoc />
    public void SetPendingAskUser(AskUserPromptState prompt)
    {
        _pendingAskUserByConversation[prompt.ConversationId] = prompt;
        NotifyChanged();
    }

    /// <inheritdoc />
    public void ClearPendingAskUser(string conversationId)
    {
        if (_pendingAskUserByConversation.Remove(conversationId))
            NotifyChanged();
    }

    /// <inheritdoc />
    public AskUserPromptState? GetPendingAskUser(string conversationId) =>
        _pendingAskUserByConversation.GetValueOrDefault(conversationId);

    // ── Session resolution ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public void RegisterSession(string agentId, string sessionId, string? channelType = null, string? sessionType = null)
    {
        _sessionToAgent[sessionId] = agentId;
        if (!_agents.TryGetValue(agentId, out var agent))
            return;
        agent.SessionId = sessionId;
        if (channelType is not null)
            agent.ChannelType = channelType;
        if (sessionType is not null)
            agent.SessionType = sessionType;
        // Immediately bind session to active conversation so TryResolveConversationBySession works
        // without waiting for the async REST refresh. This eliminates the race condition (#314)
        // where MessageStart arrives before RefreshConversationsForAgentAsync completes.
        if (agent.ActiveConversationId is not null &&
            agent.Conversations.TryGetValue(agent.ActiveConversationId, out var activeConv))
        {
            activeConv.ActiveSessionId = sessionId;
        }
    }

    /// <inheritdoc />
    public bool TryResolveAgentBySession(string? sessionId, out string? agentId)
    {
        if (sessionId is null) { agentId = null; return false; }
        return _sessionToAgent.TryGetValue(sessionId, out agentId);
    }

    /// <inheritdoc />
    public bool TryResolveConversationBySession(string agentId, string? sessionId, out string? conversationId)
    {
        conversationId = null;
        if (sessionId is null) return false;
        if (!_agents.TryGetValue(agentId, out var agent)) return false;
        var conv = agent.Conversations.Values.FirstOrDefault(c => c.ActiveSessionId == sessionId);
        if (conv is null) return false;
        conversationId = conv.ConversationId;
        return true;
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public void NotifyChanged() => OnChanged?.Invoke();
}
