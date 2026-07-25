namespace BotNexus.Memory.Embeddings;

/// <summary>
/// Identity of the model that produced a vector: which model, which exact build of it, and
/// how many dimensions it emits.
/// </summary>
/// <remarks>
/// Vectors from different models — or different builds of the same model — live in
/// unrelated coordinate spaces, so a cosine similarity computed across identities is
/// numerically well-formed but semantically meaningless. Every stored vector therefore
/// carries its identity, and <see cref="Matches"/> gates every comparison. This is why the
/// fingerprint is part of identity and not just metadata: a re-quantised or re-exported
/// model keeps its name but produces incomparable vectors.
/// </remarks>
/// <param name="ModelId">Registry name of the model, e.g. <c>nomic-embed-text-v2</c>.</param>
/// <param name="ModelFingerprint">
/// Version or content fingerprint of the exact model artefact (typically the pinned
/// SHA-256 of the model file). Distinguishes two builds published under one name.
/// </param>
/// <param name="Dimensions">Length of the vectors this identity produces.</param>
public sealed record EmbeddingIdentity(string ModelId, string ModelFingerprint, int Dimensions)
{
    /// <summary>
    /// Whether two vectors carrying these identities may be compared. Requires the model,
    /// the fingerprint and the dimension count to all agree.
    /// </summary>
    public bool Matches(EmbeddingIdentity? other)
        => other is not null
           && Dimensions == other.Dimensions
           && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
           && string.Equals(ModelFingerprint, other.ModelFingerprint, StringComparison.Ordinal);

    /// <summary>Stable display form used in diagnostics and logs.</summary>
    public override string ToString() => $"{ModelId}@{ModelFingerprint}/{Dimensions}d";
}
