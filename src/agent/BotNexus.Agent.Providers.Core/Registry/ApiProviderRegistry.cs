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
    private sealed record Registration(IApiProvider Provider, string? SourceId, IReadOnlySet<ProviderCapability> Capabilities);
    private sealed class GuardedProvider(IApiProvider inner) : IApiProvider
    {
        public string Api => inner.Api;
        public ProviderCapabilities Capabilities => inner.Capabilities;

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
    /// <param name="capabilities">
    /// The code-side capability set this registration declares (issue #2853). Omitted means
    /// <see cref="ProviderCapabilitySets.ChatOnly"/>, which is the pre-#2853 meaning of every
    /// existing registration -- so no caller changes behaviour by not passing it.
    /// </param>
    public void Register(IApiProvider provider, string? sourceId = null, IReadOnlySet<ProviderCapability>? capabilities = null)
    {
        var declared = capabilities is null || capabilities.Count == 0
            ? ProviderCapabilitySets.ChatOnly
            : new HashSet<ProviderCapability>(capabilities);
        _registry[provider.Api] = new Registration(new GuardedProvider(provider), sourceId, declared);
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
    /// Returns the providers whose registration declares <paramref name="capability"/> (issue #2853).
    /// A provider that does not declare it is absent from the result even though it is registered
    /// and still resolvable by <see cref="Get"/> -- capability declaration is orthogonal to api
    /// resolution, not a replacement for it.
    /// </summary>
    /// <param name="capability">The declared capability to filter on.</param>
    /// <returns>The registered providers declaring the capability.</returns>
    public IReadOnlyList<IApiProvider> GetByCapability(ProviderCapability capability)
    {
        return _registry.Values
            .Where(r => r.Capabilities.Contains(capability))
            .Select(r => r.Provider)
            .ToList();
    }

    /// <summary>
    /// Returns the capability set declared by the registration for <paramref name="api"/>, or
    /// <see langword="null"/> when no provider is registered for it.
    /// </summary>
    /// <param name="api">The api key.</param>
    /// <returns>The declared capability set, or null.</returns>
    public IReadOnlySet<ProviderCapability>? GetCapabilities(string api)
    {
        return _registry.TryGetValue(api, out var reg) ? reg.Capabilities : null;
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
