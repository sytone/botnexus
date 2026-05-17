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

    /// <summary>Raised when <see cref="IsReady"/>, <see cref="IsLoading"/>, or <see cref="LoadError"/> changes.</summary>
    event Action? OnReadyChanged;

    /// <summary>
    /// Executes the portal startup sequence: REST-first, SignalR-second.
    /// 1. GET /api/agents
    /// 2. GET /api/conversations for each agent (parallel)
    /// 3. Connect SignalR
    /// 4. SubscribeAll
    /// 5. IsReady = true
    /// </summary>
    Task InitializeAsync(string hubUrl, CancellationToken cancellationToken = default);
}
