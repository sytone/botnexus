using BotNexus.Memory.Embeddings;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Guards the single most important invariant of the feature: a vector produced by one model
/// identity is never compared against a vector produced by another.
/// </summary>
public sealed class VectorSimilarityTests
{
    private static readonly EmbeddingIdentity ModelA = new("model-a", "fp-1", 3);

    [Fact]
    public void TryCosine_ReturnsOne_ForIdenticalVectors()
    {
        float[] vector = [1f, 2f, 3f];

        var similarity = VectorSimilarity.TryCosine(ModelA, vector, ModelA, vector);

        Assert.NotNull(similarity);
        Assert.Equal(1d, similarity!.Value, 6);
    }

    [Fact]
    public void TryCosine_ReturnsMinusOne_ForOpposedVectors()
    {
        var similarity = VectorSimilarity.TryCosine(ModelA, [1f, 0f, 0f], ModelA, [-1f, 0f, 0f]);

        Assert.NotNull(similarity);
        Assert.Equal(-1d, similarity!.Value, 6);
    }

    [Fact]
    public void TryCosine_ReturnsZero_ForOrthogonalVectors()
    {
        var similarity = VectorSimilarity.TryCosine(ModelA, [1f, 0f, 0f], ModelA, [0f, 1f, 0f]);

        Assert.NotNull(similarity);
        Assert.Equal(0d, similarity!.Value, 6);
    }

    [Fact]
    public void TryCosine_ReturnsNull_WhenModelIdDiffers()
    {
        var other = new EmbeddingIdentity("model-b", "fp-1", 3);
        float[] vector = [1f, 2f, 3f];

        Assert.Null(VectorSimilarity.TryCosine(ModelA, vector, other, vector));
    }

    [Fact]
    public void TryCosine_ReturnsNull_WhenFingerprintDiffers()
    {
        // Same model name, different build: the vectors are numerically the same width but
        // semantically incomparable, which is precisely the trap this guards.
        var requantised = new EmbeddingIdentity("model-a", "fp-2", 3);
        float[] vector = [1f, 2f, 3f];

        Assert.Null(VectorSimilarity.TryCosine(ModelA, vector, requantised, vector));
    }

    [Fact]
    public void TryCosine_ReturnsNull_WhenDimensionsDiffer()
    {
        var wider = new EmbeddingIdentity("model-a", "fp-1", 4);

        Assert.Null(VectorSimilarity.TryCosine(ModelA, [1f, 2f, 3f], wider, [1f, 2f, 3f, 4f]));
    }

    [Fact]
    public void TryCosine_ReturnsNull_WhenEitherIdentityIsMissing()
    {
        float[] vector = [1f, 2f, 3f];

        Assert.Null(VectorSimilarity.TryCosine(null, vector, ModelA, vector));
        Assert.Null(VectorSimilarity.TryCosine(ModelA, vector, null, vector));
    }

    [Fact]
    public void TryCosine_ReturnsNull_ForZeroMagnitudeVector()
    {
        Assert.Null(VectorSimilarity.TryCosine(ModelA, [0f, 0f, 0f], ModelA, [1f, 2f, 3f]));
    }

    [Fact]
    public void Matches_RequiresAllThreeIdentityComponents()
    {
        Assert.True(ModelA.Matches(new EmbeddingIdentity("model-a", "fp-1", 3)));
        Assert.False(ModelA.Matches(new EmbeddingIdentity("model-a", "fp-1", 5)));
        Assert.False(ModelA.Matches(new EmbeddingIdentity("model-a", "fp-9", 3)));
        Assert.False(ModelA.Matches(new EmbeddingIdentity("model-z", "fp-1", 3)));
        Assert.False(ModelA.Matches(null));
    }
}
