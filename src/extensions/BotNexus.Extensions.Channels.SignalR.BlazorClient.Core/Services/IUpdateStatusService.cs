namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Polls <c>GET /api/gateway/update/status</c> at a configurable interval and surfaces
/// cached update state to Blazor components without requiring each component to manage
/// its own HTTP lifecycle.
/// </summary>
public interface IUpdateStatusService
{
    /// <summary>Most recently fetched update status, or null before the first poll completes.</summary>
    UpdateStatus? Status { get; }

    /// <summary>Fires whenever <see cref="Status"/> changes after a successful poll.</summary>
    event Action? StatusChanged;

    /// <summary>Forces an immediate <c>POST /api/gateway/update/check</c> and refreshes status.</summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Posts to <c>POST /api/gateway/update/start</c> and returns the HTTP status code.</summary>
    Task<int> StartUpdateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO that mirrors the <c>UpdateStatusResult</c> payload from the gateway REST endpoint.
/// </summary>
public sealed record UpdateStatus(
    bool Enabled,
    bool IsChecking,
    bool IsUpdateAvailable,
    bool IsUpdateInProgress,
    string CurrentCommitSha,
    string CurrentCommitShort,
    string? LatestCommitSha,
    string? LatestCommitShort,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt,
    string? RepositoryOwner,
    string? RepositoryName,
    string? Branch,
    string? CompareUrl,
    string? Error);
