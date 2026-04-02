using BotNexus.Core.Abstractions;

namespace BotNexus.Providers.Base;

/// <summary>Registry of available LLM providers keyed by name.</summary>
public sealed class ProviderRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ILlmProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IEnumerable<ILlmProvider>? providers = null)
    {
        if (providers is null) return;
        foreach (var p in providers)
            Register(GetProviderKey(p), p);
    }

    /// <summary>Registers a provider under the given name.</summary>
    public void Register(string name, ILlmProvider provider)
    {
        lock (_sync)
        {
            _providers[name] = provider;
        }
    }

    /// <summary>Removes a provider by name.</summary>
    public bool Remove(string name)
    {
        lock (_sync)
        {
            return _providers.Remove(name);
        }
    }

    /// <summary>Replaces all provider registrations.</summary>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, ILlmProvider>> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        lock (_sync)
        {
            _providers.Clear();
            foreach (var (name, provider) in providers)
                _providers[name] = provider;
        }
    }

    /// <summary>Gets a provider by name, or null if not found.</summary>
    public ILlmProvider? Get(string name)
    {
        lock (_sync)
        {
            return _providers.GetValueOrDefault(name);
        }
    }

    /// <summary>Gets a provider by name, throwing if not found.</summary>
    public ILlmProvider GetRequired(string name)
    {
        lock (_sync)
        {
            return _providers.TryGetValue(name, out var p) ? p
                : throw new InvalidOperationException($"Provider '{name}' is not registered.");
        }
    }

    /// <summary>Gets the first registered provider, or null if none are registered.</summary>
    public ILlmProvider? GetDefault()
    {
        lock (_sync)
        {
            return _providers.Values.FirstOrDefault();
        }
    }

    /// <summary>Returns all registered provider names.</summary>
    public IReadOnlyList<string> GetProviderNames()
    {
        lock (_sync)
        {
            return [.. _providers.Keys];
        }
    }

    private static string GetProviderKey(ILlmProvider provider)
    {
        var ns = provider.GetType().Namespace;
        if (!string.IsNullOrWhiteSpace(ns))
        {
            var marker = ".Providers.";
            var start = ns.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var segment = ns[(start + marker.Length)..].Split('.')[0];
                if (!string.IsNullOrWhiteSpace(segment))
                    return segment.ToLowerInvariant();
            }
        }

        return provider.GetType().Name.Replace("Provider", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
    }
}
