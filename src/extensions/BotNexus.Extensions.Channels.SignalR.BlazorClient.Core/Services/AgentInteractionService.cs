using System.Net;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Thin action layer for UI components — sends messages, steers, resets sessions,
/// manages conversations. All state mutations flow via <see cref="IClientStateStore"/>.
/// </summary>
public sealed class AgentInteractionService : IAgentInteractionService
{
    private readonly IClientStateStore _store;
    private readonly GatewayHubConnection _hub;
    private readonly IGatewayRestClient _restClient;

    public AgentInteractionService(IClientStateStore store, GatewayHubConnection hub, IGatewayRestClient restClient)
    {
        _store = store;
        _hub = hub;
        _restClient = restClient;
    }

    // ── Messaging ─────────────────────────────────────────────────────────

    public async Task SendMessageAsync(string agentId, string content)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;

        // Ensure we have an active conversation
        if (agent.ActiveConversationId is null)
        {
            var convId = await CreateConversationAsync(agentId, title: null, select: true);
            if (convId is null)
            {
                AppendError(agentId, "Failed to create conversation before sending.");
                return;
            }
            agent = _store.GetAgent(agentId)!;
        }

        var convIdNow = agent.ActiveConversationId!;
        var conv = _store.GetConversation(convIdNow);
        if (conv is null) return;

        conv.Messages.Add(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        _store.NotifyChanged();

        try
        {
            // Always pass the conversation ID — the router handles direct lookup without binding scan.
            // Removed the IsDefault special-case that previously caused duplicate thread bindings and double fan-out.
            var result = await _hub.SendMessageAsync(agentId, agent.ChannelType ?? "signalr", content, convIdNow);
            _store.RegisterSession(agentId, result.SessionId, result.ChannelType);

            // Refresh conversation so ActiveSessionId is current
            await RefreshConversationsForAgentAsync(agentId);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Send failed: {ex.Message}");
        }
    }

    public async Task SteerAsync(string agentId, string content)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        AppendUserMessage(agentId, $"🔀 {content}");

