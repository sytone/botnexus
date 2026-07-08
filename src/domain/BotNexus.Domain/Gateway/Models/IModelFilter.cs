namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Filters available providers and models based on platform configuration.
/// </summary>
public interface IModelFilter
{
    /// <summary>Get enabled provider names.</summary>
    IReadOnlyList<string> GetProviders();

    /// <summary>Get allowed models for a provider.</summary>
    IReadOnlyList<LlmModelInfo> GetModels(string provider);

    /// <summary>Get allowed models for a specific agent (intersects provider + agent allowlists).</summary>
    IReadOnlyList<LlmModelInfo> GetModelsForAgent(string provider, IReadOnlyList<string> agentAllowedModelIds);
}

/// <summary>
/// Lightweight model information for model filtering responses.
/// </summary>
/// <param name="Id">Model identifier.</param>
/// <param name="Name">Display model name.</param>
/// <param name="Provider">Provider identifier.</param>
/// <param name="SupportedThinkingLevels">
/// Wire-form thinking levels this model supports (minimal..max), so the agent editor offers
/// only valid choices. Empty for non-reasoning models. (#1705)
/// </param>
/// <param name="SupportedContextSizes">
/// Context-window sizes (tokens) this model supports. A single value for fixed-window models;
/// the selectable tiers for models advertising the extended-context capability. (#1705)
/// </param>
public sealed record LlmModelInfo(
    string Id,
    string Name,
    string Provider,
    IReadOnlyList<string>? SupportedThinkingLevels = null,
    IReadOnlyList<int>? SupportedContextSizes = null);
