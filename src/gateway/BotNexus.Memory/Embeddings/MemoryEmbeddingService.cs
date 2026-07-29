using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Embeddings;

/// <summary>
/// The seam the memory store uses to obtain vectors. Implementations stamp every vector with
/// the <see cref="EmbeddingIdentity"/> of the model that produced it.
/// </summary>
/// <remarks>
/// This sits in front of <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> rather than
/// exposing it directly because the store needs two things the raw abstraction does not give
/// it: the identity stamp, and a contract that never throws. A memory write or a search must
/// not fail because an embedding model is missing, slow, or broken — it must quietly become a
/// lexical-only operation. Hence <see cref="TryGenerateAsync"/> returns <see langword="null"/>
/// rather than propagating provider faults.
/// </remarks>
public interface IMemoryEmbeddingService
{
    /// <summary>Identity of the currently active model, or <see langword="null"/> when unavailable.</summary>
    EmbeddingIdentity? ActiveIdentity { get; }

    /// <summary>
    /// Produces a vector for <paramref name="text"/>, or <see langword="null"/> when no model is
    /// available or generation failed. Never throws for provider faults.
    /// </summary>
    Task<(EmbeddingIdentity Identity, float[] Vector)?> TryGenerateAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IMemoryEmbeddingService"/> over a provider-neutral
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// </summary>
/// <remarks>
/// Keeping the concrete provider (local ONNX, hosted service, or a test stub) behind
/// <c>Microsoft.Extensions.AI</c> means the ONNX Runtime native dependency is an
/// implementation detail that can be added — or omitted on a platform where it will not
/// load — without touching retrieval. When the generator is absent this type is still
/// constructible and simply reports no identity, which is the supported degraded mode.
/// </remarks>
public sealed class MemoryEmbeddingService : IMemoryEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _generator;
    private readonly EmbeddingIdentity? _identity;
    private readonly ILogger _logger;
    private int _failureLogged;

    /// <param name="generator">
    /// The vector source. <see langword="null"/> means embeddings are not configured and the
    /// service degrades to reporting no identity.
    /// </param>
    /// <param name="identity">
    /// Identity to stamp on produced vectors. Must be supplied whenever
    /// <paramref name="generator"/> is; without it a vector could not be safely compared later.
    /// </param>
    public MemoryEmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        EmbeddingIdentity? identity,
        ILogger<MemoryEmbeddingService>? logger = null)
    {
        if (generator is not null && identity is null)
            throw new ArgumentNullException(nameof(identity), "An embedding generator must be accompanied by the identity of the model it represents.");

        _generator = generator;
        _identity = identity;
        _logger = logger ?? NullLogger<MemoryEmbeddingService>.Instance;
    }

    /// <summary>A service with no model configured: always degrades to lexical-only retrieval.</summary>
    public static IMemoryEmbeddingService Disabled { get; } = new MemoryEmbeddingService(null, null);

    public EmbeddingIdentity? ActiveIdentity => _generator is null ? null : _identity;

    public async Task<(EmbeddingIdentity Identity, float[] Vector)?> TryGenerateAsync(string text, CancellationToken ct = default)
    {
        if (_generator is null || _identity is null || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var result = await _generator.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
            var embedding = result.FirstOrDefault();
            if (embedding is null)
                return null;

            var vector = embedding.Vector.ToArray();

            // A model that emits the wrong width is misconfigured, not merely unlucky. Storing
            // the vector under the declared identity would corrupt every later comparison, so
            // the vector is discarded and the row falls back to lexical-only.
            if (vector.Length != _identity.Dimensions)
            {
                _logger.LogWarning(
                    "Embedding model '{Identity}' returned {Actual} dimensions but {Expected} were declared; discarding the vector.",
                    _identity, vector.Length, _identity.Dimensions);
                return null;
            }

            return (_identity, vector);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log once per service instance: a broken model would otherwise emit one error per
            // memory write, drowning the log on exactly the path that is meant to degrade quietly.
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                _logger.LogWarning(ex, "Embedding generation failed for model '{Identity}'; falling back to lexical-only retrieval.", _identity);

            return null;
        }
    }
}
