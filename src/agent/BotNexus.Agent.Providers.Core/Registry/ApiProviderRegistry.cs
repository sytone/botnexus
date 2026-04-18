using System.Collections.Concurrent;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Registry of API providers. Port of pi-mono's api-registry.ts.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class ApiProviderRegistry
{
    private sealed record Registration(IApiProvider Provider, string? SourceId);
    private sealed class GuardedProvider(IApiProvider inner) : IApiProvider
    {
        public string Api => inner.Api;

        /// <summary>
        /// Executes stream.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="context">The context.</param>
        /// <param name="options">The options.</param>
        /// <returns>The stream result.</returns>
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            ValidateModelApi(model, Api);
            return inner.Stream(model, context, options);
        }

        /// <summary>
        /// Executes stream simple.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="context">The context.</param>
        /// <param name="options">The options.</param>
        /// <returns>The stream simple result.</returns>
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            ValidateModelApi(model, Api);
            return inner.StreamSimple(model, context, options);
        }

        private static void ValidateModelApi(LlmModel model, string expectedApi)
        {
            if (!string.Equals(model.Api, expectedApi, StringComparison.Ordinal))
                throw new InvalidOperationException($"Mismatched api: {model.Api} expected {expectedApi}");
        }
    }

    private readonly ConcurrentDictionary<string, Registration> _registry = new();

    /// <summary>
    /// Executes register.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="sourceId">The source id.</param>
    public void Register(IApiProvider provider, string? sourceId = null)
    {
        _registry[provider.Api] = new Registration(new GuardedProvider(provider), sourceId);
    }

    /// <summary>
    /// Executes get.
    /// </summary>
    /// <param name="api">The api.</param>
    /// <returns>The get result.</returns>
    public IApiProvider? Get(string api)
    {
        return _registry.TryGetValue(api, out var reg) ? reg.Provider : null;
    }

    /// <summary>
    /// Executes get all.
    /// </summary>
    /// <returns>The get all result.</returns>
    public IReadOnlyList<IApiProvider> GetAll()
    {
        return _registry.Values.Select(r => r.Provider).ToList();
    }

    /// <summary>
    /// Executes unregister.
    /// </summary>
    /// <param name="sourceId">The source id.</param>
    public void Unregister(string sourceId)
    {
        var toRemove = _registry
            .Where(kvp => kvp.Value.SourceId == sourceId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var api in toRemove)
            _registry.TryRemove(api, out _);
    }

    /// <summary>
    /// Executes clear.
    /// </summary>
    public void Clear()
    {
        _registry.Clear();
    }
}
