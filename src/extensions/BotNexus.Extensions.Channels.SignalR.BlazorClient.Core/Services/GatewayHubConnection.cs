using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

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

    /// <summary>Raised when the agent run loop starts. Brackets the whole loop with <see cref="OnRunEnded"/>;
    /// the authoritative "agent busy" signal that does not flicker between turns or tools.</summary>
    public event Action<AgentStreamEvent>? OnRunStarted;

    /// <summary>Raised at the start of an agent response.</summary>
    public event Action<AgentStreamEvent>? OnMessageStart;

    /// <summary>Raised for each incremental content chunk.</summary>
    public event Action<AgentStreamEvent>? OnContentDelta;

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

    /// <summary>Raised when the agent requires interactive user input mid-turn.</summary>
    public event Action<AgentStreamEvent>? OnUserInputRequired;

    /// <summary>Raised when the gateway notifies that a previous turn was interrupted by a restart.</summary>
    public event Action<AgentStreamEvent>? OnTurnInterrupted;

    /// <summary>Raised when the agent turn has fully completed (including tool-only turns that produce no MessageEnd).</summary>
    public event Action<AgentStreamEvent>? OnTurnEnd;

    /// <summary>Raised when the agent run loop fully settles (all turns, tools, and follow-up
    /// continuations done). The authoritative idle signal; brackets the run with <see cref="OnRunStarted"/>.</summary>
    public event Action<AgentStreamEvent>? OnRunEnded;

    /// <summary>Raised when a sub-agent is spawned.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentSpawned;

    /// <summary>Raised when a sub-agent completes.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentCompleted;

    /// <summary>Raised when a sub-agent fails.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentFailed;

    /// <summary>Raised when a sub-agent is killed.</summary>
    public event Action<SubAgentEventPayload>? OnSubAgentKilled;

    /// <summary>Raised when steering feedback is received from the server.</summary>
    public event Action<SteeringFeedbackPayload>? OnSteeringFeedback;

    /// <summary>Raised when the current canvas HTML is updated for an agent.</summary>
    public event Action<string, string, string>? OnCanvasUpdated;

    /// <summary>Raised when a canvas state key is changed or cleared.</summary>
    public event Action<string, string, object?>? OnCanvasStateChanged;

    /// <summary>Raised when a conversation's persisted todo state changes (raw TodoJson, or null when cleared).</summary>
    public event Action<string, string, string?>? OnTodoUpdated;

    /// <summary>Raised when a conversation is created, updated, or archived on the server.</summary>
    public event Action<ConversationChangedPayload>? OnConversationChanged;

    /// <summary>Raised when agent configuration changes on the server (add/update/delete).</summary>
    public event Action<AgentsChangedPayload>? OnAgentsChanged;

    // ── Connection lifecycle events ─────────────────────────────────────

    /// <summary>Raised when the connection is lost and automatic reconnect starts.</summary>
    public event Action? OnReconnecting;

    /// <summary>Raised when automatic reconnect succeeds and the connection is restored.</summary>
    public event Action? OnReconnected;

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
    /// <param name="clientKind">
    /// The connecting client kind ("desktop" or "mobile"). Appended as a <c>client</c> query
    /// parameter so the gateway can distinguish device classes per connection (#1209).
    /// Defaults to "desktop" to preserve the historical desktop-portal behaviour; a
    /// null/blank value is omitted so the hub falls back to its own default.
    /// </param>
    /// <param name="tuning">
    /// Optional per-connection keep-alive/timeout and reconnect tuning (#1840). When null (the
    /// desktop default) the framework's stock <see cref="HubConnection.ServerTimeout"/>,
    /// <see cref="HubConnection.KeepAliveInterval"/>, and default automatic-reconnect budget are
    /// used unchanged. The mobile client supplies a populated instance so a tunnelled, backgrounded
    /// PWA gets a longer server timeout, a tunnel-friendly keep-alive cadence, and a widened,
    /// indefinitely-retrying reconnect schedule.
    /// </param>
    public async Task ConnectAsync(string hubUrl, string clientKind = "desktop", HubConnectionTuning? tuning = null)
    {
        if (_connection is not null)
            await _connection.DisposeAsync();

        var builder = new HubConnectionBuilder()
            .WithUrl(AppendClientKindQuery(hubUrl, clientKind));

        // A supplied retry policy widens the reconnect budget for mobile; otherwise keep the
        // framework's default ~5x3s budget so the desktop path is unchanged.
        builder = tuning?.ReconnectRetryPolicy is { } retryPolicy
            ? builder.WithAutomaticReconnect(retryPolicy)
            : builder.WithAutomaticReconnect();

        _connection = builder.Build();

        // Keep-alive / server-timeout tuning (#1840). Only overridden when the caller supplies a
        // value; a null field leaves the framework default (ServerTimeout 30s, KeepAlive 15s) in
        // place so the desktop client is not regressed.
        if (tuning?.ServerTimeout is { } serverTimeout)
            _connection.ServerTimeout = serverTimeout;
        if (tuning?.KeepAliveInterval is { } keepAlive)
            _connection.KeepAliveInterval = keepAlive;

        // Register server → client event handlers matching IGatewayHubClient
        _connection.On<ConnectedPayload>("Connected", p => OnConnected?.Invoke(p));
        _connection.On<SessionResetPayload>("SessionReset", p => OnSessionReset?.Invoke(p));
        _connection.On<AgentStreamEvent>("RunStarted", e => OnRunStarted?.Invoke(e));
        _connection.On<AgentStreamEvent>("MessageStart", e => OnMessageStart?.Invoke(e));
        _connection.On<AgentStreamEvent>("ContentDelta", e => OnContentDelta?.Invoke(e));
        _connection.On<AgentStreamEvent>("ThinkingDelta", e => OnThinkingDelta?.Invoke(e));
        _connection.On<AgentStreamEvent>("ToolStart", e => OnToolStart?.Invoke(e));
        _connection.On<AgentStreamEvent>("ToolEnd", e => OnToolEnd?.Invoke(e));
        _connection.On<AgentStreamEvent>("MessageEnd", e => OnMessageEnd?.Invoke(e));
        _connection.On<AgentStreamEvent>("Error", e => OnError?.Invoke(e));
        _connection.On<AgentStreamEvent>("UserInputRequired", e => OnUserInputRequired?.Invoke(e));
        _connection.On<AgentStreamEvent>("TurnInterrupted", e => OnTurnInterrupted?.Invoke(e));
        _connection.On<AgentStreamEvent>("TurnEnd", e => OnTurnEnd?.Invoke(e));
        _connection.On<AgentStreamEvent>("RunEnded", e => OnRunEnded?.Invoke(e));
        _connection.On<SubAgentEventPayload>("SubAgentSpawned", p => OnSubAgentSpawned?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentCompleted", p => OnSubAgentCompleted?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentFailed", p => OnSubAgentFailed?.Invoke(p));
        _connection.On<SubAgentEventPayload>("SubAgentKilled", p => OnSubAgentKilled?.Invoke(p));
        _connection.On<SteeringFeedbackPayload>("SteeringFeedback", p => OnSteeringFeedback?.Invoke(p));
        _connection.On<string, string, string>("CanvasUpdated", (agentId, conversationId, html) => OnCanvasUpdated?.Invoke(agentId, conversationId, html));
        _connection.On<string, string, object?>("CanvasStateChanged", (conversationId, key, value) => OnCanvasStateChanged?.Invoke(conversationId, key, value));
        _connection.On<string, string, string?>("TodoUpdated", (agentId, conversationId, todoJson) => OnTodoUpdated?.Invoke(agentId, conversationId, todoJson));
        _connection.On<AgentsChangedPayload>("AgentsChanged", p => OnAgentsChanged?.Invoke(p));
                _connection.On<ConversationChangedPayload>("ConversationChanged", p => OnConversationChanged?.Invoke(p));

        _connection.Reconnecting += _ => { OnReconnecting?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ => { OnReconnected?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { OnDisconnected?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync();
    }

    // ── Client → Server invocations ─────────────────────────────────────

    /// <summary>
    /// Appends the connect-time <c>client</c> query parameter to <paramref name="hubUrl"/> so the
    /// gateway can distinguish device classes (e.g. "mobile" vs "desktop") per SignalR connection
    /// (#1209). The kind is lower-cased for a stable wire value. The separator is chosen so the
    /// append works whether or not the hub URL already carries a query string. A null or blank
    /// kind returns the URL unchanged so the gateway falls back to its own default, keeping the
    /// no-hint path backward compatible.
    /// </summary>
    /// <param name="hubUrl">The hub URL, possibly already containing a query string.</param>
    /// <param name="clientKind">The client kind to append, or null/blank to skip.</param>
    /// <returns>The hub URL with the client kind appended, or the original URL when no kind is supplied.</returns>
    public static string AppendClientKindQuery(string hubUrl, string? clientKind)
    {
        if (string.IsNullOrWhiteSpace(clientKind))
            return hubUrl;

        var separator = hubUrl.Contains('?') ? '&' : '?';
        return $"{hubUrl}{separator}client={clientKind.Trim().ToLowerInvariant()}";
    }

    /// <summary>Subscribe to all active sessions. Returns current session list.</summary>
    public async Task<SubscribeAllResult> SubscribeAllAsync()
        => await _connection!.InvokeAsync<SubscribeAllResult>("SubscribeAll");

    /// <summary>
    /// Performs a lightweight liveness round-trip against the hub (#1838). Invokes the server
    /// <c>Ping</c> method under the supplied cancellation token so callers can bound it with a
    /// short timeout. Unlike <see cref="IsConnected"/> -- which merely reflects the client-side
    /// <see cref="HubConnectionState"/> and stays <c>Connected</c> on an iOS zombie socket -- a
    /// completed Ping proves the transport is alive end-to-end. Returns <c>false</c> when there is
    /// no connection, when the round-trip throws, or when it is cancelled by the timeout.
    /// </summary>
    /// <param name="cancellationToken">Fires after the caller''s short probe timeout.</param>
    /// <returns><c>true</c> when the Ping round-trip completed; otherwise <c>false</c>.</returns>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is null)
            return false;

        try
        {
            _ = await _connection.InvokeAsync<long>("Ping", cancellationToken);
            return true;
        }
        catch
        {
            // Any failure -- cancellation by the short timeout, a dead socket surfacing as an
            // invocation error, or the connection dropping mid-probe -- means the connection is
            // not verifiably alive, so the caller must rebuild rather than trust it.
            return false;
        }
    }

    /// <summary>
    /// Stops and disposes the current connection so the next <see cref="ConnectAsync"/> performs a
    /// fresh negotiate (#1838). Used by the app-resume reset when the liveness probe fails: the
    /// zombie connection is torn down explicitly rather than waiting on Blazor''s reconnect budget.
    /// Safe to call when no connection exists. After this returns, <see cref="IsConnected"/> is
    /// <c>false</c> and handlers registered on the old connection are gone (a rebuild re-registers).
    /// </summary>
    public async Task StopAndDisposeAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
            return;

        try
        {
            await connection.StopAsync();
        }
        catch
        {
            // A zombie/half-open socket can throw on StopAsync; dispose still needs to run to
            // release the transport, so the stop failure is swallowed deliberately.
        }

        await connection.DisposeAsync();
    }

    /// <summary>Send a message to the specified agent, optionally targeting a specific conversation.</summary>
    public async Task<SendMessageResult> SendMessageAsync(string agentId, string channelType, string content, string? conversationId = null)
    {
        // Hub.SendMessage now accepts an optional conversationId — no separate SendMessageToConversation method.
        return await _connection!.InvokeAsync<SendMessageResult>("SendMessage", agentId, channelType, content, conversationId);
    }

    /// <summary>Sends optional text and generic content parts to a specific conversation.</summary>
    public async Task<SendMessageResult> SendMessageWithMediaAsync(string agentId, string channelType, string content, IReadOnlyList<MediaContentPartDto> parts, string? conversationId = null)
        => await _connection!.InvokeAsync<SendMessageResult>("SendMessageWithMedia", agentId, channelType, content, parts, conversationId);

    /// <summary>Steer an in-progress agent response.</summary>
    public async Task<SendMessageResult> SteerAsync(string agentId, string sessionId, string content, string? conversationId)
        => await _connection!.InvokeAsync<SendMessageResult>("Steer", agentId, sessionId, content, conversationId);

    /// <summary>Send a follow-up message into an existing session.</summary>
    public async Task FollowUpAsync(string agentId, string sessionId, string content)
        => await _connection!.InvokeAsync("FollowUp", agentId, sessionId, content);

    /// <summary>Abort an in-progress agent response.</summary>
    public async Task AbortAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync("Abort", agentId, sessionId);

    /// <summary>Atomically abort the current turn and steer the agent in a new direction.</summary>
    /// <returns><c>true</c> when the interrupt was delivered to a live handle; <c>false</c> when the agent was idle.</returns>
    public async Task<bool> InterruptAndSteerAsync(string agentId, string sessionId, string message)
        => await _connection!.InvokeAsync<bool>("InterruptAndSteer", agentId, sessionId, message);

    /// <summary>Reset (archive) a session and start fresh.</summary>
    public async Task ResetSessionAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync("ResetSession", agentId, sessionId);

    /// <summary>Compact a session to reduce token usage.</summary>
    public async Task<CompactSessionResult> CompactSessionAsync(string agentId, string sessionId)
        => await _connection!.InvokeAsync<CompactSessionResult>("CompactSession", agentId, sessionId);

    /// <summary>
    /// Complete or cancel a pending <c>ask_user</c> prompt for the specified
    /// conversation request.
    /// </summary>
    public async Task RespondToAskUserAsync(
        string conversationId,
        string requestId,
        string? freeFormText,
        string[]? selectedValues,
        bool cancelled)
        => await _connection!.InvokeAsync(
            "RespondToAskUser",
            conversationId,
            requestId,
            freeFormText,
            selectedValues,
            cancelled);

    // ── Dispose ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
