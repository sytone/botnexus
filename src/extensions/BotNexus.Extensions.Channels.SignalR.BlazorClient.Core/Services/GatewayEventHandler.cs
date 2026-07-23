using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<GatewayEventHandler> _logger;
    private readonly HashSet<string> _streamingWhenDisconnected = new();

    // Conversation refreshes that arrive during a streaming turn are deferred
    // until HandleMessageEnd/HandleError to avoid overwriting ActiveSessionId
    // mid-turn (breaks stream event routing -- issue #456).
    private readonly HashSet<string> _pendingConversationRefresh = new();

    // Both HashSets above are mutated from SignalR client callbacks (reconnect lifecycle and
    // ConversationChanged), which are NOT guaranteed to be invoked serially. A concurrent
    // enumerate-while-mutate can throw InvalidOperationException or tear the set's internal state,
    // so every Add/Remove/Clear/enumerate of either set is serialized through this gate (#1621).
    // When a snapshot-then-act pattern is needed the snapshot is taken under the lock and the async
    // work runs outside it -- the lock is never held across an await.
    private readonly object _stateGate = new();

    public GatewayEventHandler(IClientStateStore store, GatewayHubConnection hub, ILogger<GatewayEventHandler> logger)
    {
        _store = store;
        _hub = hub;
        _logger = logger;

        _hub.OnConnected += HandleConnected;
        _hub.OnRunStarted += HandleRunStarted;
        _hub.OnMessageStart += HandleMessageStart;
        _hub.OnContentDelta += HandleContentDelta;
        _hub.OnThinkingDelta += HandleThinkingDelta;
        _hub.OnToolStart += HandleToolStart;
        _hub.OnToolEnd += HandleToolEnd;
        _hub.OnMessageEnd += HandleMessageEnd;
        _hub.OnError += HandleError;
        _hub.OnUserInputRequired += HandleUserInputRequired;
        _hub.OnTurnInterrupted += HandleTurnInterrupted;
        _hub.OnTurnEnd += HandleTurnEnd;
        _hub.OnRunEnded += HandleRunEnded;
        _hub.OnSessionReset += HandleSessionReset;
        _hub.OnSubAgentSpawned += HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted += HandleSubAgentCompleted;
        _hub.OnSubAgentFailed += HandleSubAgentFailed;
        _hub.OnSubAgentKilled += HandleSubAgentKilled;
        _hub.OnSteeringFeedback += HandleSteeringFeedback;
        _hub.OnCanvasUpdated += HandleCanvasUpdated;
        _hub.OnCanvasStateChanged += HandleCanvasStateChanged;
        _hub.OnTodoUpdated += HandleTodoUpdated;
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

    /// <summary>
    /// Handles a <c>RunStarted</c> event: the agent run loop has begun. Marks the conversation's
    /// run as active — the authoritative signal that keeps Steer/Follow Up/Stop controls visible
    /// across the entire loop, including the gaps between turns and tools where per-step events
    /// (message-end/tool-start, tool-end/tool-start, tool-end/next-message-start) leave a window.
    /// </summary>
    public void HandleRunStarted(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId) ?? agent!.ActiveConversationId;
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.IsRunActive = true;
        // #1897/streaming-flash: RunStarted fires BEFORE the first MessageStart (which is what
        // normally calls ClearBuffers). Any residual Buffer/ThinkingBuffer left over from the
        // previous turn would otherwise be painted by ChatPanel's live streaming bubble the
        // instant IsTurnActive flips true — as RAW text (the live bubble does not run the
        // Markdown pipeline), producing the reported "previous assistant message flashes as raw
        // markdown right after I send, then vanishes when the real reply streams in". Clearing
        // the buffers when the run opens closes that window; MessageStart still clears again per
        // message, so this is purely defensive and cannot drop in-flight content.
        conv.StreamState.ClearBuffers();
        agent.IsStreaming = true;
        if (string.IsNullOrEmpty(agent.ProcessingStage))
            agent.ProcessingStage = "\U0001F916 Agent is working\u2026";
        _store.NotifyChanged();
    }

    /// <summary>
    /// Handles a <c>RunEnded</c> event: the agent run loop has fully settled (final turn, last tool
    /// result, and any follow-up continuations are all done). Clears the run-active bracket and all
    /// residual streaming/tool state. This is the authoritative idle signal — unlike MessageEnd and
    /// TurnEnd, it does not flicker between turns or tools.
    /// </summary>
    public void HandleRunEnded(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;

        // #2195: RunEnded is the authoritative idle signal that clears the turn-active bracket
        // (Steer/Redirect/Follow Up/Stop). If it is missed or misrouted the input stays stuck on
        // those controls until reload. The resolved ConversationId can be a stale/misrouted hint
        // that does not map to any local conversation (GetValueOrDefault returns null), so clearing
        // ONLY that conversation would silently no-op and leave the active conversation stuck.
        // Harden the recovery: clear the resolved conversation when it exists, and always also clear
        // the agent's active conversation bracket as a fallback so a null/mismatched hint can never
        // strand the user.
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId);

        var cleared = false;
        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.StreamState.EndRun();
            cleared = convId == agent.ActiveConversationId;
        }

        // Fallback: the resolved conversation was unknown (misrouted hint) or was not the active
        // one -- clear the active conversation's bracket so the portal reliably swaps back to Send.
        if (!cleared
            && agent!.ActiveConversationId is { } activeConvId
            && agent.Conversations.GetValueOrDefault(activeConvId) is { } activeConv)
        {
            activeConv.StreamState.EndRun();
        }

        agent!.IsStreaming = false;
        agent.ProcessingStage = null;
        _store.NotifyChanged();

        // Drain any conversation-list refreshes deferred during the run (same as the MessageEnd/TurnEnd paths).
        DrainPendingConversationRefreshes(agentId!);
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
        conv.StreamState.ClearBuffers();
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

        conv.StreamState.AppendBuffer(evt.ContentDelta);
        // #1651: a non-streaming SendAsync fan-out can stamp the role the delivered content
        // should render under (e.g. an on-behalf-of-user kickoff carried as `user`). Record it
        // so the terminal flush commits the bubble under that role instead of the default
        // assistant. Absent/blank leaves any prior value untouched; the common streamed reply
        // carries no role and flushes as assistant exactly as before.
        if (!string.IsNullOrWhiteSpace(evt.Role))
            conv.StreamState.PendingRole = evt.Role;
        agent.ProcessingStage = "🤖 Agent is responding…";
        _store.NotifyChangedThrottled();
    }

    public void HandleThinkingDelta(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;
        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId);
        if (convId is null) return;

        var conv = agent!.Conversations.GetValueOrDefault(convId);
        if (conv is null) return;

        conv.StreamState.AppendThinking(evt.ThinkingContent);
        agent.ProcessingStage = "💭 Thinking…";
        _store.NotifyChangedThrottled();
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

        conv.AppendMessage(msg);
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
            // O(1) id->index lookup instead of an O(n) FindIndex on every ToolEnd (#1622).
            // A miss (id not present) leaves index at -1 and falls through to the new-message
            // fallback below -- the exact behaviour of the previous FindIndex(...) == -1 path.
            if (conv.TryGetMessageIndex(messageId, out var index))
            {
                var original = conv.Messages[index];
                conv.ReplaceMessageAt(index, original with
                {
                    Content = evt.ToolIsError == true ? $"❌ {evt.ToolName} failed" : $"✅ {evt.ToolName} completed",
                    ToolResult = AnsiStripper.Strip(evt.ToolResult),
                    ToolIsError = evt.ToolIsError,
                    ToolDuration = duration
                });

                agent.ProcessingStage = agent.IsStreaming ? "🤖 Agent is responding…" : null;
                _store.NotifyChanged();
                return;
            }
        }

        // Fallback: new message
        conv.AppendMessage(new ChatMessage("Tool",
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

    // #1651: maps a pending stream role (carried on the ContentDelta.role field) to the
    // ChatMessage.Role string ChatPanel keys assistant-vs-user rendering off. A null/blank
    // pending role -- the default for every ordinary streamed reply -- resolves to "Assistant",
    // preserving the pre-post-as-assistant behaviour. Mirrors AgentInteractionService.MapRole so
    // the live-fan-out path and the history-replay path agree on the displayed role.
    private static string ResolveFlushRole(string? pendingRole) =>
        (pendingRole ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => "Assistant",
            "user" => "User",
            "assistant" => "Assistant",
            "tool" => "Tool",
            "error" => "Error",
            "system" => "System",
            var other => other,
        };

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
            // #1651: honour a role stamped on the delivered content (via ContentDelta.role) so a
            // user-stamped agent-post flushes as a user bubble. PendingRole is null for the
            // ordinary streamed reply, defaulting to the assistant bubble as before.
            conv.AppendMessage(new ChatMessage(ResolveFlushRole(conv.StreamState.PendingRole), conv.StreamState.Buffer, DateTimeOffset.UtcNow)
            {
                ThinkingContent = thinkingContent,
                InputTokens = evt.Usage?.InputTokens,
                OutputTokens = evt.Usage?.OutputTokens,
                CacheRead = evt.Usage?.CacheRead,
                CacheWrite = evt.Usage?.CacheWrite
            });
        }

        conv.StreamState.Reset();
        agent.IsStreaming = false;
        agent.ProcessingStage = null;

        if (!isNoReply)
        {
            // Update timestamp client-side so sort order reflects recent activity (issue #382).
            // NO_REPLY turns must not bump the timestamp -- they produce no user-facing activity
            // and bumping UpdatedAt would cause the conversation to appear at the top of the list
            // with a recent timestamp, creating noise for cron wakeups that had nothing to say (#773).
            conv.UpdatedAt = DateTimeOffset.UtcNow;

            if (agent.AgentId != _store.ActiveAgentId)
                agent.UnreadCount++;

            if (convId != agent.ActiveConversationId)
                conv.UnreadCount++;
        }

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
            conv.AppendMessage(new ChatMessage("Error", evt.ErrorMessage ?? "An unknown error occurred.", DateTimeOffset.UtcNow));
            // An error is terminal for the loop (the runner emits RunEnded right after), so clear the
            // whole run bracket defensively in case the RunEnded event is missed.
            conv.StreamState.EndRun();
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
            conv.AppendMessage(new ChatMessage("Notification",
                evt.ErrorMessage ?? "The gateway was restarted while your last message was being processed.",
                DateTimeOffset.UtcNow));
            // A gateway restart terminates the run; clear the whole run bracket (no RunEnded will arrive).
            conv.StreamState.EndRun();
        }

        if (agent is not null)
        {
            agent.IsStreaming = false;
            agent.ProcessingStage = null;
        }

        _store.NotifyChanged();
    }

    /// <summary>
    /// Handles a <c>TurnEnd</c> event: the agent turn completed fully.
    /// For tool-only turns (NO_REPLY or agent runs that only call tools and return nothing),
    /// no <c>MessageEnd</c> is sent, so IsStreaming may still be true. TurnEnd is the
    /// reliable signal to clear it (#668).
    /// </summary>
    public void HandleTurnEnd(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId)) return;

        // Only take action if still streaming -- if MessageEnd already cleared it, this is a no-op.
        if (!agent.IsStreaming) return;

        var convId = ResolveConversationId(agentId!, agent!, evt.SessionId, evt.ConversationId)
            ?? agent!.ActiveConversationId;
        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            // A tool-only turn may have buffered content (e.g. a partial NO_REPLY).
            // Clear it without adding a message -- the turn produced no user-visible output.
            conv.StreamState.Reset();
        }

        agent.IsStreaming = false;
        agent.ProcessingStage = null;
        _store.NotifyChanged();

        // Drain any deferred conversation refreshes (same as MessageEnd path).
        DrainPendingConversationRefreshes(agentId!);
    }
    public void HandleUserInputRequired(AgentStreamEvent evt)
    {
        if (!ResolveAgent(evt.SessionId, out var agentId, out var agent, evt.ConversationId))
            return;

        if (!AskUserPromptFactory.TryBuildFromStreamEvent(evt, out var prompt))
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
            conv.StreamState.EndRun();
            // Do NOT clear conv.Messages or set HistoryLoaded=false.
            // Session reset clears the agent's context window — it does not
            // erase conversation history. The portal should keep showing all
            // prior messages with a visual divider marking the new session.
            conv.AppendMessage(new ChatMessage("System", "--- New session started ---", DateTimeOffset.UtcNow));
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
        _store.MarkSubAgent(payload.SubAgentId);
        _store.RegisterSession(payload.SubAgentId, payload.SubAgentId);

        if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            var taskHint = string.IsNullOrWhiteSpace(payload.Task) ? string.Empty : $" — {payload.Task}";
            conv.AppendMessage(new ChatMessage("System",
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
            conv.AppendMessage(new ChatMessage("System",
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
            conv.AppendMessage(new ChatMessage("System",
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
            conv.AppendMessage(new ChatMessage("System",
                $"⛔ Sub-agent killed: {payload.Name ?? payload.SubAgentId}",
                DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
    }

    // ── Steering feedback ──────────────────────────────────────────────────

    /// <summary>
    /// Handles steering feedback: on Injected, removes from pending queue and
    /// appends a styled stream message; on Queued, keeps entry pending.
    /// </summary>
    public void HandleSteeringFeedback(SteeringFeedbackPayload payload)
    {
        // Resolve the conversation for this feedback
        var convId = payload.ConversationId;
        AgentState? agent = null;

        if (!string.IsNullOrWhiteSpace(convId))
        {
            _store.TryResolveAgentByConversation(convId, out var agentIdFromConv);
            agent = agentIdFromConv is not null ? _store.GetAgent(agentIdFromConv) : null;
        }

        if (agent is null)
        {
            // Fallback: find agent owning this session
            agent = _store.Agents.Values.FirstOrDefault(a =>
                a.ActiveConversationSessionId == payload.SessionId ||
                a.SessionId == payload.SessionId);
        }

        if (agent is null) return;

        convId ??= agent.ActiveConversationId;
        if (convId is null || !agent.Conversations.TryGetValue(convId, out var conv)) return;

        if (payload.Kind == SteeringFeedbackKind.Injected)
        {
            // Remove the oldest pending entry from the queue (FIFO)
            var pendingEntry = conv.PendingSteeringQueue.FirstOrDefault(e => e.Status == SteeringEntryStatus.Pending);
            if (pendingEntry is not null)
            {
                _store.UpdateSteeringEntry(convId, pendingEntry.Id, SteeringEntryStatus.Injected);
            }

            // Add styled stream message
            conv.AppendMessage(new ChatMessage("System", "\u21b3 Steering injected", DateTimeOffset.UtcNow));
        }
        else
        {
            // Queued: keep as pending, add informational message
            conv.AppendMessage(new ChatMessage("System", "\u21b3 Steering queued \u2014 will process next turn", DateTimeOffset.UtcNow));
        }

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

    public void HandleCanvasStateChanged(string conversationId, string key, object? value)
    {
        // Notify store that canvas state changed — the CanvasPanel will re-read state.
        // The actual state is fetched from the REST API by the iframe SDK; we just trigger
        // the re-render so the portal can forward the notification to the iframe.
        _store.NotifyChanged();
    }

    /// <summary>
    /// Applies a live per-conversation todo update (#1464 step 5). Routes the raw TodoJson to the
    /// target conversation so the Todo panel re-renders without a manual refresh. Mirrors
    /// <see cref="HandleCanvasUpdated"/>.
    /// </summary>
    public void HandleTodoUpdated(string agentId, string conversationId, string? todoJson)
    {
        var agent = string.IsNullOrWhiteSpace(agentId) ? null : _store.GetAgent(agentId);
        if (agent is null)
            return;

        var conv = string.IsNullOrWhiteSpace(conversationId)
            ? null
            : agent.Conversations.GetValueOrDefault(conversationId);
        if (conv is null)
        {
            // Unknown conversation — nothing to update.
            _store.NotifyChanged();
            return;
        }

        conv.TodoJson = string.IsNullOrWhiteSpace(todoJson) ? null : todoJson;
        conv.TodoUpdatedAt = DateTimeOffset.UtcNow;
        _store.NotifyChanged();
    }

    // ── Connection lifecycle ──────────────────────────────────────────────

    public void HandleReconnecting()
    {
        foreach (var agent in _store.Agents.Values)
        {
            if (agent.IsStreaming)
            {
                lock (_stateGate)
                    _streamingWhenDisconnected.Add(agent.AgentId);
            }
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
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType, session.ConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect recovery (SubscribeAll) failed");
        }

        // Clear stale streaming state unconditionally (even if SubscribeAllAsync failed).
        // The portal must never get stuck in a perpetual streaming indicator (#759).
        // Snapshot + clear the shared set under the lock, then apply the store mutations OUTSIDE the
        // lock so we never hold _stateGate across the per-agent work (#1621).
        string[] staleStreamingAgents;
        lock (_stateGate)
        {
            staleStreamingAgents = _streamingWhenDisconnected.ToArray();
            _streamingWhenDisconnected.Clear();
        }

        foreach (var agentId in staleStreamingAgents)
        {
            if (_store.GetAgent(agentId) is { } agent)
            {
                agent.IsStreaming = false;
                agent.ProcessingStage = null;

                if (agent.ActiveConversationId is not null &&
                    agent.Conversations.GetValueOrDefault(agent.ActiveConversationId) is { } conv)
                {
                    conv.StreamState.Reset();
                    // Force history reload so the UI fetches server-persisted state.
                    // HandleMessageEnd never committed the buffer to Messages (#759).
                    conv.HistoryLoaded = false;
                }
            }
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

    // ── Hub event wiring (called when hub reconnects) ─────────────────────

    private void HandleReconnected() => SafeFireAsync(() => HandleReconnectedAsync(), "reconnect-recovery");

    // Launches fire-and-forget async work from a synchronous SignalR callback while OBSERVING the
    // resulting Task: any exception (synchronous or faulted-task) is caught and logged instead of
    // becoming an unobserved task exception that silently vanishes (#1621). The happy path is
    // unchanged -- observation and logging only happen on failure. The launching callback never sees
    // the exception, so a failing reconnect/refresh cannot tear down the hub callback thread.
    private void SafeFireAsync(Func<Task> work, string context)
    {
        _ = RunObservedAsync(work, context);

        async Task RunObservedAsync(Func<Task> inner, string ctx)
        {
            try
            {
                await inner().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fire-and-forget operation failed: {Context}", ctx);
            }
        }
    }

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
            lock (_stateGate)
                _pendingConversationRefresh.Add(agentId);
            return;
        }

        SafeFireAsync(() => RefreshConversationsAsync(agentId), "conversation-refresh");
    }

    // Injected async refresh delegate — wired by AgentInteractionService or PortalLoadService.
    public Func<string, Task>? ConversationRefreshDelegate { get; set; }

    private Task RefreshConversationsAsync(string agentId)
        => ConversationRefreshDelegate?.Invoke(agentId) ?? Task.CompletedTask;

    private void DrainPendingConversationRefreshes(string agentId)
    {
        bool wasPending;
        lock (_stateGate)
            wasPending = _pendingConversationRefresh.Remove(agentId);

        if (!wasPending)
            return;

        SafeFireAsync(() => RefreshConversationsAsync(agentId), "conversation-refresh-drain");
    }

    public void Dispose()
    {
        _hub.OnConnected -= HandleConnected;
        _hub.OnRunStarted -= HandleRunStarted;
        _hub.OnMessageStart -= HandleMessageStart;
        _hub.OnContentDelta -= HandleContentDelta;
        _hub.OnThinkingDelta -= HandleThinkingDelta;
        _hub.OnToolStart -= HandleToolStart;
        _hub.OnToolEnd -= HandleToolEnd;
        _hub.OnMessageEnd -= HandleMessageEnd;
        _hub.OnError -= HandleError;
        _hub.OnUserInputRequired -= HandleUserInputRequired;
        _hub.OnTurnInterrupted -= HandleTurnInterrupted;
        _hub.OnTurnEnd -= HandleTurnEnd;
        _hub.OnRunEnded -= HandleRunEnded;
        _hub.OnSessionReset -= HandleSessionReset;
        _hub.OnSubAgentSpawned -= HandleSubAgentSpawned;
        _hub.OnSubAgentCompleted -= HandleSubAgentCompleted;
        _hub.OnSubAgentFailed -= HandleSubAgentFailed;
        _hub.OnSubAgentKilled -= HandleSubAgentKilled;
        _hub.OnSteeringFeedback -= HandleSteeringFeedback;
        _hub.OnCanvasUpdated -= HandleCanvasUpdated;
        _hub.OnCanvasStateChanged -= HandleCanvasStateChanged;
        _hub.OnTodoUpdated -= HandleTodoUpdated;
        _hub.OnConversationChanged -= HandleConversationChanged;
                _hub.OnReconnecting -= HandleReconnecting;
        _hub.OnReconnected -= HandleReconnected;
        _hub.OnDisconnected -= HandleDisconnected;
    }
}


