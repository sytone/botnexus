using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Typed wrapper around <see cref="HubConnection"/> for the BotNexus Gateway hub.
/// Exposes C# events matching the <c>IGatewayHubClient</c> contract so Blazor
/// components can bind to real-time agent output.
/// </summary>
public sealed class GatewayHubConnection : IAsyncDisposable
{
    private HubConnection? _connection;

    // ── Server → Client events ──────────────────────────────────────────

    /// <summary>Raised when the hub sends the initial <c>Connected</c> payload.</summary>
    public event Action<ConnectedPayload>? OnConnected;

    /// <summary>Raised when a session is reset server-side.</summary>
    public event Action<SessionResetPayload>? OnSessionReset;

    /// <summary>Raised at the start of an agent response.</summary>
    public event Action<AgentStreamEvent>? OnMessageStart;

    /// <summary>Raised for each incremental content chunk.</summary>
    public event Action<ContentDeltaPayload>? OnContentDelta;

    /// <summary>Raised for each incremental thinking chunk.</summary>
    public event Action<AgentStreamEvent>? OnThinkingDelta;

    /// <summary>Raised when a tool invocation begins.</summary>
    public event Action<AgentStreamEvent>? OnToolStart;

    /// <summary>Raised when a tool invocation completes.</summary>
    public event Action<AgentStreamEvent>? OnToolEnd;

    /// <summary>Raised when the agent response is complete.</summary>
    public event Action<AgentStreamEvent>? OnMessageEnd;

    /// <summary>Raised when an error occurs during streaming.</summary>
    public event Action<AgentStreamEvent>? OnError;

    /// <summary>Raised when a sub-agent is spawned.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentSpawned;

    /// <summary>Raised when a sub-agent completes.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentCompleted;

    /// <summary>Raised when a sub-agent fails.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentFailed;

    /// <summary>Raised when a sub-agent is killed.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentKilled;

    // ── Connection lifecycle events ─────────────────────────────────────

    /// <summary>Raised when the connection is lost and automatic reconnect starts.</summary>
    public event Action? OnReconnecting;

    /// <summary>Raised when the connection is closed (after reconnect exhaustion or explicit stop).</summary>
    public event Action? OnDisconnected;

    // ── State ───────────────────────────────────────────────────────────

    /// <summary>Whether the hub connection is currently in the <c>Connected</c> state.</summary>
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Current connection state for UI display.</summary>
    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    // ── Connect / Disconnect ────────────────────────────────────────────

    /// <summary>
    /// Builds a new <see cref="HubConnection"/>, registers all event handlers,
    /// and starts the connection.
    /// </summary>
    /// <param name="hubUrl">Absolute URL of the Gateway hub (e.g. <c>https://localhost:5000/hub/gateway</c>).</param>
    public async Task ConnectAsync(string hubUrl)
    {
        if (_connection is not null)
            await _connection.DisposeAsync();

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Register server → client event handlers matching IGatewayHubClient
        _connection.On<ConnectedPayload>("Connected", p => OnConnected?.Invoke(p));
        _connection.On<SessionResetPayload>("SessionReset", p => OnSessionReset?.Invoke(p));
        _connection.On<AgentStreamEvent>("MessageStart", e => OnMessageStart?.Invoke(e));
        _connection.On<ContentDeltaPayload>("ContentDelta", e => OnContentDelta?.Invoke(e));
        _connection.On<AgentStreamEvent>("ThinkingDelta", e => OnThinkingDelta?.Invoke(e));
        _connection.On<AgentStreamEvent>("ToolStart", e => OnToolStart?.Invoke(e));
        _connection.On<AgentStreamEvent>("ToolEnd", e => OnToolEnd?.Invoke(e));
        _connection.On<AgentStreamEvent>("MessageEnd", e => OnMessageEnd?.Invoke(e));
        _connection.On<AgentStreamEvent>("Error", e => OnError?.Invoke(e));
        _connection.On<SubAgentEventPayload>("SubAgentSpawned", p => OnSubAgentSpawned?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentCompleted", p => OnSubAgentCompleted?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentFailed", p => OnSubAgentFailed?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentKilled", p => OnSubAgentKilled?.Invoke(p));

        _connection.Reconnecting += _ => { OnReconnecting?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { OnDisconnected?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync();
    }

    // ── Client → Server invocations ─────────────────────────────────────

    /// <summary>Subscribe to all active sessions. Returns current session list.</summary>
    public async Task<SubscribeAllResult> SubscribeAllAsync()
        => await _connection!.InvokeAsync<SubscribeAllResult>("SubscribeAll");

    /// <summary>Send a message to the specified agent.</summary>
    public async Task<SendMessageResult> SendMessageAsync(string agentId, string channelType, string content)
        => await _connection!.InvokeAsync<SendMessageResult>("SendMessage", agentId, channelType, content);

    /// <summary>Steer an in-progress agent response.</summary>
    public async Task SteerAsync(string agentId, string sessionId, string content)
        => await _connection!.InvokeAsync("Steer", agentId, sessionId, content);

    /// <summary>Send a follow-up message into an existing session.</summary>
    public async Task FollowUpAsync(string agentId, string sessionId, string content)
        => await _connection!.InvokeAsync("FollowUp", agentId, sessionId, content);

    /// <summary>Abort an in-progress agent response.</summary>
    public async Task AbortAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync("Abort", agentId, sessionId);

    /// <summary>Reset (archive) a session and start fresh.</summary>
    public async Task ResetSessionAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync("ResetSession", agentId, sessionId);

    /// <summary>Compact a session to reduce token usage.</summary>
    public async Task<CompactSessionResult> CompactSessionAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync<CompactSessionResult>("CompactSession", agentId, sessionId);

    // ── Dispose ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
