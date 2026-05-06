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
    private readonly ConversationHistoryCache? _cache;
    private readonly FeatureFlagsService? _featureFlags;

    public AgentInteractionService(IClientStateStore store, GatewayHubConnection hub, IGatewayRestClient restClient)
    {
        _store = store;
        _hub = hub;
        _restClient = restClient;
    }

    /// <summary>
    /// Extended constructor used when the conversation history cache is enabled.
    /// Injects the cache and feature-flag services required by <see cref="LoadConversationHistoryAsync"/>.
    /// </summary>
    public AgentInteractionService(IClientStateStore store, GatewayHubConnection hub, IGatewayRestClient restClient,
        ConversationHistoryCache cache, FeatureFlagsService featureFlags)
        : this(store, hub, restClient)
    {
        _cache = cache;
        _featureFlags = featureFlags;
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
        var conv = agent.Conversations.GetValueOrDefault(convIdNow);
        if (conv is null) return;

        conv.Messages.Add(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        _store.NotifyChanged();

        try
        {
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
            var result = await _hub.SteerAsync(agentId, agent.ActiveConversationSessionId!, content);
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

    public async Task ResetSessionAsync(string agentId)
    {
        var agent = _store.GetAgent(agentId);
        if (agent?.ActiveConversationSessionId is null) return;

        // Invalidate cached history so the reset conversation doesn't surface stale messages
        if (_featureFlags?.ConversationHistoryCache == true && _cache is not null &&
            agent.ActiveConversationId is { } convId)
        {
            await _cache.InvalidateAsync(convId);
        }

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
            var convId = agent.ActiveConversationId;
            if (convId is not null && agent.Conversations.GetValueOrDefault(convId) is { } conv)
            {
                conv.Messages.Add(new ChatMessage("System",
                    $"Session compacted: {result.Summarized} messages summarized, {result.Preserved} preserved. " +
                    $"Tokens: {result.TokensBefore} → {result.TokensAfter}",
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

        // Load history if not already loaded
        var conv = agent.Conversations.GetValueOrDefault(conversationId);
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
        if (!agent.Conversations.ContainsKey(conversationId)) return;

        try
        {
            var success = await _restClient.ArchiveConversationAsync(conversationId);
            if (!success)
            {
                Console.Error.WriteLine($"AgentInteractionService: ArchiveConversation returned failure for {conversationId}");
                return;
            }

            agent.Conversations.Remove(conversationId);

            // If this was the active conversation, switch to the next available or clear
            if (agent.ActiveConversationId == conversationId)
            {
                var next = agent.Conversations.Keys.FirstOrDefault();
                _store.SetActiveConversation(agentId, next);
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
                        IsConnected = true
                    });
                }
                else if (_store.GetAgent(agent.AgentId) is { } existing)
                {
                    existing.DisplayName = agent.DisplayName;
                }
            }

            _store.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AgentInteractionService: RefreshAgents failed: {ex.Message}");
        }
    }

    public async Task ViewSubAgentAsync(SubAgentInfo subAgent)
    {
        var subAgentId = subAgent.SubAgentId;

        if (!_store.Agents.ContainsKey(subAgentId))
        {
            _store.UpsertAgent(new AgentState
            {
                AgentId = subAgentId,
                DisplayName = subAgent.Name ?? $"Sub-agent {subAgentId[..Math.Min(8, subAgentId.Length)]}",
                SessionId = subAgentId,
                SessionType = "agent-subagent",
                IsConnected = true
            });
            _store.RegisterSession(subAgentId, subAgentId);
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
        if (conv.Messages.Count > 0)
        {
            conv.HistoryLoaded = true;
            return;
        }

        // Cache hit: render immediately, then fall through to refresh from server
        if (_featureFlags?.ConversationHistoryCache == true && _cache is not null)
        {
            var cached = await _cache.GetAsync(conversationId);
            if (cached is { Messages.Count: > 0 })
            {
                foreach (var msg in cached.Messages)
                    conv.Messages.Add(msg);
                conv.HistoryLoaded = true;
                _store.NotifyChanged();
                // Don't return — fall through to refresh cache from server
            }
        }

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();

        try
        {
            var response = await _restClient.GetHistoryAsync(conversationId, limit: 200);
            if (response?.Entries is { Count: > 0 })
            {
                // Replace any cache-rendered messages with fresh server data
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
                            ToolResult = isToolCall ? entry.Content : null,
                            ToolArgs = entry.ToolArgs,
                            ToolIsError = entry.ToolIsError
                        });
                    }
                }
            }

            conv.HistoryLoaded = true;

            // Write refreshed history to cache
            if (_featureFlags?.ConversationHistoryCache == true && _cache is not null)
                await _cache.SetAsync(conversationId, conv.Messages.ToList());

            // Sync session ID
            if (agent.ActiveConversationId == conversationId && conv.ActiveSessionId is not null)
                agent.SessionId = conv.ActiveSessionId;
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
        // For now create a stub conversation to hold messages
        var agent = _store.GetAgent(subAgentId);
        if (agent is null) return;

        // Create a virtual conversation for the sub-agent session
        const string convId = "subagent-session";
        if (!agent.Conversations.ContainsKey(convId))
        {
            agent.Conversations[convId] = new ConversationState
            {
                ConversationId = convId,
                Title = "Sub-agent session",
                HistoryLoaded = false
            };
            agent.ActiveConversationId = convId;
        }

        var conv = agent.Conversations[convId];
        if (conv.HistoryLoaded || conv.IsLoadingHistory) return;

        conv.IsLoadingHistory = true;
        _store.NotifyChanged();

        try
        {
            // Use session history endpoint
            var response = await _restClient.GetHistoryAsync(subAgentId, limit: 50);
            if (response?.Entries is { Count: > 0 })
            {
                foreach (var entry in response.Entries.Where(e => e.Kind != "boundary"))
                {
                    conv.Messages.Add(new ChatMessage(
                        MapRole(entry.Role ?? "system"),
                        entry.Content ?? string.Empty,
                        entry.Timestamp)
                    {
                        ToolName = entry.ToolName,
                        ToolCallId = entry.ToolCallId,
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
            var list = await _restClient.GetConversationsAsync(agentId);
            _store.SeedConversations(agentId, list);
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
