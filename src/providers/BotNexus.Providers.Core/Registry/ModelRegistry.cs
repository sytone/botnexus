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

    /// <summary>
    /// Executes register.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="model">The model.</param>
    public void Register(string provider, LlmModel model)
    {
        var models = _registry.GetOrAdd(provider, _ => new ConcurrentDictionary<string, LlmModel>());
        models[model.Id] = model;
    }

    /// <summary>
    /// Executes get model.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="modelId">The model id.</param>
    /// <returns>The get model result.</returns>
    public LlmModel? GetModel(string provider, string modelId)
    {
        if (_registry.TryGetValue(provider, out var models) &&
            models.TryGetValue(modelId, out var model))
            return model;

        return null;
    }

    /// <summary>
    /// Executes get providers.
    /// </summary>
    /// <returns>The get providers result.</returns>
    public IReadOnlyList<string> GetProviders()
    {
        return _registry.Keys.ToList();
    }

    /// <summary>
    /// Executes get models.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The get models result.</returns>
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

    /// <summary>
    /// Executes supports extra high.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>The supports extra high result.</returns>
    public static bool SupportsExtraHigh(LlmModel model)
    {
        return model.SupportsExtraHighThinking;
    }

    /// <summary>
    /// Executes models are equal.
    /// </summary>
    /// <param name="a">The a.</param>
    /// <param name="b">The b.</param>
    /// <returns>The models are equal result.</returns>
    public static bool ModelsAreEqual(LlmModel a, LlmModel b)
    {
        return string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
               string.Equals(a.Provider, b.Provider, StringComparison.Ordinal);
    }

    /// <summary>
    /// Executes clear.
    /// </summary>
    public void Clear()
    {
        _registry.Clear();
    }
}
