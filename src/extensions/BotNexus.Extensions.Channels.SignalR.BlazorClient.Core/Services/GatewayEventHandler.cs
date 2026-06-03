using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Maps SignalR hub events to <see cref="IClientStateStore"/> mutations.
/// Subscribes to <see cref="GatewayHubConnection"/> events in the constructor.
/// </summary>
public sealed class GatewayEventHandler : IGatewayEventHandler, IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IClientStateStore _store;
    private readonly GatewayHubConnection _hub;
    private readonly HashSet<string> _streamingWhenDisconnected = new();

    // Conversation refreshes that arrive during a streaming turn are deferred
    // until HandleMessageEnd/HandleError to avoid overwriting ActiveSessionId
    // mid-turn (breaks stream event routing -- issue #456).
    private readonly HashSet<string> _pendingConversationRefresh = new();

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
        _hub.OnUserInputRequired += HandleUserInputRequired;
        _hub.OnTurnInterrupted += HandleTurnInterrupted;
        _hub.OnSessionReset += HandleSessionReset;
        _hub.OnSubAgentSpawned += HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted += HandleSubAgentCompleted;
        _hub.OnSubAgentFailed += HandleSubAgentFailed;
        _hub.OnSubAgentKilled += HandleSubAgentKilled;
        _hub.OnSteeringFeedback += HandleSteeringFeedback;
        _hub.OnCanvasUpdated += HandleCanvasUpdated;
        _hub.OnConversationChanged += HandleConversationChanged;
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
                    Emoji = agent.Emoji,
                    IsConnected = true
                });
            }
            else if (_store.GetAgent(agent.AgentId) is { } existing)
            {
                existing.DisplayName = agent.DisplayName;
                existing.Emoji = agent.Emoji;
                existing.IsConnected = true;
            }
        }

        _store.NotifyChanged();
    }

    public void HandleMessageStart(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId);
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
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.Buffer += evt.ContentDelta ?? "";
        agent.ProcessingStage = "🤖 Agent is responding…";
        _store.NotifyChanged();
    }

    public void HandleThinkingDelta(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.ThinkingBuffer += evt.ThinkingContent ?? "";
        agent.ProcessingStage = "💭 Thinking…";
        _store.NotifyChanged();
    }

    public void HandleToolStart(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;
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
        _store.NotifyChanged();
    }

    public void HandleToolEnd(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;
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
                    ToolResult = AnsiStripper.Strip(evt.ToolResult),
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
            ToolResult = AnsiStripper.Strip(evt.ToolResult),
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
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;
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

        // Update timestamp client-side so sort order reflects recent activity (issue #382).
        conv.UpdatedAt = DateTimeOffset.UtcNow;

        if (agent.AgentId != _store.ActiveAgentId)
            agent.UnreadCount++;

        if (convId != agent.ActiveConversationId)
            conv.UnreadCount++;

        _store.ClearPendingAskUser(convId);
        _store.NotifyChanged();

        // Drain any conversation-list refreshes that were deferred during the stream
        // (e.g., conversation(action=new) called mid-turn -- issue #456).
        DrainPendingConversationRefreshes(agentId!);
    }

    public void HandleError(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;

        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("Error", evt.ErrorMessage ?? "An unknown error occurred.", DateTimeOffset.UtcNow));
            conv.StreamState.Buffer = "";
            conv.StreamState.ThinkingBuffer = "";
            conv.StreamState.IsStreaming = false;
            _store.ClearPendingAskUser(convId);
        }

        if (agent is not null)
        {
            agent.IsStreaming = false;
            agent.ProcessingStage = null;
        }

        _store.NotifyChanged();

        // Drain deferred refreshes on error-exit as well.
        if (agentId is not null)
            DrainPendingConversationRefreshes(agentId);
    }


    /// <summary>
    /// Handles a <c>TurnInterrupted</c> event signalling that a gateway restart cut the previous turn short.
    /// Appends a notification message to the affected conversation so the user sees the context.
    /// </summary>
    public void HandleTurnInterrupted(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;

        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("Notification",
                evt.ErrorMessage ?? "The gateway was restarted while your last message was being processed.",
                DateTimeOffset.UtcNow));
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
    public void HandleUserInputRequired(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId))
            return;

        if (!TryBuildAskUserPrompt(evt, out var prompt))
            return;

        var resolvedConversationId = prompt.ConversationId;
        if (string.IsNullOrWhiteSpace(resolvedConversationId))
            resolvedConversationId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;
        if (string.IsNullOrWhiteSpace(resolvedConversationId))
            return;

        _store.SetPendingAskUser(prompt with { ConversationId = resolvedConversationId });
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
            _store.ClearPendingAskUser(conv.ConversationId);
        }

        agent.ActiveToolCalls.Clear();
        agent.SubAgents.Clear();
        agent.UnreadCount = 0;

        _store.NotifyChanged();
    }

    // ── Sub-agent events ──────────────────────────────────────────────────

    public void HandleSubAgentSpawned(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var agentId, out var agent, payload.ConversationId)) return;
        // PR1.5 (#682): SubAgentSignalRBridge stamps payload.ConversationId — prefer it.
        var convId = ResolveConversationId(agentId!, agent!, payload.SessionId, payload.ConversationId);

        agent.SubAgents[payload.SubAgentId] = new SubAgentInfo
        {
            SubAgentId = payload.SubAgentId,
            Name = payload.Name,
            Task = payload.Task,
            OriginConversationId = convId,
            Status = "Running",
            StartedAt = payload.StartedAt,
            Model = payload.Model,
            Archetype = payload.Archetype,
            ChildSessionId = payload.ChildSessionId
        };

        // Register sub-agent's own session
        _store.RegisterSession(payload.SubAgentId, payload.SubAgentId);

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            var taskHint = string.IsNullOrWhiteSpace(payload.Task) ? string.Empty : $" — {payload.Task}";
            conv.Messages.Add(new ChatMessage("System",
                $"🔄 Sub-agent spawned: {payload.Name ?? payload.SubAgentId}{taskHint}",
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    public void HandleSubAgentCompleted(SubAgentEventPayload payload)
    {
        if (!ResolveAgent(payload.SessionId, out var agentId, out var agent, payload.ConversationId)) return;
        var convId = ResolveSubAgentConversationId(agentId!, agent!, payload);

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
        if (!ResolveAgent(payload.SessionId, out var agentId, out var agent, payload.ConversationId)) return;
        var convId = ResolveSubAgentConversationId(agentId!, agent!, payload);

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
        if (!ResolveAgent(payload.SessionId, out var agentId, out var agent, payload.ConversationId)) return;
        var convId = ResolveSubAgentConversationId(agentId!, agent!, payload);

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

    public void HandleCanvasUpdated(string agentId, string conversationId, string html)
    {
        var agent = string.IsNullOrWhiteSpace(agentId) ? null : _store.GetAgent(agentId);

        if (agent is null)
            return;

        // Route canvas to conversation (Phase 3, #413)
        var conv = string.IsNullOrWhiteSpace(conversationId)
            ? null
            : agent.Conversations.GetValueOrDefault(conversationId);
        if (conv is null)
        {
            // Fallback: unknown conversation — nothing to update
            _store.NotifyChanged();
            return;
        }
        conv.CanvasHtml = string.IsNullOrWhiteSpace(html) ? null : html;
        conv.CanvasUpdatedAt = DateTimeOffset.UtcNow;
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

    private bool ResolveAgent(
        string? sessionId,
        [NotNullWhen(true)] out string? agentId,
        [NotNullWhen(true)] out AgentState? agent,
        string? conversationIdHint = null)
    {
        agentId = null;
        agent = null;
        if (_store.TryResolveAgentBySession(sessionId, out agentId) && agentId is not null)
        {
            agent = _store.GetAgent(agentId);
            return agent is not null;
        }
        // PR1.5 (#682): post-compaction events arrive with a new sessionId the client
        // has not yet registered, but the conversation survives. Use the conversation
        // hint to find the owning agent so the routing still lands.
        if (!string.IsNullOrWhiteSpace(conversationIdHint)
            && _store.TryResolveAgentByConversation(conversationIdHint, out agentId)
            && agentId is not null)
        {
            agent = _store.GetAgent(agentId);
            return agent is not null;
        }
        return false;
    }

    private string? ResolveConversationId(string agentId, AgentState agent, string? sessionId, string? conversationIdHint = null)
    {
        // PR1.5 (#682): SignalR stamps ConversationId on stream payloads so the client
        // does not need a session→conversation lookup at all. Prefer the hint when set;
        // fall back to the legacy resolver for events that have not yet been updated.
        if (!string.IsNullOrWhiteSpace(conversationIdHint))
            return conversationIdHint;
        if (_store.TryResolveConversationBySession(agentId, sessionId, out var convId))
            return convId;
        return agent.ActiveConversationId;
    }

    private string? ResolveSubAgentConversationId(string agentId, AgentState agent, SubAgentEventPayload payload)
    {
        // PR1.5 (#682): SubAgentSignalRBridge stamps payload.ConversationId on every
        // event. Prefer it over the session-bound lookup so post-compaction sub-agent
        // events still route to the surviving conversation state.
        if (!string.IsNullOrWhiteSpace(payload.ConversationId))
            return payload.ConversationId;

        if (agent.SubAgents.TryGetValue(payload.SubAgentId, out var subAgent) &&
            !string.IsNullOrWhiteSpace(subAgent.OriginConversationId))
        {
            return subAgent.OriginConversationId;
        }

        if (_store.TryResolveConversationBySession(agentId, payload.SessionId, out var convId))
            return convId;

        return null;
    }

    private static bool TryBuildAskUserPrompt(AgentStreamEvent evt, [NotNullWhen(true)] out AskUserPromptState? prompt)
    {
        prompt = null;
        var metadata = evt.Metadata;
        var payload = evt.UserInputRequest;

        var requestId = GetRequiredString(metadata, "requestId") ?? payload?.RequestId;
        var conversationId = GetRequiredString(metadata, "conversationId") ?? payload?.ConversationId;
        var promptText = GetRequiredString(metadata, "prompt") ?? payload?.Prompt;
        var inputType = GetRequiredString(metadata, "inputType") ?? payload?.InputType;

        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(promptText) ||
            string.IsNullOrWhiteSpace(inputType))
        {
            return false;
        }

        var choices = ParseChoices(metadata, payload?.Choices);
        var allowMultiple = GetBool(metadata, "allowMultiple") ?? payload?.AllowMultiple ?? false;
        var allowFreeForm = GetBool(metadata, "allowFreeForm") ?? payload?.AllowFreeForm ?? false;
        var timeout = GetString(metadata, "timeout") ?? payload?.Timeout;
        var expiresAt = ParseExpiration(timeout);

        prompt = new AskUserPromptState
        {
            RequestId = requestId,
            ConversationId = conversationId ?? string.Empty,
            Prompt = promptText,
            InputType = inputType,
            Choices = choices,
            AllowMultiple = allowMultiple,
            AllowFreeForm = allowFreeForm,
            ExpiresAt = expiresAt
        };

        return true;
    }

    private static string? GetRequiredString(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        var value = GetString(metadata, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return null;

        return raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();
    }

    private static bool? GetBool(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return null;

        return raw.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(raw.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<AskUserChoiceState>? ParseChoices(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        IReadOnlyList<AskUserChoicePayload>? fallbackChoices)
    {
        if (metadata is not null && metadata.TryGetValue("choices", out var rawChoices))
        {
            var parsed = ParseChoicesFromJson(rawChoices);
            if (parsed is { Count: > 0 })
                return parsed;
        }

        if (fallbackChoices is null || fallbackChoices.Count == 0)
            return null;

        return fallbackChoices
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
            .Select(choice => new AskUserChoiceState(
                choice.Value!,
                string.IsNullOrWhiteSpace(choice.Label) ? choice.Value! : choice.Label!,
                choice.Description))
            .ToList();
    }

    private static IReadOnlyList<AskUserChoiceState>? ParseChoicesFromJson(JsonElement rawChoices)
    {
        JsonElement choicesElement;
        if (rawChoices.ValueKind == JsonValueKind.String)
        {
            var rawString = rawChoices.GetString();
            if (string.IsNullOrWhiteSpace(rawString))
                return null;

            try
            {
                using var document = JsonDocument.Parse(rawString);
                choicesElement = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else
        {
            choicesElement = rawChoices;
        }

        if (choicesElement.ValueKind != JsonValueKind.Array)
            return null;

        var choices = new List<AskUserChoiceState>();
        foreach (var choice in choicesElement.EnumerateArray())
        {
            var value = choice.TryGetProperty("value", out var valueElement)
                ? valueElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var label = choice.TryGetProperty("label", out var labelElement)
                ? labelElement.GetString()
                : null;
            var description = choice.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;

            choices.Add(new AskUserChoiceState(
                value,
                string.IsNullOrWhiteSpace(label) ? value : label,
                description));
        }

        return choices;
    }

    private static DateTimeOffset? ParseExpiration(string? timeout)
    {
        if (string.IsNullOrWhiteSpace(timeout) || !TimeSpan.TryParse(timeout, out var duration))
            return null;

        return DateTimeOffset.UtcNow.Add(duration);
    }

    // ── Hub event wiring (called when hub reconnects) ─────────────────────

    private void HandleReconnected() => _ = HandleReconnectedAsync();

    public void HandleConversationChanged(ConversationChangedPayload payload)
    {
        // Server notified that a conversation was created, updated, or archived.
        var agentId = payload.AgentId;
        if (string.IsNullOrWhiteSpace(agentId)) return;

        // Fast path: if the payload carries the new UpdatedAt, apply it directly
        // to the client state without a REST round-trip (issue #382).
        if (payload.UpdatedAt.HasValue && _store.GetAgent(agentId) is { } agent)
        {
            var convId = payload.ConversationId;
            if (!string.IsNullOrWhiteSpace(convId) &&
                agent.Conversations.TryGetValue(convId, out var conv))
            {
                conv.UpdatedAt = payload.UpdatedAt.Value;
                _store.NotifyChanged();
                return;
            }
        }

        // Fallback: full conversation list refresh (covers create/archive and cases
        // where the conversation is not yet in the local store).
        // Guard: defer refresh if the agent is mid-stream -- SeedConversations overwrites
        // ActiveSessionId from server state, which breaks TryResolveConversationBySession
        // and causes stream events to stop rendering in the portal (issue #456).
        var streamingAgent = _store.GetAgent(agentId);
        if (streamingAgent?.IsStreaming == true)
        {
            _pendingConversationRefresh.Add(agentId);
            return;
        }

        _ = RefreshConversationsAsync(agentId);
    }

    // Injected async refresh delegate — wired by AgentInteractionService or PortalLoadService.
    public Func<string, Task>? ConversationRefreshDelegate { get; set; }

    private Task RefreshConversationsAsync(string agentId)
        => ConversationRefreshDelegate?.Invoke(agentId) ?? Task.CompletedTask;

    private void DrainPendingConversationRefreshes(string agentId)
    {
        if (!_pendingConversationRefresh.Remove(agentId))
            return;
        _ = RefreshConversationsAsync(agentId);
    }

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
        _hub.OnUserInputRequired -= HandleUserInputRequired;
        _hub.OnTurnInterrupted -= HandleTurnInterrupted;
        _hub.OnSessionReset -= HandleSessionReset;
        _hub.OnSubAgentSpawned -= HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted -= HandleSubAgentCompleted;
        _hub.OnSubAgentFailed -= HandleSubAgentFailed;
        _hub.OnSubAgentKilled -= HandleSubAgentKilled;
        _hub.OnSteeringFeedback -= HandleSteeringFeedback;
        _hub.OnCanvasUpdated -= HandleCanvasUpdated;
        _hub.OnConversationChanged -= HandleConversationChanged;
                _hub.OnReconnecting -= HandleReconnecting;
        _hub.OnReconnected -= HandleReconnected;
        _hub.OnDisconnected -= HandleDisconnected;
    }
}

