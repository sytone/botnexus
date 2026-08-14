using BotNexus.Agent.Providers.Core.Embeddings;
using BotNexus.Gateway.Configuration;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Providers;

/// <summary>
/// Composition root for the memory embedding backend (#2855, #2790): turns configuration plus the
/// optional <see cref="IEmbeddingProvider"/> capability into the
/// <see cref="IMemoryEmbeddingService"/> the memory store consumes.
/// </summary>
/// <remarks>
/// <para>
/// #2790 turned the binary <c>enabled</c> toggle into an explicit backend LADDER. Selection is
/// resolved here and nowhere else, so "which backend am I on?" has exactly one answer and one
/// place to read it.
/// </para>
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
    /// Builds the embedding service for <paramref name="config"/> by resolving the selected
    /// backend (#2790), or <see cref="MemoryEmbeddingService.Disabled"/> when the backend is
    /// <c>none</c>, unrecognised, unsatisfiable in this build, incompletely configured, or the
    /// named provider does not expose the capability.
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

        // AC6: absent is not a warning. It is the documented default.
        if (config is null)
            return MemoryEmbeddingService.Disabled;

        var backend = config.ResolveBackend(out var unrecognized);

        if (unrecognized is not null)
        {
            // A typo must be named, not swallowed. Silently defaulting an unrecognised token would
            // leave an operator staring at lexical-only results with a config file that looks right.
            logger?.LogWarning(
                "Memory embedding backend '{Backend}' is not recognised; valid values are 'none', 'local' and 'provider'. Retrieval stays lexical-only.",
                unrecognized);
            return MemoryEmbeddingService.Disabled;
        }

        switch (backend)
        {
            case MemoryEmbeddingBackend.None:
                return MemoryEmbeddingService.Disabled;

            case MemoryEmbeddingBackend.Local:
                // #2790 ships the SELECTION seam, deliberately not the runtime. The ONNX Runtime
                // native binary is not vendored, so operators on 'none' or 'provider' never carry
                // it. Selecting 'local' is therefore a valid choice that is not yet satisfiable,
                // and the contract for an unsatisfiable backend is the same as for a broken one:
                // warn and degrade, never fail startup.
                logger?.LogWarning(
                    "Memory embedding backend 'local' is selected but no local inference runtime is present in this build; retrieval stays lexical-only. Use backend 'provider' to embed via a configured provider.");
                return MemoryEmbeddingService.Disabled;

            case MemoryEmbeddingBackend.Provider:
                return BuildProviderBackend(config, registry, loggerFactory, logger);

            default:
                return MemoryEmbeddingService.Disabled;
        }
    }

    private static IMemoryEmbeddingService BuildProviderBackend(
        MemoryEmbeddingsConfig config,
        EmbeddingProviderRegistry? registry,
        ILoggerFactory? loggerFactory,
        ILogger? logger)
    {
        if (!config.IsComplete())
        {
            logger?.LogWarning(
                "Memory embedding backend 'provider' is selected but the configuration is incomplete (provider, model, baseUrl and dimensions are all required); retrieval stays lexical-only.");
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
            // #2790 AC5: the BACKEND is part of the fingerprint material, not just the model name.
            // A local and a hosted build of the same model produce incomparable vectors, so their
            // identities must differ even when every other observable component agrees.
            HostedEmbeddingFingerprint.Derive(
                MemoryEmbeddingBackend.Provider.ToString() + ":" + config.Provider!,
                config.BaseUrl,
                config.Model!,
                config.Dimensions),
            config.Dimensions);

        logger?.LogInformation(
            "Memory embeddings enabled via backend 'provider' key '{Provider}' with identity '{Identity}'.",
            config.Provider, identity);

        return new MemoryEmbeddingService(
            new EmbeddingProviderGenerator(provider, config.Model!),
            identity,
            loggerFactory?.CreateLogger<MemoryEmbeddingService>());
    }
}
