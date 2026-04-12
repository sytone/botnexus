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
public sealed record LlmModelInfo(string Id, string Name, string Provider);
