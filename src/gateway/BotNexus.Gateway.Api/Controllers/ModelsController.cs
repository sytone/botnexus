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
                    Provider: model.Provider,
                    SupportedThinkingLevels: model.SupportedThinkingLevels ?? [],
                    SupportedContextSizes: model.SupportedContextSizes ?? [],
                    ContextWindow: model.ContextWindow,
                    MaxTokens: model.MaxTokens,
                    ContextWindowSource: model.ContextWindowSource,
                    MaxTokensSource: model.MaxTokensSource))
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
                Provider: model.Provider,
                SupportedThinkingLevels: model.SupportedThinkingLevels ?? [],
                SupportedContextSizes: model.SupportedContextSizes ?? [],
                ContextWindow: model.ContextWindow,
                MaxTokens: model.MaxTokens,
                ContextWindowSource: model.ContextWindowSource,
                MaxTokensSource: model.MaxTokensSource))
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
/// <param name="SupportedThinkingLevels">Wire-form thinking levels the model supports (empty when none).</param>
/// <param name="SupportedContextSizes">Context-window sizes (tokens) the model supports.</param>
/// <param name="ContextWindow">Registered context capacity in tokens, or null when unavailable.</param>
/// <param name="MaxTokens">Registered maximum output tokens, or null when unavailable.</param>
/// <param name="ContextWindowSource">Context declaration origin, not a provider-verification claim.</param>
/// <param name="MaxTokensSource">Output declaration origin, not a provider-verification claim.</param>
public sealed record ModelInfo(
    string Name,
    string ModelId,
    string Id,
    string Provider,
    IReadOnlyList<string> SupportedThinkingLevels,
    IReadOnlyList<int> SupportedContextSizes,
    int? ContextWindow = null,
    int? MaxTokens = null,
    string? ContextWindowSource = null,
    string? MaxTokensSource = null
);
