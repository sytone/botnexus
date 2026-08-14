using BotNexus.Agent.Providers.Core.Embeddings;
using BotNexus.Gateway.Configuration;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Composition root for the memory embedding backend (#2855): turns configuration plus the
/// optional <see cref="IEmbeddingProvider"/> capability into the
/// <see cref="IMemoryEmbeddingService"/> the memory store consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is the ONLY place the provider stack and <c>BotNexus.Memory</c> meet, and it lives on the
/// composition side so that neither project references the other. <c>BotNexus.Memory</c> is
/// unchanged by this feature: it still consumes the provider-neutral
/// <c>Microsoft.Extensions.AI</c> abstraction it was written against.
/// </para>
/// <para>
/// Every failure path returns <see cref="MemoryEmbeddingService.Disabled"/> rather than throwing.
/// Embeddings are an enhancement to retrieval; a mistyped provider key must degrade the gateway to
/// the lexical-only behaviour it has today, not stop it from starting.
/// </para>
/// </remarks>
public static class MemoryEmbeddingComposition
{
    /// <summary>
    /// Builds the embedding service for <paramref name="config"/>, or
    /// <see cref="MemoryEmbeddingService.Disabled"/> when embeddings are absent, disabled,
    /// incompletely configured, or the named provider does not expose the capability.
    /// </summary>
    /// <param name="config">Memory embeddings configuration. <see langword="null"/> means absent.</param>
    /// <param name="registry">Registry of providers that opted into the embeddings capability.</param>
    /// <param name="loggerFactory">Optional; used to explain why embeddings stayed off.</param>
    public static IMemoryEmbeddingService Build(
        MemoryEmbeddingsConfig? config,
        EmbeddingProviderRegistry? registry,
        ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger(typeof(MemoryEmbeddingComposition).FullName!);

        // AC6: absent or disabled is not a warning. It is the documented default.
        if (config is null || !config.Enabled)
            return MemoryEmbeddingService.Disabled;

        if (!config.IsComplete())
        {
            logger?.LogWarning(
                "Memory embeddings are enabled but the configuration is incomplete (provider, model, baseUrl and dimensions are all required); retrieval stays lexical-only.");
            return MemoryEmbeddingService.Disabled;
        }

        var provider = registry?.Get(config.Provider);
        if (provider is null)
        {
            // AC1/AC7: a provider that does not implement IEmbeddingProvider is absent, not an
            // error. Naming the keys that DO expose the capability turns a silent no-op into a
            // one-line diagnosis.
            logger?.LogWarning(
                "Memory embeddings are enabled for provider '{Provider}', which does not expose an embeddings capability. Providers that do: [{Available}]. Retrieval stays lexical-only.",
                config.Provider,
                registry is null ? string.Empty : string.Join(", ", registry.Keys));
            return MemoryEmbeddingService.Disabled;
        }

        var identity = new EmbeddingIdentity(
            config.Model!,
            HostedEmbeddingFingerprint.Derive(config.Provider!, config.BaseUrl, config.Model!, config.Dimensions),
            config.Dimensions);

        logger?.LogInformation(
            "Memory embeddings enabled via provider '{Provider}' with identity '{Identity}'.",
            config.Provider, identity);

        return new MemoryEmbeddingService(
            new EmbeddingProviderGenerator(provider, config.Model!),
            identity,
            loggerFactory?.CreateLogger<MemoryEmbeddingService>());
    }
}
