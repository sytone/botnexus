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

    // #2243 hardening: sub-agent ids marked read-only-for-navigation at SPAWN time, before any
    // AgentState exists or its SessionType has been stamped. The original guard only rejected a
    // switch when target.IsReadOnly was ALREADY derived true (SessionType == "agent-subagent"),
    // but HandleSubAgentSpawned registers the sub-agent asynchronously — during the spawn-during-send
    // window there is often no AgentState yet, or one still carrying the default "user-agent"
    // SessionType, so the derived flag reads false and the guard leaks. Tracking the id at spawn
    // makes the rejection independent of that ordering race. Explicit user "view sub-agent" clicks
    // still bypass via SetActiveSubAgent.
    private readonly HashSet<string> _knownSubAgentIds = new(StringComparer.Ordinal);

    private string? _activeAgentId;

    // High-frequency streaming deltas (content/thinking) route through NotifyChangedThrottled,
    // which coalesces a burst into at most one render per window so a long streamed response
    // does not trigger one StateHasChanged per token (#1620). Discrete lifecycle events keep
    // using the immediate NotifyChanged. The window is computed from an injectable clock so the
    // coalescing is deterministically testable (mirrors the repo's TimeProvider test pattern).
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMilliseconds(50);
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastNotifyUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates the store. <paramref name="clock"/> drives the streaming-render coalescing
    /// window; it defaults to <see cref="TimeProvider.System"/> and is only overridden in tests.
    /// </summary>
    public ClientStateStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    // ── IClientStateStore ────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action? OnChanged;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AgentState> Agents => _agents;

    /// <inheritdoc />
    /// <remarks>
    /// #2243 anti-hijack guard: a sub-agent read-only virtual session (<see cref="AgentState.IsReadOnly"/>)
    /// must never become the active view except through an explicit user "view sub-agent" click, which
    /// routes through <see cref="SetActiveSubAgent"/>. Every other assignment path — SubAgentSpawned and
    /// other streaming events, route/state refreshes, reconnect recovery — flows through this setter, so
    /// silently rejecting a switch onto a read-only agent here keeps the composer, the new-conversation
    /// button, and the user's own conversation in place. This mirrors the existing SeedConversations guard
    /// that already stops virtual sessions from auto-hijacking the active conversation tab.
    /// </remarks>
    public string? ActiveAgentId
    {
        get => _activeAgentId;
        set
        {
            if (value is not null
                && !_allowSubAgentActivation
                && (_knownSubAgentIds.Contains(value)
                    || (_agents.TryGetValue(value, out var target) && target.IsReadOnly)))
            {
                return;
            }

            _activeAgentId = value;
        }
    }

    // Set only for the duration of an explicit user-initiated sub-agent view (SetActiveSubAgent),
    // which is the one path allowed to promote a read-only sub-agent session to the active view.
    private bool _allowSubAgentActivation;

    /// <inheritdoc />
    public void SetActiveSubAgent(string subAgentId)
    {
        _allowSubAgentActivation = true;
        try
        {
            ActiveAgentId = subAgentId;
        }
        finally
        {
            _allowSubAgentActivation = false;
        }
    }

    /// <inheritdoc />
    public void MarkSubAgent(string subAgentId)
    {
        if (!string.IsNullOrEmpty(subAgentId))
            _knownSubAgentIds.Add(subAgentId);
    }

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
                    Description = a.Description,
                    IsBuiltIn = a.IsBuiltIn,
                    IsConnected = true
                };
            }
            else
            {
                existing.DisplayName = a.DisplayName;
                existing.Emoji = a.Emoji;
                existing.Description = a.Description;
                existing.IsBuiltIn = a.IsBuiltIn;
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
        if (_agents.TryGetValue(agent.AgentId, out var existing))
        {
            // Merge metadata without destroying local-only state (Conversations,
            // ActiveConversationId, SessionId, Messages, StreamState, etc.).
            existing.DisplayName = agent.DisplayName;
            existing.Emoji = agent.Emoji;
            existing.Description = agent.Description;
            existing.IsConnected = agent.IsConnected;
        }
        else
        {
            _agents[agent.AgentId] = agent;
        }

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

        // Skip non-user-facing conversations (AgentAgent exchanges, AgentSubAgent supervision)
        // from the portal's conversation list. The user did not initiate these directly and they
        // would clutter the agent's conversation drawer, potentially auto-hijacking the active
        // tab if their UpdatedAt is the most recent. They are still queryable through the REST
        // API for debugging/admin views.
        var incoming = conversations
            .Where(c => string.IsNullOrEmpty(c.Kind) || string.Equals(c.Kind, "HumanAgent", StringComparison.Ordinal))
            .ToList();

        foreach (var dto in incoming)
        {
            if (agent.Conversations.TryGetValue(dto.ConversationId, out var existing))
            {
                // Preserve local-only state; update server-sourced fields
                existing.Title = dto.Title;
                existing.IsDefault = dto.IsDefault;
                existing.IsPinned = dto.IsPinned;
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
                    IsPinned = dto.IsPinned,
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

        conv.AppendMessage(message);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void PrependMessages(string conversationId, IEnumerable<ChatMessage> messages)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.PrependMessages(messages);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void ClearMessages(string conversationId)
    {
        var conv = GetConversation(conversationId);
        if (conv is null)
            return;

        conv.ClearMessages();
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

        conv.StreamState.AppendBuffer(delta);
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
            // #1651: honour a role carried on the buffered content (PendingRole) so a
            // user-stamped agent-post commits as a user bubble. Null defaults to Assistant,
            // matching the pre-post-as-assistant behaviour of this flush.
            var role = (conv.StreamState.PendingRole ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" => "Assistant",
                "user" => "User",
                "assistant" => "Assistant",
                var other => other,
            };
            conv.AppendMessage(new ChatMessage(role, buffer ?? "", DateTimeOffset.UtcNow)
            {
                ThinkingContent = thinking
            });
        }

        conv.StreamState.Reset();

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

    // ── Steering queue ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void AddSteeringEntry(string conversationId, SteeringEntry entry)
    {
        var conv = GetConversation(conversationId);
        if (conv is null) return;

        conv.PendingSteeringQueue.Add(entry);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void UpdateSteeringEntry(string conversationId, string entryId, SteeringEntryStatus newStatus)
    {
        var conv = GetConversation(conversationId);
        if (conv is null) return;

        var index = conv.PendingSteeringQueue.FindIndex(e => e.Id == entryId);
        if (index < 0) return;

        var existing = conv.PendingSteeringQueue[index];
        conv.PendingSteeringQueue[index] = existing with { Status = newStatus };

        // Remove non-pending entries after a short time to avoid clutter
        if (newStatus != SteeringEntryStatus.Pending)
            conv.PendingSteeringQueue.RemoveAt(index);

        NotifyChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<SteeringEntry> GetSteeringQueue(string conversationId)
    {
        var conv = GetConversation(conversationId);
        return conv?.PendingSteeringQueue ?? (IReadOnlyList<SteeringEntry>)[];
    }

    // ── Session resolution ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public void RegisterSession(string agentId, string sessionId, string? channelType = null, string? sessionType = null, string? conversationId = null)
    {
        _sessionToAgent[sessionId] = agentId;
        if (!_agents.TryGetValue(agentId, out var agent))
            return;

        // Cron sessions must not overwrite the active user-facing session or the active
        // conversation's ActiveSessionId. Doing so caused new user conversations to receive
        // a cron: session ID prefix, because RefreshConversationsForAgentAsync iterates all
        // sessions (including cron) and the last one processed would stamp agent.SessionId.
        var isCron = string.Equals(sessionType, "cron", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(sessionId) && sessionId.StartsWith("cron:", StringComparison.Ordinal));
        if (isCron)
        {
            // For cron sessions, only update the virtual conversation projection if it exists.
            // Never touch agent.SessionId or a real user conversation.
            if (channelType is not null)
                agent.ChannelType ??= channelType;
            return;
        }

        if (channelType is not null)
            agent.ChannelType = channelType;
        if (sessionType is not null)
            agent.SessionType = sessionType;

        // When the caller knows which conversation this session belongs to (e.g. the bulk
        // /api/sessions refresh, where each SessionSummary carries its conversationId), bind the
        // session to THAT conversation and only touch the agent-global SessionId / active-conv
        // binding when the session actually belongs to the active conversation.
        //
        // The previous logic always stamped agent.SessionId = sessionId AND bound the session to
        // the *active* conversation regardless of ownership. In a loop over every session that left
        // agent.SessionId pointing at the last-iterated session and overwrote the active
        // conversation's ActiveSessionId with the wrong value — causing steer / abort / compact to
        // target a different conversation's (often idle) session. See steer-routing fix.
        if (conversationId is not null)
        {
            if (agent.Conversations.TryGetValue(conversationId, out var ownerConv) && !ownerConv.IsVirtualSession)
                ownerConv.ActiveSessionId = sessionId;

            // Only the active conversation's session should drive the agent-global fallback.
            if (string.Equals(conversationId, agent.ActiveConversationId, StringComparison.Ordinal))
                agent.SessionId = sessionId;

            return;
        }

        // Legacy single-establish path (conversationId unknown): bind to the active conversation.
        // This is the #314 race fix — a freshly established session (e.g. from a SendMessage
        // result) is immediately bound so MessageStart can resolve it before the REST refresh.
        agent.SessionId = sessionId;
        if (agent.ActiveConversationId is not null &&
            agent.Conversations.TryGetValue(agent.ActiveConversationId, out var activeConv) &&
            !activeConv.IsVirtualSession)
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
    public bool TryResolveAgentByConversation(string? conversationId, out string? agentId)
    {
        agentId = null;
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        foreach (var (id, agent) in _agents)
        {
            if (agent.Conversations.ContainsKey(conversationId))
            {
                agentId = id;
                return true;
            }
        }
        return false;
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
    public void NotifyChanged()
    {
        // Immediate render for discrete lifecycle events (tool start/end, message complete,
        // run end, etc.). Resets the coalescing window so any change accumulated by a throttled
        // delta is now on screen and the next streamed delta fires on its leading edge.
        _lastNotifyUtc = _clock.GetUtcNow();
        OnChanged?.Invoke();
    }

    /// <inheritdoc />
    public void NotifyChangedThrottled()
    {
        // Leading-edge + coalesce: the first notify in a window renders immediately; further
        // notifies inside the same window are dropped (coalesced) so a burst of streamed tokens
        // collapses to at most one render per window. The trailing accumulated state is always
        // flushed by the next window's leading edge or by a discrete NotifyChanged (which every
        // stream terminates with: message-end / turn-end / commit). This avoids a background
        // timer entirely, keeping the path allocation-free and deterministically testable.
        var now = _clock.GetUtcNow();
        if (now - _lastNotifyUtc < ThrottleWindow)
            return;

        _lastNotifyUtc = now;
        OnChanged?.Invoke();
    }
}

