using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Manages per-agent session state and routes SignalR hub events to the correct agent.
/// No global "current session" — each chat panel handles its own state independently.
/// Multiple agents can stream simultaneously; each buffers independently.
/// </summary>
public sealed class AgentSessionManager : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly GatewayHubConnection _hub;
    private readonly HttpClient _http;
    private readonly Dictionary<string, AgentSessionState> _sessions = new();
    private readonly Dictionary<string, string> _sessionToAgent = new(); // sessionId → agentId
    private readonly HashSet<string> _streamingWhenDisconnected = new();

    private string? _apiBaseUrl;

    /// <summary>Raised when any agent's state changes. UI components use this to trigger re-render.</summary>
    public event Action? OnStateChanged;

    /// <summary>All per-agent session states, keyed by agent ID.</summary>
    public IReadOnlyDictionary<string, AgentSessionState> Sessions => _sessions;

    /// <summary>The underlying hub connection for status checks.</summary>
    public GatewayHubConnection Hub => _hub;

    /// <summary>The currently active/visible agent tab. Used for unread tracking.</summary>
    public string? ActiveAgentId { get; private set; }

    public AgentSessionManager(GatewayHubConnection hub, HttpClient http)
    {
        _hub = hub;
        _http = http;
        _hub.OnConnected += HandleConnected;
        _hub.OnMessageStart += HandleMessageStart;
        _hub.OnContentDelta += HandleContentDelta;
        _hub.OnToolStart += HandleToolStart;
        _hub.OnToolEnd += HandleToolEnd;
        _hub.OnMessageEnd += HandleMessageEnd;
        _hub.OnError += HandleError;
        _hub.OnSessionReset += HandleSessionReset;
        _hub.OnReconnecting += HandleReconnecting;
        _hub.OnReconnected += HandleReconnected;
        _hub.OnDisconnected += HandleDisconnected;
    }

    /// <summary>
    /// Connects to the hub and subscribes to all active sessions.
    /// Creates an <see cref="AgentSessionState"/> for each agent reported by the server.
    /// </summary>
    public async Task InitializeAsync(string hubUrl)
    {
        _apiBaseUrl = new Uri(new Uri(hubUrl), "/api/").ToString();
        await _hub.ConnectAsync(hubUrl);

        // SubscribeAll returns existing sessions — map them to agents
        var result = await _hub.SubscribeAllAsync();
        foreach (var session in result.Sessions)
        {
            RegisterSession(session.AgentId, session.SessionId, session.ChannelType);
        }
    }

    /// <summary>Set the active agent tab, clear its unread count, and load history if needed.</summary>
    public async Task SetActiveAgentAsync(string? agentId)
    {
        ActiveAgentId = agentId;
        if (agentId is not null && _sessions.TryGetValue(agentId, out var state))
        {
            state.UnreadCount = 0;

            // Load history on first visit when a session exists but we have no local messages
            if (!state.HistoryLoaded && !state.IsLoadingHistory)
            {
                if (state.SessionId is not null && state.Messages.Count == 0)
                    await LoadHistoryAsync(agentId);
                else
                    state.HistoryLoaded = true;
            }
        }

        OnStateChanged?.Invoke();
    }

    /// <summary>Send a message to the specified agent, creating a session if needed.</summary>
    public async Task SendMessageAsync(string agentId, string content)
    {
        if (!_sessions.TryGetValue(agentId, out var state))
            return;

        state.Messages.Add(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        OnStateChanged?.Invoke();

        try
        {
            var result = await _hub.SendMessageAsync(agentId, state.ChannelType ?? "signalr", content);
            RegisterSession(agentId, result.SessionId, result.ChannelType);
        }
        catch (Exception ex)
        {
            state.Messages.Add(new ChatMessage("Error", $"Send failed: {ex.Message}", DateTimeOffset.UtcNow));
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Steer an in-progress agent response.</summary>
    public async Task SteerAsync(string agentId, string content)
    {
        if (!_sessions.TryGetValue(agentId, out var state) || state.SessionId is null)
            return;

        state.Messages.Add(new ChatMessage("User", $"🔀 {content}", DateTimeOffset.UtcNow));
        OnStateChanged?.Invoke();

        try
        {
            await _hub.SteerAsync(agentId, state.SessionId, content);
        }
        catch (Exception ex)
        {
            state.Messages.Add(new ChatMessage("Error", $"Steer failed: {ex.Message}", DateTimeOffset.UtcNow));
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Send a follow-up message into an existing session.</summary>
    public async Task FollowUpAsync(string agentId, string content)
    {
        if (!_sessions.TryGetValue(agentId, out var state) || state.SessionId is null)
            return;

        state.Messages.Add(new ChatMessage("User", content, DateTimeOffset.UtcNow));
        OnStateChanged?.Invoke();

        try
        {
            await _hub.FollowUpAsync(agentId, state.SessionId, content);
        }
        catch (Exception ex)
        {
            state.Messages.Add(new ChatMessage("Error", $"Follow-up failed: {ex.Message}", DateTimeOffset.UtcNow));
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Abort an in-progress agent response.</summary>
    public async Task AbortAsync(string agentId)
    {
        if (!_sessions.TryGetValue(agentId, out var state) || state.SessionId is null)
            return;

        try
        {
            await _hub.AbortAsync(agentId, state.SessionId);
        }
        catch (Exception ex)
        {
            state.Messages.Add(new ChatMessage("Error", $"Abort failed: {ex.Message}", DateTimeOffset.UtcNow));
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Reset (archive) the agent's current session.</summary>
    public async Task ResetSessionAsync(string agentId)
    {
        if (!_sessions.TryGetValue(agentId, out var state) || state.SessionId is null)
            return;

        try
        {
            await _hub.ResetSessionAsync(agentId, state.SessionId);
            // The server will send a SessionReset event that HandleSessionReset processes.
        }
        catch (Exception ex)
        {
            state.Messages.Add(new ChatMessage("Error", $"Reset failed: {ex.Message}", DateTimeOffset.UtcNow));
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Load message history from the REST API for the given agent.</summary>
    public async Task LoadHistoryAsync(string agentId)
    {
        if (!_sessions.TryGetValue(agentId, out var state))
            return;
        if (state.HistoryLoaded || state.IsLoadingHistory)
            return;

        state.IsLoadingHistory = true;
        OnStateChanged?.Invoke();

        try
        {
            var channelType = state.ChannelType ?? "signalr";
            var url = $"{_apiBaseUrl}channels/{channelType}/agents/{agentId}/history?limit=50";
            var response = await _http.GetFromJsonAsync<HistoryResponse>(url);

            if (response?.Messages is { Count: > 0 })
            {
                state.Messages.Clear();
                // API returns messages in reverse chronological order — reverse to chronological
                foreach (var msg in response.Messages.Reverse())
                {
                    state.Messages.Add(new ChatMessage(
                        MapRole(msg.Role),
                        msg.Content,
                        msg.Timestamp)
                    {
                        ToolName = msg.ToolName,
                        ToolCallId = msg.ToolCallId,
                        IsToolCall = msg.ToolName is not null
                    });
                }
            }

            state.HistoryLoaded = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load history for {agentId}: {ex.Message}");
            state.HistoryLoaded = true; // Don't retry on failure
        }
        finally
        {
            state.IsLoadingHistory = false;
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Register a session ID → agent ID mapping.</summary>
    public void RegisterSession(string agentId, string sessionId, string? channelType = null)
    {
        _sessionToAgent[sessionId] = agentId;
        if (_sessions.TryGetValue(agentId, out var state))
        {
            state.SessionId = sessionId;
            if (channelType is not null)
                state.ChannelType = channelType;
        }
    }

    // ── Event routing ─────────────────────────────────────────────────────
    // Each handler finds the correct AgentSessionState by sessionId and updates it.
    // OnStateChanged fires to trigger UI re-render.

    private void HandleConnected(ConnectedPayload payload)
    {
        _sessions.Clear();
        _sessionToAgent.Clear();

        foreach (var agent in payload.Agents)
        {
            _sessions[agent.AgentId] = new AgentSessionState
            {
                AgentId = agent.AgentId,
                DisplayName = agent.DisplayName,
                IsConnected = true
            };
        }

        OnStateChanged?.Invoke();
    }

    private void HandleMessageStart(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

        state.IsStreaming = true;
        state.CurrentStreamBuffer = "";
        OnStateChanged?.Invoke();
    }

    private void HandleContentDelta(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

        state.CurrentStreamBuffer += evt.ContentDelta ?? "";
        OnStateChanged?.Invoke();
    }

    private void HandleToolStart(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

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

        state.Messages.Add(msg);
        state.ActiveToolCalls[toolCallId] = new ActiveToolCall
        {
            ToolCallId = toolCallId,
            ToolName = evt.ToolName ?? "unknown",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = msg.Id
        };

        OnStateChanged?.Invoke();
    }

    private void HandleToolEnd(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

        var toolCallId = evt.ToolCallId;
        TimeSpan? duration = null;
        string? messageId = null;

        if (toolCallId is not null && state.ActiveToolCalls.TryGetValue(toolCallId, out var activeTool))
        {
            duration = DateTimeOffset.UtcNow - activeTool.StartedAt;
            messageId = activeTool.MessageId;
            state.ActiveToolCalls.Remove(toolCallId);
        }

        // Try to update the ToolStart message in-place with result and duration
        if (messageId is not null)
        {
            var index = state.Messages.FindIndex(m => m.Id == messageId);
            if (index >= 0)
            {
                var original = state.Messages[index];
                state.Messages[index] = original with
                {
                    Content = evt.ToolIsError == true
                        ? $"❌ {evt.ToolName} failed"
                        : $"✅ {evt.ToolName} completed",
                    ToolResult = evt.ToolResult,
                    ToolIsError = evt.ToolIsError,
                    ToolDuration = duration
                };
                OnStateChanged?.Invoke();
                return;
            }
        }

        // Fallback: add as a new message if the original was not found
        state.Messages.Add(new ChatMessage("Tool",
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
        OnStateChanged?.Invoke();
    }

    private void HandleMessageEnd(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

        if (!string.IsNullOrEmpty(state.CurrentStreamBuffer))
        {
            state.Messages.Add(new ChatMessage("Assistant", state.CurrentStreamBuffer, DateTimeOffset.UtcNow));
        }

        state.CurrentStreamBuffer = "";
        state.IsStreaming = false;

        // Track unread for non-active agents
        if (state.AgentId != ActiveAgentId)
        {
            state.UnreadCount++;
        }

        OnStateChanged?.Invoke();
    }

    private void HandleError(AgentStreamEvent evt)
    {
        var state = FindStateBySessionId(evt.SessionId);
        if (state is null) return;

        state.Messages.Add(new ChatMessage("Error", evt.ErrorMessage ?? "An unknown error occurred.", DateTimeOffset.UtcNow));
        state.IsStreaming = false;
        state.CurrentStreamBuffer = "";
        OnStateChanged?.Invoke();
    }

    private void HandleSessionReset(SessionResetPayload payload)
    {
        if (_sessions.TryGetValue(payload.AgentId, out var state))
        {
            if (state.SessionId is not null)
                _sessionToAgent.Remove(state.SessionId);

            state.SessionId = null;
            state.Messages.Clear();
            state.IsStreaming = false;
            state.CurrentStreamBuffer = "";
            state.UnreadCount = 0;
            state.HistoryLoaded = false;
            state.ActiveToolCalls.Clear();
        }

        OnStateChanged?.Invoke();
    }

    private void HandleReconnecting()
    {
        foreach (var state in _sessions.Values)
        {
            if (state.IsStreaming)
                _streamingWhenDisconnected.Add(state.AgentId);
            state.IsConnected = false;
        }

        OnStateChanged?.Invoke();
    }

    private async void HandleReconnected()
    {
        // Mark all agents as connected again
        foreach (var state in _sessions.Values)
            state.IsConnected = true;

        try
        {
            // Re-subscribe to all session groups after reconnection
            var result = await _hub.SubscribeAllAsync();
            foreach (var session in result.Sessions)
                RegisterSession(session.AgentId, session.SessionId, session.ChannelType);

            // Recover any agents that were streaming when disconnect occurred
            foreach (var agentId in _streamingWhenDisconnected)
            {
                if (_sessions.TryGetValue(agentId, out var state))
                {
                    state.IsStreaming = false;
                    state.CurrentStreamBuffer = "";
                    state.HistoryLoaded = false; // Force reload to pick up missed messages
                    await LoadHistoryAsync(agentId);
                }
            }

            _streamingWhenDisconnected.Clear();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Reconnect recovery failed: {ex.Message}");
        }

        OnStateChanged?.Invoke();
    }

    private void HandleDisconnected()
    {
        foreach (var state in _sessions.Values)
            state.IsConnected = false;
        OnStateChanged?.Invoke();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private AgentSessionState? FindStateBySessionId(string? sessionId)
    {
        if (sessionId is null) return null;
        return _sessionToAgent.TryGetValue(sessionId, out var agentId)
            && _sessions.TryGetValue(agentId, out var state)
            ? state
            : null;
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
    public void Dispose()
    {
        _hub.OnConnected -= HandleConnected;
        _hub.OnMessageStart -= HandleMessageStart;
        _hub.OnContentDelta -= HandleContentDelta;
        _hub.OnToolStart -= HandleToolStart;
        _hub.OnToolEnd -= HandleToolEnd;
        _hub.OnMessageEnd -= HandleMessageEnd;
        _hub.OnError -= HandleError;
        _hub.OnSessionReset -= HandleSessionReset;
        _hub.OnReconnecting -= HandleReconnecting;
        _hub.OnReconnected -= HandleReconnected;
        _hub.OnDisconnected -= HandleDisconnected;
    }
}
