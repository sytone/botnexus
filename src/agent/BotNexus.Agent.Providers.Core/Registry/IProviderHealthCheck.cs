namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Optional health check interface for API providers. Implementations validate
/// that credentials are configured and the provider endpoint is reachable.
/// </summary>
public interface IProviderHealthCheck
{
    /// <summary>
    /// Checks the health of a provider identified by its registry key (e.g. "copilot", "anthropic").
    /// </summary>
    /// <param name="providerId">Provider identifier as known by ModelRegistry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health check result with status, latency, and optional error details.</returns>
    Task<ProviderHealthResult> CheckAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a provider health check.
/// </summary>
/// <param name="ProviderId">Provider identifier.</param>
/// <param name="Status">Health status: healthy, unhealthy, or unknown.</param>
/// <param name="LatencyMs">Time taken to perform the check in milliseconds.</param>
/// <param name="CheckedAt">UTC timestamp when the check was performed.</param>
/// <param name="ModelCount">Number of models registered for this provider.</param>
/// <param name="HasCredentials">Whether credentials could be resolved.</param>
/// <param name="Error">Error message if unhealthy, null otherwise.</param>
public sealed record ProviderHealthResult(
    string ProviderId,
    ProviderHealthStatus Status,
    long LatencyMs,
    DateTimeOffset CheckedAt,
    int ModelCount,
    bool HasCredentials,
    string? Error = null);

/// <summary>
/// Provider health status values.
/// </summary>
public enum ProviderHealthStatus
{
    /// <summary>Provider is reachable and credentials are valid.</summary>
    Healthy,

    /// <summary>Provider is unreachable, credentials invalid, or check failed.</summary>
    Unhealthy,

    /// <summary>Health cannot be determined (e.g. no health check implementation for this provider).</summary>
    Unknown
}