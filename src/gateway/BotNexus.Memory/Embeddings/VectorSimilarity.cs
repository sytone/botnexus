namespace BotNexus.Memory.Embeddings;

/// <summary>
/// Cosine similarity over embedding vectors, with identity as a hard precondition.
/// </summary>
public static class VectorSimilarity
{
    /// <summary>
    /// Returns the cosine similarity of two vectors, or <see langword="null"/> when the two
    /// identities are not comparable or either vector is degenerate (zero magnitude).
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null"/> rather than <c>0</c> for a non-comparable pair is
    /// deliberate: <c>0</c> is a legitimate similarity value (orthogonal vectors) and would be
    /// fed into ranking as evidence, whereas <see langword="null"/> means "no evidence", which
    /// is what the ranker needs in order to fall back to the lexical signal for that row.
    /// </remarks>
    public static double? TryCosine(
        EmbeddingIdentity? left,
        ReadOnlySpan<float> leftVector,
        EmbeddingIdentity? right,
        ReadOnlySpan<float> rightVector)
    {
        if (left is null || right is null || !left.Matches(right))
            return null;

        // Matches() already enforces equal declared dimensions; this guards a vector whose
        // materialised length disagrees with its own declared identity.
        if (leftVector.Length != rightVector.Length || leftVector.Length == 0)
            return null;

        double dot = 0d, leftMagnitude = 0d, rightMagnitude = 0d;
        for (var i = 0; i < leftVector.Length; i++)
        {
            double l = leftVector[i], r = rightVector[i];
            dot += l * r;
            leftMagnitude += l * l;
            rightMagnitude += r * r;
        }

        if (leftMagnitude <= 0d || rightMagnitude <= 0d)
            return null;

        var cosine = dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        return Math.Clamp(cosine, -1d, 1d);
    }
}
