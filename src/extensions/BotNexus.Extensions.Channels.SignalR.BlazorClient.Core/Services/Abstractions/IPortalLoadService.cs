namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Owns startup sequence and readiness gate.
/// </summary>
public interface IPortalLoadService
{
    /// <summary>True once the initial REST load, SignalR connect, and SubscribeAll have succeeded.</summary>
    bool IsReady { get; }

    /// <summary>True while startup is in progress.</summary>
    bool IsLoading { get; }

    /// <summary>Non-null if startup failed.</summary>
    string? LoadError { get; }

    /// <summary>True when the SignalR hub connection is in the Connected state.</summary>
    bool IsSignalRConnected { get; }

    /// <summary>
    /// The connecting client kind ("desktop" or "mobile") appended to the hub URL as a
    /// <c>client</c> query parameter so the gateway can distinguish device classes per
    /// SignalR connection (#1209). Callers set this before <see cref="InitializeAsync"/>;
    /// it defaults to "desktop" so the historical desktop-portal path is unchanged (AC#5).
    /// </summary>
    string ClientKind { get; set; }

    /// <summary>Raised when <see cref="IsReady"/>, <see cref="IsLoading"/>, or <see cref="LoadError"/> changes.</summary>
    event Action? OnReadyChanged;

    /// <summary>Raised when <see cref="IsSignalRConnected"/> changes.</summary>
    event Action? OnConnectionStateChanged;

    /// <summary>
    /// Executes the portal startup sequence: REST-first, SignalR-second.
    /// 1. GET /api/agents
    /// 2. GET /api/conversations for each agent (parallel)
    /// 3. Connect SignalR
    /// 4. SubscribeAll
    /// 5. IsReady = true
    /// </summary>
    Task InitializeAsync(string hubUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fetches agent and conversation data and reconnects SignalR if disconnected.
    /// Intended for mobile app-resume and manual refresh flows.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Liveness-verified hub reset for mobile app resume (#1838). On foreground return, iOS may
    /// have silently recycled the background WebSocket, leaving <see cref="IsSignalRConnected"/>
    /// reporting connected on a dead "zombie" socket. This probes the hub with a short-timeout
    /// round-trip; on success the existing connection is kept (equivalent to <see cref="RefreshAsync"/>),
    /// and on probe failure the connection is torn down and rebuilt with a fresh negotiate before
    /// refreshing. A reentrancy guard collapses rapid visibility toggles to a single reset so
    /// concurrent rebuilds cannot stack.
    /// </summary>
    /// <returns>The outcome describing whether the connection was kept alive, rebuilt, or skipped.</returns>
    Task<HubResumeOutcome> ResumeAsync(CancellationToken cancellationToken = default);
}
