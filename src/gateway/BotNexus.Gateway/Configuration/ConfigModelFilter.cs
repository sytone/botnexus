using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Configuration-backed model filter applying provider and model allowlists.
/// </summary>
public sealed class ConfigModelFilter : IModelFilter
{
    private readonly ModelRegistry _modelRegistry;
    private readonly IOptionsMonitor<PlatformConfig> _config;

    public ConfigModelFilter(ModelRegistry modelRegistry, IOptionsMonitor<PlatformConfig> config)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetProviders()
    {
        var allProviders = _modelRegistry.GetProviders();
        var providerConfigs = _config.CurrentValue.Providers;

        if (providerConfigs is null or { Count: 0 })
        {
            return allProviders
                .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return allProviders
            .Where(provider => IsProviderEnabled(provider, providerConfigs))
            .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<LlmModelInfo> GetModels(string provider)
    {
        var models = _modelRegistry.GetModels(provider);
        var allowlist = GetProviderAllowlist(provider);

        var filteredModels = allowlist is null
            ? models
            : models.Where(model => allowlist.Contains(model.Id, StringComparer.OrdinalIgnoreCase));

        return filteredModels
            .Select(model => new LlmModelInfo(model.Id, model.Name, model.Provider))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<LlmModelInfo> GetModelsForAgent(string provider, IReadOnlyList<string> agentAllowedModelIds)
    {
        var providerModels = GetModels(provider);
        if (agentAllowedModelIds is null or { Count: 0 })
            return providerModels;

        return providerModels
            .Where(model => agentAllowedModelIds.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private bool IsProviderEnabled(string provider, Dictionary<string, ProviderConfig> providerConfigs)
    {
        return !providerConfigs.TryGetValue(provider, out var config) || config.Enabled;
    }

    private List<string>? GetProviderAllowlist(string provider)
    {
        var providerConfigs = _config.CurrentValue.Providers;
        if (providerConfigs is null || !providerConfigs.TryGetValue(provider, out var config))
            return null;

        return config.Models;
    }
}
