using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.AI;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// A deterministic in-memory <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> so hybrid
/// retrieval can be tested without an ONNX runtime or a downloaded model.
/// </summary>
/// <remarks>
/// Vectors are looked up from an explicit map, which lets a test state the semantic
/// relationships it is asserting on instead of depending on a real model's behaviour.
/// </remarks>
internal sealed class StubEmbeddingGenerator(
    IReadOnlyDictionary<string, float[]> vectors,
    int dimensions,
    Exception? throwOnGenerate = null) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IReadOnlyDictionary<string, float[]> _vectors = vectors;
    private readonly int _dimensions = dimensions;
    private readonly Exception? _throwOnGenerate = throwOnGenerate;

    public int GenerateCallCount { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GenerateCallCount++;

        if (_throwOnGenerate is not null)
            throw _throwOnGenerate;

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var vector = _vectors.TryGetValue(value, out var known)
                ? known
                : new float[_dimensions];
            embeddings.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
