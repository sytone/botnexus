using BotNexus.Providers.Core.Registry;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for available LLM models.
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly ModelRegistry _modelRegistry;

    public ModelsController(ModelRegistry modelRegistry)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
    }

    /// <summary>
    /// Get all available models from all registered providers.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<ModelInfo>> GetModels()
    {
        var providers = _modelRegistry.GetProviders();
        var models = new List<ModelInfo>();

        foreach (var provider in providers)
        {
            var providerModels = _modelRegistry.GetModels(provider);
            foreach (var model in providerModels)
            {
                models.Add(new ModelInfo(
                    Name: model.Name,
                    ModelId: model.Id,
                    Id: model.Id,
                    Provider: model.Provider
                ));
            }
        }

        return Ok(models);
    }
}

/// <summary>
/// Model information for WebUI dropdown.
/// </summary>
/// <param name="Name">Display name of the model.</param>
/// <param name="ModelId">Model identifier.</param>
/// <param name="Id">Model identifier (alias for modelId).</param>
/// <param name="Provider">Provider name (e.g., github-copilot, anthropic, openai).</param>
public sealed record ModelInfo(
    string Name,
    string ModelId,
    string Id,
    string Provider
);
