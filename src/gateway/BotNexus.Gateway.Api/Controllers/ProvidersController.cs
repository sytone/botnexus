using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Mvc;
namespace BotNexus.Gateway.Api.Controllers;
/// <summary>
/// REST API for available LLM providers and their health status.
/// </summary>
[ApiController]
[Route("api/providers")]
public sealed class ProvidersController : ControllerBase
{
    private readonly IModelFilter _modelFilter;
    private readonly IProviderHealthCheck? _healthCheck;

    /// <inheritdoc cref="ProvidersController"/>
    public ProvidersController(IModelFilter modelFilter, IProviderHealthCheck? healthCheck = null)
    {
        _modelFilter = modelFilter ?? throw new ArgumentNullException(nameof(modelFilter));
        _healthCheck = healthCheck;
    }

    /// <summary>
    /// Get all available providers.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<ProviderInfo>> GetProviders()
    {
        var providers = _modelFilter.GetProviders()
            .Select(provider => new ProviderInfo(
                Name: provider,
                ProviderId: provider,
                Id: provider))
            .ToList();
        return Ok(providers);
    }

    /// <summary>
    /// Check health of a specific provider. Validates credential availability
    /// and model registration.
    /// </summary>
    /// <param name="id">Provider identifier (e.g. "copilot", "anthropic", "openai").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health status with latency, model count, and credential state.</returns>
    [HttpGet("{id}/health")]
    [ProducesResponseType(typeof(ProviderHealthResponse), 200)]
    [ProducesResponseType(typeof(ProviderHealthResponse), 503)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CheckHealth(string id, CancellationToken cancellationToken)
    {
        if (_healthCheck is null)
        {
            return NotFound("Provider health check service not available.");
        }

        // Verify provider exists in the registry
        var providers = _modelFilter.GetProviders();
        if (!providers.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            return NotFound($"Provider '{id}' not found.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        ProviderHealthResult result;
        try
        {
            result = await _healthCheck.CheckAsync(id, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = new ProviderHealthResult(
                id,
                ProviderHealthStatus.Unhealthy,
                10_000,
                DateTimeOffset.UtcNow,
                0,
                false,
                "Health check timed out after 10 seconds.");
        }

        var response = new ProviderHealthResponse
        {
            ProviderId = result.ProviderId,
            Status = result.Status.ToString().ToLowerInvariant(),
            LatencyMs = result.LatencyMs,
            CheckedAt = result.CheckedAt,
            Models = result.ModelCount,
            HasCredentials = result.HasCredentials,
            Error = result.Error
        };

        return result.Status == ProviderHealthStatus.Healthy
            ? Ok(response)
            : StatusCode(503, response);
    }
}
/// <summary>
/// Provider information for WebUI dropdown.
/// </summary>
/// <param name="Name">Display name of the provider.</param>
/// <param name="ProviderId">Provider identifier.</param>
/// <param name="Id">Provider identifier (alias for providerId).</param>
public sealed record ProviderInfo(
    string Name,
    string ProviderId,
    string Id
);

/// <summary>
/// Response DTO for provider health check endpoint.
/// </summary>
public sealed class ProviderHealthResponse
{
    /// <summary>Provider identifier.</summary>
    public required string ProviderId { get; init; }
    /// <summary>Health status: healthy, unhealthy, or unknown.</summary>
    public required string Status { get; init; }
    /// <summary>Time taken for the health check in milliseconds.</summary>
    public required long LatencyMs { get; init; }
    /// <summary>When the check was performed.</summary>
    public required DateTimeOffset CheckedAt { get; init; }
    /// <summary>Number of models registered for this provider.</summary>
    public required int Models { get; init; }
    /// <summary>Whether valid credentials could be resolved.</summary>
    public required bool HasCredentials { get; init; }
    /// <summary>Error details if unhealthy, null otherwise.</summary>
    public string? Error { get; init; }
}