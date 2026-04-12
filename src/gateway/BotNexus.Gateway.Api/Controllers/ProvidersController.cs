using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for available LLM providers.
/// </summary>
/// <summary>
/// Represents providers controller.
/// </summary>
[ApiController]
[Route("api/providers")]
public sealed class ProvidersController : ControllerBase
{
    private readonly IModelFilter _modelFilter;

    /// <inheritdoc cref="ProvidersController"/>
    public ProvidersController(IModelFilter modelFilter)
    {
        _modelFilter = modelFilter ?? throw new ArgumentNullException(nameof(modelFilter));
    }

    /// <summary>
    /// Get all available providers.
    /// </summary>
    /// <summary>
    /// Executes get providers.
    /// </summary>
    /// <returns>The get providers result.</returns>
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
