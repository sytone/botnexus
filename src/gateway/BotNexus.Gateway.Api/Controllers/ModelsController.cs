using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for available LLM models.
/// </summary>
/// <summary>
/// Represents models controller.
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly IModelFilter _modelFilter;
    private readonly IAgentRegistry _agentRegistry;

    /// <inheritdoc cref="ModelsController"/>
    public ModelsController(IModelFilter modelFilter, IAgentRegistry agentRegistry)
    {
        _modelFilter = modelFilter ?? throw new ArgumentNullException(nameof(modelFilter));
        _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
    }

    /// <summary>
    /// Get all available models from all registered providers.
    /// </summary>
    /// <summary>
    /// Executes get models.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The get models result.</returns>
    [HttpGet]
    public ActionResult<IEnumerable<ModelInfo>> GetModels([FromQuery] string? provider = null, [FromQuery] string? agentId = null)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var agent = _agentRegistry.Get(AgentId.From(agentId));
            if (agent is null)
                return NotFound(new { error = $"Agent '{agentId}' not found." });

            var agentProviders = !string.IsNullOrWhiteSpace(provider)
                ? new[] { provider }
                : new[] { agent.ApiProvider };

            var agentModels = agentProviders
                .SelectMany(currentProvider => _modelFilter.GetModelsForAgent(currentProvider, agent.AllowedModelIds))
                .Select(model => new ModelInfo(
                    Name: model.Name,
                    ModelId: model.Id,
                    Id: model.Id,
                    Provider: model.Provider))
                .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(agentModels);
        }

        var providers = !string.IsNullOrWhiteSpace(provider)
            ? new[] { provider }
            : _modelFilter.GetProviders();

        var models = providers
            .SelectMany(currentProvider => _modelFilter.GetModels(currentProvider))
            .Select(model => new ModelInfo(
                Name: model.Name,
                ModelId: model.Id,
                Id: model.Id,
                Provider: model.Provider))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(models);
    }

    /// <summary>
    /// Get allowed models for a specific agent.
    /// </summary>
    /// <summary>
    /// Executes get agent models.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The get agent models result.</returns>
    [HttpGet("/api/agents/{agentId}/models")]
    public ActionResult<IEnumerable<ModelInfo>> GetAgentModels(string agentId, [FromQuery] string? provider = null)
    {
        var result = GetModels(provider, agentId);
        if (result.Result is not null)
            return result.Result;

        var models = result.Value ?? [];
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
