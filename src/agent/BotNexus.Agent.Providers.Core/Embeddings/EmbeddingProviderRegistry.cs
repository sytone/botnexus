namespace BotNexus.Agent.Providers.Core.Embeddings;

/// <summary>
/// Resolves the OPTIONAL <see cref="IEmbeddingProvider"/> capability by provider key (#2855).
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this type is that a miss is a normal answer. Every lookup returns
/// <see langword="null"/> for an unknown key, and <see cref="TryRegister"/> accepts an arbitrary
/// object and quietly declines the ones that do not implement the interface. Composition can
/// therefore hand it the entire provider set without first knowing which members embed - which
/// is acceptance criterion 7: a provider that does not implement <see cref="IEmbeddingProvider"/>
/// keeps working and is simply not present here.
/// </para>
/// <para>
/// Registration is last-wins on the key, matching <c>ApiProviderRegistry</c>: composition may
/// legitimately replace a built-in capability with a configured one.
/// </para>
/// </remarks>
public sealed class EmbeddingProviderRegistry
{
    private readonly Dictionary<string, IEmbeddingProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    /// <summary>Registers a capability under its own <see cref="IEmbeddingProvider.ProviderKey"/>.</summary>
    public void Register(IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderKey);

        lock (_gate)
            _providers[provider.ProviderKey] = provider;
    }

    /// <summary>
    /// Registers <paramref name="candidate"/> only if it implements <see cref="IEmbeddingProvider"/>.
    /// Returns <see langword="false"/> - never throws - when it does not.
    /// </summary>
    public bool TryRegister(object? candidate)
    {
        if (candidate is not IEmbeddingProvider provider || string.IsNullOrWhiteSpace(provider.ProviderKey))
            return false;

        Register(provider);
        return true;
    }

    /// <summary>The capability registered under <paramref name="providerKey"/>, or <see langword="null"/>.</summary>
    public IEmbeddingProvider? Get(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return null;

        lock (_gate)
            return _providers.TryGetValue(providerKey, out var provider) ? provider : null;
    }

    /// <summary>Provider keys that currently expose the embeddings capability.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
                return [.. _providers.Keys];
        }
    }
}
