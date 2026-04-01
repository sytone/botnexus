using BotNexus.Core.Abstractions;

namespace BotNexus.Providers.Base;

/// <summary>Registry of available LLM providers keyed by name.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IEnumerable<ILlmProvider>? providers = null)
    {
        if (providers is null) return;
        foreach (var p in providers)
            Register(p.DefaultModel, p);
    }

    /// <summary>Registers a provider under the given name.</summary>
    public void Register(string name, ILlmProvider provider)
        => _providers[name] = provider;

    /// <summary>Gets a provider by name, or null if not found.</summary>
    public ILlmProvider? Get(string name)
        => _providers.GetValueOrDefault(name);

    /// <summary>Gets a provider by name, throwing if not found.</summary>
    public ILlmProvider GetRequired(string name)
        => _providers.TryGetValue(name, out var p) ? p
            : throw new InvalidOperationException($"Provider '{name}' is not registered.");

    /// <summary>Returns all registered provider names.</summary>
    public IReadOnlyList<string> GetProviderNames() => [.. _providers.Keys];
}