        try
        {
            var result = await _hub.SteerAsync(agentId, agent.ActiveConversationSessionId!, content, agent.ActiveConversationId);
            _store.RegisterSession(agentId, result.SessionId, result.ChannelType);
            await RefreshConversationsForAgentAsync(agentId);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Steer failed: {ex.Message}");
        }
    }

    public async Task FollowUpAsync(string agentId, string content)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        AppendUserMessage(agentId, content);

        try
        {
            await _hub.FollowUpAsync(agentId, agent.ActiveConversationSessionId!, content);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Follow-up failed: {ex.Message}");
        }
    }

    public async Task AbortAsync(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        try
        {
            await _hub.AbortAsync(agentId, agent.ActiveConversationSessionId!);
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Abort failed: {ex.Message}");
        }
    }

    // ── Session management ────────────────────────────────────────────────


    public async Task InterruptAndSteerAsync(string agentId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        AppendUserMessage(agentId, "[redirect] " + message);

        try
        {
            var delivered = await _hub.InterruptAndSteerAsync(agentId, agent.ActiveConversationSessionId!, message);
            if (!delivered)
                AppendError(agentId, "Interrupt not delivered - agent was not running.");
        }
        catch (Exception ex)
        {
            AppendError(agentId, "Interrupt and steer failed: " + ex.Message);
        }
    }
    public async Task ResetSessionAsync(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        try
        {
            await _hub.ResetSessionAsync(agentId, agent.ActiveConversationSessionId!);
            // Server will send SessionReset event; GatewayEventHandler handles it
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Reset failed: {ex.Message}");
        }
    }

    public async Task<CompactSessionResult?> CompactSessionAsync(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return null;

        try
        {
            var result = await _hub.CompactSessionAsync(agentId, agent.ActiveConversationSessionId!);
            // Inject the canonical compaction notification locally so the user
            // sees the same _[Session context compacted: ...]_ text regardless of
            // which path (auto-compact via channel push, or this RPC) triggered
            // compaction. The gateway no longer fans this out via the SignalR
            // channel for hub-initiated compactions, so we render it here.
            var convId = agent.ActiveConversationId;
            if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
            {
                conv.Messages.Add(new ChatMessage("System",
                    $"_[Session context compacted: {result.Summarized} older messages summarised, {result.Preserved} recent messages preserved. Continuing…]_",
                    DateTimeOffset.UtcNow));
                _store.NotifyChanged();
            }

            return result;
        }
        catch (Exception ex)
        {
            AppendError(agentId, $"Compact failed: {ex.Message}");
            return null;
        }
    }

    // ── Conversation management ───────────────────────────────────────────

    public async Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return null;

        try
        {
            var request = new CreateConversationRequestDto(agentId, title);
            var dto = await _restClient.CreateConversationAsync(request);
            if (dto is null) return null;

            agent.Conversations[dto.ConversationId] = new ConversationState
            {
                ConversationId = dto.ConversationId,
                Title = dto.Title,
                IsDefault = dto.IsDefault,
                Status = dto.Status,
                ActiveSessionId = dto.ActiveSessionId,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt,
                HistoryLoaded = true // brand new — nothing to load
            };

            if (select)
                _store.SetActiveConversation(agentId, dto.ConversationId);
            else
                _store.NotifyChanged();

            return dto.ConversationId;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: CreateConversation failed for {agentId}: {ex.Message}");
            return null;
        }
    }

    public async Task SelectConversationAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (!agent.Conversations.ContainsKey(conversationId)) return;

        _store.SetActiveConversation(agentId, conversationId);

        var conv = agent.Conversations.GetValueOrDefault(conversationId);

        // Fetch canvas from REST if not already loaded (Phase 3, #413)
        if (conv is not null && conv.CanvasHtml is null)
        {
            try
            {
                var canvasHtml = await _restClient.GetConversationCanvasAsync(agentId, conversationId);
                if (canvasHtml is not null)
                {
                    conv.CanvasHtml = canvasHtml;
                    conv.CanvasUpdatedAt = DateTimeOffset.UtcNow;
                    _store.NotifyChanged();
                }
            }
            catch { /* canvas fetch is best-effort */ }
        }

        // Fix #789: if the conversation was streaming when the user navigated away, a
        // terminal SignalR event (MessageEnd/Error/TurnInterrupted) may have been missed.
        // Reset stale streaming state and force a history reload so the UI reflects the
        // actual server-side turn result rather than a perpetual in-progress spinner.
        if (conv is not null && conv.StreamState.IsStreaming)
        {
            conv.StreamState.IsStreaming = false;
            conv.HistoryLoaded = false; // force reload below
        }

        // Load history if not already loaded
        if (conv is not null && !conv.HistoryLoaded && !conv.IsLoadingHistory)
            await LoadConversationHistoryAsync(agentId, conversationId);
    }

    public async Task RenameConversationAsync(string agentId, string? conversationId, string newTitle)
    {
        if (conversationId is null) return;
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        if (!agent.Conversations.TryGetValue(conversationId, out var conv)) return;

        try
        {
            await _restClient.RenameConversationAsync(conversationId, newTitle);
            conv.Title = newTitle;
            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: RenameConversation failed: {ex.Message}");
        }
    }

    public async Task ArchiveConversationAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        if (!agent.Conversations.TryGetValue(conversationId, out var conversation)) return;

        try
        {
            // All conversation cleanup — including virtual cron projections — routes through
            // DELETE /api/conversations/{conversationId}. The backend handles cron-session: IDs
            // idempotently (returns 204 even if no backing session exists), preserving session
            // records while hiding the conversation from the sidebar.
            var success = await _restClient.ArchiveConversationAsync(conversationId);

            if (!success)
            {
                Console.Error.WriteLine($"AgentInteractionService: Conversation cleanup returned failure for {conversationId}");
                return;
            }

            agent.Conversations.Remove(conversationId);

            // If this was the active conversation, switch to the next available or clear
            if (agent.ActiveConversationId == conversationId)
            {
                var next = agent.Conversations.Keys.FirstOrDefault();
                if (next is not null)
                {
                    _store.SetActiveConversation(agentId, next);
                }
                else
                {
                    agent.ActiveConversationId = null;
                    _store.NotifyChanged();
                }
            }
            else
            {
                _store.NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: ArchiveConversation failed: {ex.Message}");
        }
    }

    public async Task RefreshAgentsAsync()
    {
        try
        {
            var agents = await _restClient.GetAgentsAsync();
            foreach (var agent in agents)
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
                }
            }

            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: RefreshAgents failed: {ex.Message}");
        }
    }

    public async Task RefreshConversationsAsync(string agentId)
    {
        await RefreshConversationsForAgentAsync(agentId);
    }

    public async Task ViewSubAgentAsync(SubAgentInfo subAgent)
    {
        var subAgentId = subAgent.SubAgentId;

        var childSessionId = subAgent.ChildSessionId ?? subAgentId;
        if (!_store.Agents.ContainsKey(subAgentId))
        {
            _store.UpsertAgent(new AgentState
            {
                AgentId = subAgentId,
                DisplayName = subAgent.Name ?? $"Sub-agent {subAgentId[..Math.Min(8, subAgentId.Length)]}",
                SessionId = childSessionId,
                SessionType = "agent-subagent",
                IsConnected = true
            });
            _store.RegisterSession(subAgentId, childSessionId);
        }

        _store.ActiveAgentId = subAgentId;
        _store.NotifyChanged();

        // Load history if needed
        var agent = _store.GetAgent(subAgentId)!;
        var convId = agent.ActiveConversationId;
        if (convId is null || (agent.Conversations.GetValueOrDefault(convId) is { } conv && !conv.HistoryLoaded))
        {
            // Sub-agent sessions are loaded by session ID
            await LoadSubAgentHistoryAsync(subAgentId);
        }
    }

    public async Task RespondToAskUserAsync(
        string conversationId,
        string requestId,
        string? freeFormText,
        string[]? selectedValues,
        bool cancelled)
    {
        await _hub.RespondToAskUserAsync(
            conversationId,
            requestId,
            freeFormText,
            selectedValues,
            cancelled);
    }

    public void ClearLocalMessages(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationId is null) return;

        var conv = agent.Conversations.GetValueOrDefault(agent.ActiveConversationId);
        if (conv is null) return;

        conv.Messages.Clear();
        conv.HistoryLoaded = false;
        conv.Messages.Add(new ChatMessage("System", "Local messages cleared.", DateTimeOffset.UtcNow));
        _store.NotifyChanged();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task LoadConversationHistoryAsync(string agentId, string conversationId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent is null) return;
        var conv = agent.Conversations.GetValueOrDefault(conversationId);
        if (conv is null || conv.IsLoadingHistory) return;
        if (conv.HistoryLoaded)
            return; // Already loaded from server — don't reload

        // Guard: never load history while the conversation is mid-stream.
        // Messages.Clear() would wipe tool-call messages and the partially-built
        // assistant buffer that HandleContentDelta is accumulating. The deferred
        // refresh mechanism (DrainPendingConversationRefreshes) handles post-stream
        // updates; attempting a load here would lose visible streamed text (#759).
        if (conv.StreamState.IsStreaming)
            return;

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();

        try
        {
            if (conv.IsVirtualSession && conv.ActiveSessionId is { Length: > 0 } sessionId)
            {
                const int virtualHistoryLimit = 200;
                var sessionResponse = await _restClient.GetSessionHistoryAsync(sessionId, limit: virtualHistoryLimit);
                conv.Messages.Clear();
                if (sessionResponse?.Entries is { Count: > 0 })
                {
                    foreach (var entry in sessionResponse.Entries)
                    {
                        var role = MapRole(entry.Role ?? "system");
                        conv.Messages.Add(new ChatMessage(role, entry.Content ?? string.Empty, entry.Timestamp)
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

                conv.HistoryLoaded = true;
                return;
            }

            const int historyLimit = 200;
            var response = await _restClient.GetHistoryAsync(conversationId, limit: historyLimit);

            if (response?.Entries is { Count: > 0 })
            {
                conv.Messages.Clear();

                foreach (var entry in response.Entries)
                {
                    if (entry.Kind == "boundary")
                    {
                        var label = $"Session · {entry.Timestamp.ToLocalTime():MMM d HH:mm} · {entry.SessionId}";
                        conv.Messages.Add(new ChatMessage("System", string.Empty, entry.Timestamp)
                        {
                            Kind = "boundary",
                            BoundaryLabel = label,
                            BoundarySessionId = entry.SessionId
                        });
                    }
                    else if (entry.Kind == "compaction")
                    {
                        var label = "Context compacted \u00b7 " + entry.Timestamp.ToLocalTime().ToString("MMM d HH:mm");
                        conv.Messages.Add(new ChatMessage("System", entry.Content ?? string.Empty, entry.Timestamp)
                        {
                            Kind = "compaction",
                            BoundaryLabel = label,
                            BoundarySessionId = entry.SessionId
                        });
                    }
                    else
                    {
                        var isToolCall = entry.ToolName is not null;
                        conv.Messages.Add(new ChatMessage(
                            MapRole(entry.Role ?? "system"),
                            entry.Content ?? string.Empty,
                            entry.Timestamp)
                        {
                            ToolName = entry.ToolName,
                            ToolCallId = entry.ToolCallId,
                            IsToolCall = isToolCall,
                            ToolResult = isToolCall ? AnsiStripper.Strip(entry.Content) : null,
                            ToolArgs = entry.ToolArgs,
                            ToolIsError = entry.ToolIsError
                        });
                    }
                }
            }

            conv.HistoryLoaded = true;

            // Sync session ID
            if (agent.ActiveConversationId == conversationId && conv.ActiveSessionId is not null)
                agent.SessionId = conv.ActiveSessionId;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Console.Error.WriteLine($"AgentInteractionService: History 404 for conversation {conversationId}: {ex.Message}");
            agent.Conversations.Remove(conversationId);
            if (agent.ActiveConversationId == conversationId)
            {
                var nextConversationId = agent.Conversations.Values
                    .OrderByDescending(c => c.IsDefault)
                    .ThenByDescending(c => c.UpdatedAt)
                    .Select(c => c.ConversationId)
                    .FirstOrDefault();
                if (nextConversationId is not null)
                {
                    _store.SetActiveConversation(agentId, nextConversationId);
                }
                else
                {
                    agent.ActiveConversationId = null;
                    _store.NotifyChanged();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: LoadHistory failed for {conversationId}: {ex.Message}");
            conv.HistoryLoaded = true; // don't retry
        }
        finally
        {
            conv.IsLoadingHistory = false;
            _store.NotifyChanged();
        }
    }

    private async Task LoadSubAgentHistoryAsync(string subAgentId)
    {
        // Sub-agents use session history endpoint, not conversation history
        var agent = _store.GetAgent(subAgentId);
        if (agent is null) return;

        // Create a virtual conversation for the sub-agent session
        var convId = $"subagent-session:{subAgentId}";
        if (!agent.Conversations.TryGetValue(convId, out var conv))
        {
            conv = new ConversationState
            {
                ConversationId = convId,
                Title = "Sub-agent session",
                Status = "Active",
                ActiveSessionId = subAgentId,
                IsVirtualSession = true,
                VirtualSessionKind = "subagent",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HistoryLoaded = false
            };
            agent.Conversations[convId] = conv;
        }

        // Keep ActiveSessionId in sync with the actual child session ID
        conv.ActiveSessionId = agent.SessionId ?? subAgentId;
        agent.ActiveConversationId = convId;
        if (conv.HistoryLoaded || conv.IsLoadingHistory) return;

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();

        try
        {
            // Use session history endpoint for sub-agent session transcripts.
            // Use the child session ID (from SubAgentInfo.ChildSessionId propagated via AgentState.SessionId)
            // rather than the ephemeral sub-agent run ID to avoid 404s.
            var sessionIdForHistory = agent.SessionId ?? subAgentId;
            var response = await _restClient.GetSessionHistoryAsync(sessionIdForHistory, limit: 50);
            conv.Messages.Clear();
            if (response?.Entries is { Count: > 0 })
            {
                foreach (var entry in response.Entries)
                {
                    conv.Messages.Add(new ChatMessage(
                        MapRole(entry.Role ?? "system"),
                        entry.Content ?? string.Empty,
                        entry.Timestamp)
                    {
                        ToolName = entry.ToolName,
                        ToolCallId = entry.ToolCallId,
                        ToolArgs = entry.ToolArgs,
                        ToolIsError = entry.ToolIsError,
                        ToolResult = entry.ToolName is not null ? AnsiStripper.Strip(entry.Content) : null,
                        IsToolCall = entry.ToolName is not null
                    });
                }
            }

            conv.HistoryLoaded = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: LoadSubAgentHistory failed for {subAgentId}: {ex.Message}");
            conv.HistoryLoaded = true;
        }
        finally
        {
            conv.IsLoadingHistory = false;
            _store.NotifyChanged();
        }
    }

    private async Task RefreshConversationsForAgentAsync(string agentId)
    {
        try
        {
            var listTask = _restClient.GetConversationsAsync(agentId);
            var sessionsTask = _restClient.GetSessionsAsync(agentId);
            await Task.WhenAll(listTask, sessionsTask);

            var list = listTask.Result;
            _store.SeedConversations(agentId, list);

            foreach (var session in sessionsTask.Result)
                _store.RegisterSession(session.AgentId, session.SessionId, session.ChannelType, session.SessionType);

            // Fetch canvas for the auto-selected conversation on initial load (#383)
            var agent = _store.GetAgent(agentId);
            var activeConvId = agent?.ActiveConversationId;
            if (activeConvId is not null && agent!.Conversations.TryGetValue(activeConvId, out var activeConv))
            {
                try
                {
                    var canvasHtml = await _restClient.GetConversationCanvasAsync(agentId, activeConvId);
                    if (canvasHtml is not null)
                    {
                        activeConv.CanvasHtml = canvasHtml;
                        activeConv.CanvasUpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                catch { /* canvas fetch is best-effort */ }
            }

            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: RefreshConversations failed for {agentId}: {ex.Message}");
        }
    }

    private void AppendUserMessage(string agentId, string content)
    {
        var agent = _store.GetAgent(agentId);
        var convId = agent?.ActiveConversationId;
        if (convId is null || agent!.Conversations.GetValueOrDefault(convId) is not { } conv) return;

        conv.Messages.Add(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        _store.NotifyChanged();
    }

    private void AppendError(string agentId, string message)
    {
        var agent = _store.GetAgent(agentId);
        var convId = agent?.ActiveConversationId;
        if (convId is not null && agent!.Conversations.GetValueOrDefault(convId) is { } conv)
        {
            conv.Messages.Add(new ChatMessage("Error", message, DateTimeOffset.UtcNow));
        }

        _store.NotifyChanged();
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
