using System.Collections.Concurrent;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Model registry. Port of pi-mono's models.ts registry.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class ModelRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, LlmModel>> _registry = new();

    // Common aliases so users can write "copilot" instead of "github-copilot" in config
    private static readonly Dictionary<string, string> ProviderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copilot"] = "github-copilot"
    };

    // Selectable context-window tiers for models that advertise the extended-context
    // capability. 200K is the standard ceiling; 1M is the Anthropic-direct extended tier
    // (gated behind the context-1m beta header on the Anthropic messages path).
    private const int StandardContextWindow = 200_000;
    private const int ExtendedContextWindow = 1_000_000;

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
        var resolved = ResolveProvider(provider);
        if (_registry.TryGetValue(resolved, out var models) &&
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
        var resolved = ResolveProvider(provider);
        return _registry.TryGetValue(resolved, out var models)
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
    /// Returns the thinking levels a model legitimately supports, so the UI can offer
    /// only valid choices. Non-reasoning models support none; reasoning models support
    /// the base tiers, and ExtraHigh/Max are added only when the model advertises the
    /// extra-high capability.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>The supported thinking levels.</returns>
    public static IReadOnlyList<ThinkingLevel> GetSupportedThinkingLevels(LlmModel model)
    {
        if (!model.Reasoning)
            return [];

        if (model.SupportsExtraHighThinking)
            return
            [
                ThinkingLevel.Minimal,
                ThinkingLevel.Low,
                ThinkingLevel.Medium,
                ThinkingLevel.High,
                ThinkingLevel.ExtraHigh,
                ThinkingLevel.Max
            ];

        return
        [
            ThinkingLevel.Minimal,
            ThinkingLevel.Low,
            ThinkingLevel.Medium,
            ThinkingLevel.High
        ];
    }

    /// <summary>
    /// Reports whether a model can be driven with the extended (1M) context window.
    /// Mirrors <see cref="SupportsExtraHigh"/> for thinking: the capability is carried per
    /// model on the registry so a caller never offers a context size the provider rejects.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>True when the model advertises the extended-context capability.</returns>
    public static bool SupportsExtendedContext(LlmModel model)
    {
        return model.SupportsExtendedContextWindow;
    }

    /// <summary>
    /// Returns the context-window sizes a model legitimately supports, so the UI can offer
    /// only valid choices. Models without the extended capability expose just their single
    /// default window (for example Copilot Claude is fixed at 200K); models that advertise
    /// the extended capability expose the selectable 200K and 1M tiers (Anthropic-direct).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>The supported context-window sizes.</returns>
    public static IReadOnlyList<int> GetSupportedContextSizes(LlmModel model)
    {
        if (model.SupportsExtendedContextWindow)
            return [StandardContextWindow, ExtendedContextWindow];
        return [model.ContextWindow];
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

    private static string ResolveProvider(string provider) =>
        ProviderAliases.TryGetValue(provider, out var canonical) ? canonical : provider;
}
