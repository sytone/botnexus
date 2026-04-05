using System.Collections.Concurrent;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Streaming;

namespace BotNexus.Providers.Core.Registry;

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

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            ValidateModelApi(model, Api);
            return inner.Stream(model, context, options);
        }

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

    public void Register(IApiProvider provider, string? sourceId = null)
    {
        _registry[provider.Api] = new Registration(new GuardedProvider(provider), sourceId);
    }

    public IApiProvider? Get(string api)
    {
        return _registry.TryGetValue(api, out var reg) ? reg.Provider : null;
    }

    public IReadOnlyList<IApiProvider> GetAll()
    {
        return _registry.Values.Select(r => r.Provider).ToList();
    }

    public void Unregister(string sourceId)
    {
        var toRemove = _registry
            .Where(kvp => kvp.Value.SourceId == sourceId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var api in toRemove)
            _registry.TryRemove(api, out _);
    }

    public void Clear()
    {
        _registry.Clear();
    }
}
