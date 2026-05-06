using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Maps SignalR hub events to <see cref="IClientStateStore"/> mutations.
/// Subscribes to <see cref="GatewayHubConnection"/> events in the constructor.
/// </summary>
public sealed class GatewayEventHandler : IGatewayEventHandler, IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly IClientStateStore _store;
    private readonly GatewayHubConnection _hub;
    private readonly HashSet<string> _streamingWhenDisconnected = new();

    public GatewayEventHandler(IClientStateStore store, GatewayHubConnection hub)
    {
        _store = store;
        _hub = hub;

        _hub.OnConnected += HandleConnected;
        _hub.OnMessageStart += HandleMessageStart;
        _hub.OnContentDelta += HandleContentDelta;
        _hub.OnThinkingDelta += HandleThinkingDelta;
        _hub.OnToolStart += HandleToolStart;
        _hub.OnToolEnd += HandleToolEnd;
        _hub.OnMessageEnd += HandleMessageEnd;
        _hub.OnError += HandleError;
        _hub.OnSessionReset += HandleSessionReset;
        _hub.OnSubAgentSpawned += HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted += HandleSubAgentCompleted;
        _hub.OnSubAgentFailed += HandleSubAgentFailed;
        _hub.OnSubAgentKilled += HandleSubAgentKilled;
        _hub.OnSteeringFeedback += HandleSteeringFeedback;
        _hub.OnReconnecting += HandleReconnecting;
        _hub.OnReconnected += HandleReconnected;
        _hub.OnDisconnected += HandleDisconnected;
    }

    // ── Event handlers ────────────────────────────────────────────────────

    public void HandleConnected(ConnectedPayload payload)
    {
        // Upsert agents but do NOT trigger a full conversation reload —
        // PortalLoadService already loaded everything before connecting.
        foreach (var agent in payload.Agents)
        {
            if (!_store.Agents.ContainsKey(agent.AgentId))
            {
                _store.UpsertAgent(new AgentState
                {
                    AgentId = agent.AgentId,
                    DisplayName = agent.DisplayName,
                    IsConnected = true
                });
            }
            else if (_store.GetAgent(agent.AgentId) is { } existing)
            {
                existing.DisplayName = agent.DisplayName;
                existing.IsConnected = true;
            }
        }

        _store.NotifyChanged();
    }

    public void HandleMessageStart(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        agent.IsStreaming = true;
        conv.StreamState.IsStreaming = true;
        conv.StreamState.Buffer = "";
        conv.StreamState.ThinkingBuffer = "";
        agent.ProcessingStage = "🤖 Agent is responding…";
        _store.NotifyChanged();
    }

    public void HandleContentDelta(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.Buffer += evt.ContentDelta ?? "";
        agent.ProcessingStage = "🤖 Agent is responding…";
        _store.NotifyChanged();
    }

    public void HandleThinkingDelta(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.ThinkingBuffer += evt.ThinkingContent ?? "";
        agent.ProcessingStage = "💭 Thinking…";
        _store.NotifyChanged();
    }

    public void HandleToolStart(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId) ?? agent!.ActiveConversationId;
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        var toolCallId = evt.ToolCallId ?? Guid.NewGuid().ToString("N");
        var argsJson = evt.ToolArgs is not null
            ? JsonSerializer.Serialize(evt.ToolArgs, s_jsonOptions)
            : null;

        var msg = new ChatMessage("Tool", $"⏳ Calling {evt.ToolName}…", DateTimeOffset.UtcNow)
        {
            ToolName = evt.ToolName,
            ToolCallId = toolCallId,
            ToolArgs = argsJson,
            IsToolCall = true
        };

        conv.Messages.Add(msg);
        conv.StreamState.ActiveToolCalls[toolCallId] = new ActiveToolCall
        {
            ToolCallId = toolCallId,
            ToolName = evt.ToolName ?? "unknown",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = msg.Id
        };

        if (convId != agent.ActiveConversationId)
            conv.UnreadCount++;

        agent.ProcessingStage = $"🔧 Using tool: {evt.ToolName}";
        _store.NotifyChanged();
    }

    public void HandleToolEnd(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId) ?? agent!.ActiveConversationId;
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        var toolCallId = evt.ToolCallId;
        TimeSpan? duration = null;
        string? messageId = null;

        if (toolCallId is not null && conv.StreamState.ActiveToolCalls.TryGetValue(toolCallId, out var activeTool))
        {
            duration = DateTimeOffset.UtcNow - activeTool.StartedAt;
            messageId = activeTool.MessageId;
            conv.StreamState.ActiveToolCalls.Remove(toolCallId);
        }

        if (messageId is not null)
        {
            var index = conv.Messages.FindIndex(m => m.Id == messageId);
            if (index >= 0)
            {
                var original = conv.Messages[index];
                conv.Messages[index] = original with
                {
                    Content = evt.ToolIsError == true ? $"❌ {evt.ToolName} failed" : $"✅ {evt.ToolName} completed",
                    ToolResult = evt.ToolResult,
                    ToolIsError = evt.ToolIsError,
                    ToolDuration = duration
                };

                agent.ProcessingStage = agent.IsStreaming ? "🤖 Agent is responding…" : null;
                _store.NotifyChanged();
                return;
            }
        }

        // Fallback: new message
        conv.Messages.Add(new ChatMessage("Tool",
            evt.ToolIsError == true ? $"❌ {evt.ToolName} failed" : $"✅ {evt.ToolName} completed",
            DateTimeOffset.UtcNow)
        {
            ToolName = evt.ToolName,
            ToolCallId = toolCallId,
            ToolResult = evt.ToolResult,
            IsToolCall = true,
            ToolIsError = evt.ToolIsError,
            ToolDuration = duration
        });

        if (convId != agent.ActiveConversationId)
            conv.UnreadCount++;

        agent.ProcessingStage = agent.IsStreaming ? "🤖 Agent is responding…" : null;
        _store.NotifyChanged();
    }

    // Agents use this exact string to indicate they have nothing to say in a turn.
    // Suppress it silently — users must never see "NO_REPLY" as a chat message.
    private const string NoReplySentinel = "NO_REPLY";

    public void HandleMessageEnd(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId) ?? agent!.ActiveConversationId;
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        var thinkingContent = string.IsNullOrEmpty(conv.StreamState.ThinkingBuffer)
            ? null
            : conv.StreamState.ThinkingBuffer;

        var isNoReply = string.Equals(conv.StreamState.Buffer.Trim(), NoReplySentinel, StringComparison.Ordinal);

        if (!isNoReply && (!string.IsNullOrEmpty(conv.StreamState.Buffer) || thinkingContent is not null))
        {
            conv.Messages.Add(new ChatMessage("Assistant", conv.StreamState.Buffer, DateTimeOffset.UtcNow)
            {
                ThinkingContent = thinkingContent
            });
        }

        conv.StreamState.Buffer = "";
        conv.StreamState.ThinkingBuffer = "";
        conv.StreamState.IsStreaming = false;
        agent.IsStreaming = false;
        agent.ProcessingStage = null;

        if (agent.AgentId != _store.ActiveAgentId)
            agent.UnreadCount++;

        if (convId != agent.ActiveConversationId)
            conv.UnreadCount++;

        _store.NotifyChanged();
    }

    public void HandleError(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId) ?? agent!.ActiveConversationId;

        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("Error", evt.ErrorMessage ?? "An unknown error occurred.", DateTimeOffset.UtcNow));
            conv.StreamState.Buffer = "";
            conv.StreamState.ThinkingBuffer = "";
            conv.StreamState.IsStreaming = false;
        }

        if (agent is not null)
        {
            agent.IsStreaming = false;
            agent.ProcessingStage = null;
        }

        _store.NotifyChanged();
    }

    public void HandleSessionReset(SessionResetPayload payload)
    {
        var agent = _store.GetAgent(payload.AgentId);
        if (agent is null) return;

        agent.IsStreaming = false;
        agent.ProcessingStage = null;

        if (agent.SessionId is not null)
            // We can't remove from _sessionToAgent directly; re-registering with null clears it next time
            agent.SessionId = null;

        if (agent.ActiveConversationId is not null &&
            agent.Conversations.GetValueOrDefault(agent.ActiveConversationId) is { } conv)
        {
            conv.StreamState.Buffer = "";
            conv.StreamState.ThinkingBuffer = "";
            conv.StreamState.IsStreaming = false;
            conv.StreamState.ActiveToolCalls.Clear();
            // Do NOT clear conv.Messages or set HistoryLoaded=false.
            // Session reset clears the agent's context window — it does not
            // erase conversation history. The portal should keep showing all
            // prior messages with a visual divider marking the new session.
            conv.Messages.Add(new ChatMessage("System", "─── New session started ───", DateTimeOffset.UtcNow));
        }

        agent.ActiveToolCalls.Clear();
        agent.SubAgents.Clear();
        agent.UnreadCount = 0;

        _store.NotifyChanged();
    }

    // ── Sub-agent events ──────────────────────────────────────────────────

    public void HandleSubAgentSpawned(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var agentId, out var agent)) return;
        var convId = agent!.ActiveConversationId;

        agent.SubAgents[payload.SubAgentId] = new SubAgentInfo
        {
            SubAgentId = payload.SubAgentId,
            Name = payload.Name,
            Task = payload.Task,
            Status = "Running",
            StartedAt = payload.StartedAt,
            Model = payload.Model,
            Archetype = payload.Archetype
        };

        // Register sub-agent's own session
        _store.RegisterSession(payload.SubAgentId, payload.SubAgentId);

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("System",
                $"🔄 Sub-agent spawned: {payload.Name ?? payload.SubAgentId} — {payload.Task}",
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    public void HandleSubAgentCompleted(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var _, out var agent)) return;
        var convId = agent!.ActiveConversationId;

        if (agent.SubAgents.TryGetValue(payload.SubAgentId, out var sub))
        {
            sub.Status = "Completed";
            sub.CompletedAt = payload.CompletedAt;
            sub.ResultSummary = payload.ResultSummary;
        }

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("System",
                $"✅ Sub-agent completed: {payload.Name ?? payload.SubAgentId}" +
                (payload.ResultSummary is not null ? $" — {payload.ResultSummary}" : ""),
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    public void HandleSubAgentFailed(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var _, out var agent)) return;
        var convId = agent!.ActiveConversationId;

        if (agent.SubAgents.TryGetValue(payload.SubAgentId, out var sub))
        {
            sub.Status = "Failed";
            sub.CompletedAt = payload.CompletedAt;
            sub.ResultSummary = payload.ResultSummary;
        }

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("System",
                $"❌ Sub-agent failed: {payload.Name ?? payload.SubAgentId}" +
                (payload.ResultSummary is not null ? $" — {payload.ResultSummary}" : ""),
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    public void HandleSubAgentKilled(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var _, out var agent)) return;
        var convId = agent!.ActiveConversationId;

        if (agent.SubAgents.TryGetValue(payload.SubAgentId, out var sub))
        {
            sub.Status = "Killed";
            sub.CompletedAt = payload.CompletedAt;
        }

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("System",
                $"⛔ Sub-agent killed: {payload.Name ?? payload.SubAgentId}",
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    // ── Steering feedback ──────────────────────────────────────────────────

    /// <summary>
    /// Appends a subtle system message to the active conversation confirming that
    /// a steering message was accepted or queued.
    /// </summary>
    public void HandleSteeringFeedback(SteeringFeedbackPayload payload)
    {
        // Find agent owning this session
        var agent = _store.Agents.Values.FirstOrDefault(a =>
            a.ActiveConversationSessionId == payload.SessionId ||
            a.SessionId == payload.SessionId);

        if (agent is null) return;

        var convId = agent.ActiveConversationId;
        if (convId is null || !agent.Conversations.TryGetValue(convId, out var conv)) return;

        var text = payload.Kind == SteeringFeedbackKind.Injected
            ? "↳ Steering accepted mid-turn"
            : "↳ Steering queued — will process next turn";

        conv.Messages.Add(new ChatMessage("System", text, DateTimeOffset.UtcNow));
        _store.NotifyChanged();
    }

    // ── Connection lifecycle ──────────────────────────────────────────────

    public void HandleReconnecting()
    {
        foreach (var agent in _store.Agents.Values)
        {
            if (agent.IsStreaming)
                _streamingWhenDisconnected.Add(agent.AgentId);
            agent.IsConnected = false;
        }

        _store.NotifyChanged();
    }

    public async Task HandleReconnectedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var agent in _store.Agents.Values)
            agent.IsConnected = true;

        try
        {
            var result = await _hub.SubscribeAllAsync();
            foreach (var session in result.Sessions)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType);

            foreach (var agentId in _streamingWhenDisconnected)
            {
                if (_store.GetAgent(agentId) is { } agent)
                {
                    agent.IsStreaming = false;
                    agent.ProcessingStage = null;

                    if (agent.ActiveConversationId is not null &&
                        agent.Conversations.GetValueOrDefault(agent.ActiveConversationId) is { } conv)
                    {
                        conv.StreamState.Buffer = "";
                        conv.StreamState.ThinkingBuffer = "";
                        conv.StreamState.IsStreaming = false;
                    }
                }
            }

            _streamingWhenDisconnected.Clear();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GatewayEventHandler: reconnect recovery failed: {ex.Message}");
        }

        _store.NotifyChanged();
    }

    public void HandleDisconnected()
    {
        foreach (var agent in _store.Agents.Values)
            agent.IsConnected = false;
        _store.NotifyChanged();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private bool ResolveAgent(string? sessionId, out string? agentId, out AgentState? agent)
    {
        agentId = null;
        agent = null;
        if (!_store.TryResolveAgentBySession(sessionId, out agentId) || agentId is null)
            return false;
        agent = _store.GetAgent(agentId);
        return agent is not null;
    }

    private string? ResolveConversationId(string agentId, AgentState agent, string? sessionId)
    {
        if (_store.TryResolveConversationBySession(agentId, sessionId, out var convId))
            return convId;
        return agent.ActiveConversationId;
    }

    // ── Hub event wiring (called when hub reconnects) ─────────────────────

    private void HandleReconnected() => _ = HandleReconnectedAsync();

    public void Dispose()
    {
        _hub.OnConnected -= HandleConnected;
        _hub.OnMessageStart -= HandleMessageStart;
        _hub.OnContentDelta -= HandleContentDelta;
        _hub.OnThinkingDelta -= HandleThinkingDelta;
        _hub.OnToolStart -= HandleToolStart;
        _hub.OnToolEnd -= HandleToolEnd;
        _hub.OnMessageEnd -= HandleMessageEnd;
        _hub.OnError -= HandleError;
        _hub.OnSessionReset -= HandleSessionReset;
        _hub.OnSubAgentSpawned -= HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted -= HandleSubAgentCompleted;
        _hub.OnSubAgentFailed -= HandleSubAgentFailed;
        _hub.OnSubAgentKilled -= HandleSubAgentKilled;
        _hub.OnSteeringFeedback -= HandleSteeringFeedback;
        _hub.OnReconnecting -= HandleReconnecting;
        _hub.OnReconnected -= HandleReconnected;
        _hub.OnDisconnected -= HandleDisconnected;
    }
}
