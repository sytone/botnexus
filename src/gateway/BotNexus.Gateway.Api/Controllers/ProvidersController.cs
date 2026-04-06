using BotNexus.Providers.Core.Registry;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for available LLM providers.
/// </summary>
[ApiController]
[Route("api/providers")]
public sealed class ProvidersController : ControllerBase
{
    private readonly ModelRegistry _modelRegistry;

    /// <inheritdoc cref="ProvidersController"/>
    public ProvidersController(ModelRegistry modelRegistry)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
    }

    /// <summary>
    /// Get all available providers.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<ProviderInfo>> GetProviders()
    {
        var providers = _modelRegistry.GetProviders()
            .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
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
