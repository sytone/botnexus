using Microsoft.Extensions.AI;

namespace BotNexus.Agent.Providers.Core.Embeddings;

/// <summary>
/// Adapts an <see cref="IEmbeddingProvider"/> to the <c>Microsoft.Extensions.AI</c>
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that <c>BotNexus.Memory</c> already
/// consumes (#2855).
/// </summary>
/// <remarks>
/// <para>
/// This type exists so that <c>BotNexus.Memory</c> needs no change at all. The memory seam was
/// written against the provider-neutral <c>Microsoft.Extensions.AI</c> abstraction precisely so
/// the concrete vector source could be supplied from outside; the adapter is the outside. The
/// dependency direction is one-way - the provider stack references
/// <c>Microsoft.Extensions.AI.Abstractions</c>, exactly as the memory project does, and neither
/// references the other.
/// </para>
/// <para>
/// It is deliberately thin: no retry, no caching, no batching. Everything the memory path needs on
/// the failure side already lives in <c>MemoryEmbeddingService</c>, which catches provider faults
/// and degrades to lexical-only retrieval. Duplicating that policy here would give a memory write
/// two different failure behaviours depending on which layer swallowed the error first.
/// </para>
/// </remarks>
public sealed class EmbeddingProviderGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingProvider _provider;
    private readonly string _modelId;

    /// <param name="provider">The capability supplying vectors.</param>
    /// <param name="modelId">Model to request from <paramref name="provider"/>.</param>
    public EmbeddingProviderGenerator(IEmbeddingProvider provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _provider = provider;
        _modelId = modelId;
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var results = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var vector = await _provider.EmbedAsync(_modelId, value, cancellationToken).ConfigureAwait(false);

            // A null vector means the endpoint produced no embedding for this input. Emitting a
            // zero vector instead would be silently wrong: it has the right WIDTH, so it would
            // sail past the seam's dimension check and be stored as if it were a real embedding,
            // and a zero vector compares equally badly against everything. Skipping the entry lets
            // MemoryEmbeddingService see an empty result and fall back to lexical-only.
            if (vector is null)
                continue;

            results.Add(new Embedding<float>(vector));
        }

        return results;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        if (serviceType.IsInstanceOfType(this))
            return this;

        return serviceType.IsInstanceOfType(_provider) ? _provider : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The provider and its HttpClient are owned by composition, not by this adapter.
    }
}
