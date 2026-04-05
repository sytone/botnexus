using System.Collections.Concurrent;
using BotNexus.Providers.Core.Models;

namespace BotNexus.Providers.Core.Registry;

/// <summary>
/// Model registry. Port of pi-mono's models.ts registry.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class ModelRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LlmModel>> _registry = new();

    public void Register(string provider, LlmModel model)
    {
        var models = _registry.GetOrAdd(provider, _ => new ConcurrentDictionary<string, LlmModel>());
        models[model.Id] = model;
    }

    public LlmModel? GetModel(string provider, string modelId)
    {
        if (_registry.TryGetValue(provider, out var models) &&
            models.TryGetValue(modelId, out var model))
            return model;

        return null;
    }

    public IReadOnlyList<string> GetProviders()
    {
        return _registry.Keys.ToList();
    }

    public IReadOnlyList<LlmModel> GetModels(string provider)
    {
        return _registry.TryGetValue(provider, out var models)
            ? models.Values.ToList()
            : [];
    }

    /// <summary>
    /// Calculate cost from usage and model pricing.
    /// Port of pi-mono's calculateCost from models.ts.
    /// </summary>
    public static UsageCost CalculateCost(LlmModel model, Usage usage)
    {
        const decimal perMillion = 1_000_000m;
        var input = usage.Input * model.Cost.Input / perMillion;
        var output = usage.Output * model.Cost.Output / perMillion;
        var cacheRead = usage.CacheRead * model.Cost.CacheRead / perMillion;
        var cacheWrite = usage.CacheWrite * model.Cost.CacheWrite / perMillion;
        var total = input + output + cacheRead + cacheWrite;
        return new UsageCost(input, output, cacheRead, cacheWrite, total);
    }

    public static bool SupportsExtraHigh(LlmModel model)
    {
        if (!model.Reasoning)
            return false;

        return model.Id.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ModelsAreEqual(LlmModel a, LlmModel b)
    {
        return string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear()
    {
        _registry.Clear();
    }
}
