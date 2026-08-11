using BotNexus.Memory.Models;

namespace BotNexus.Memory.Embeddings;

/// <summary>
/// A single ranking candidate: a memory row plus the signals available for it.
/// </summary>
/// <param name="Entry">The memory row.</param>
/// <param name="LexicalScore">
/// Non-negative lexical relevance (BM25 magnitude, or term-hit count on the LIKE fallback).
/// Zero for a row that was reached only through vector similarity.
/// </param>
/// <param name="Similarity">
/// Cosine similarity in <c>[-1, 1]</c>, or <see langword="null"/> when no comparable vector
/// exists — because embeddings are disabled, the row has none, or its identity differs.
/// </param>
/// <param name="AgeDays">Age of the row in days, used for temporal decay.</param>
public sealed record MemoryRankingCandidate(MemoryEntry Entry, double LexicalScore, double? Similarity, double AgeDays);

/// <summary>
/// A ranked memory row paired with the fused relevance magnitude that placed it there.
/// </summary>
/// <param name="Entry">The memory row.</param>
/// <param name="Score">
/// The fused relevance score produced by <see cref="HybridMemoryRanker"/>. Higher is more relevant.
/// This is the single definition of relevance: callers render and threshold this value rather than
/// deriving a second notion of relevance downstream (#2781).
/// </param>
public sealed record ScoredMemoryEntry(MemoryEntry Entry, double Score);

/// <summary>
/// Fuses lexical relevance and vector similarity into one ordering while preserving the
/// existing temporal-decay behaviour.
/// </summary>
/// <remarks>
/// Two properties matter more than the exact weights:
/// <list type="number">
/// <item><description>
/// When no candidate carries a similarity — embeddings disabled, model unavailable, or every
/// stored vector belongs to a different identity — the ranking collapses to exactly the
/// pre-existing <c>lexical * exp(-lambda * age)</c> formula. Degradation is not a
/// near-equivalent path; it is the original path.
/// </description></item>
/// <item><description>
/// Lexical normalisation is scale-only (divide by the maximum) rather than min-max. An
/// affine shift would silently reorder rows once decay multiplies through, so scale
/// normalisation is what makes the guarantee above hold.
/// </description></item>
/// </list>
/// A row that has no comparable vector inside an otherwise vector-enabled result set is
/// given a neutral similarity prior rather than being scored as dissimilar. Treating "no
/// evidence" as evidence of dissimilarity would bury every not-yet-embedded row the moment
/// embeddings were switched on - including exact lexical matches - which is precisely the
/// regression this feature must not cause.
/// </remarks>
public static class HybridMemoryRanker
{
    /// <summary>Weight applied to the normalised lexical signal.</summary>
    public const double LexicalWeight = 0.6d;

    /// <summary>Weight applied to the normalised similarity signal.</summary>
    public const double SimilarityWeight = 0.4d;

    /// <summary>
    /// Normalised similarity assigned to a row that carries no comparable vector. Sits at the
    /// midpoint of the normalised range so such a row is neither rewarded nor penalised for
    /// the absence of an embedding.
    /// </summary>
    private const double NeutralSimilarityPrior = 0.5d;

    /// <summary>
    /// Ranks candidates and returns the top <paramref name="limit"/> entries, most relevant first.
    /// </summary>
    public static IReadOnlyList<MemoryEntry> Rank(
        IReadOnlyCollection<MemoryRankingCandidate> candidates,
        int limit,
        double lambda)
        => RankWithScores(candidates, limit, lambda).Select(scored => scored.Entry).ToList();

    /// <summary>
    /// Ranks candidates exactly as <see cref="Rank"/> does, but also returns the fused relevance
    /// magnitude that produced each position.
    /// </summary>
    /// <remarks>
    /// This is the ordering function itself, not a parallel scoring pass: <see cref="Rank"/> is
    /// implemented on top of it, so the score a caller renders or thresholds is by construction the
    /// score that decided the order. Recomputing relevance anywhere downstream would let the two
    /// definitions drift (#2781).
    /// </remarks>
    public static IReadOnlyList<ScoredMemoryEntry> RankWithScores(
        IReadOnlyCollection<MemoryRankingCandidate> candidates,
        int limit,
        double lambda)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0 || limit <= 0)
            return [];

        var anySimilarity = candidates.Any(candidate => candidate.Similarity.HasValue);

        // No usable vector anywhere: reproduce the original lexical ranking exactly.
        if (!anySimilarity)
        {
            return candidates
                .Select(candidate => new ScoredMemoryEntry(
                    candidate.Entry,
                    candidate.LexicalScore * Decay(candidate.AgeDays, lambda)))
                .OrderByDescending(scored => scored.Score)
                .Take(limit)
                .ToList();
        }

        var maxLexical = candidates.Max(candidate => candidate.LexicalScore);

        return candidates
            .Select(candidate =>
            {
                var lexicalNormalised = maxLexical > 0d ? candidate.LexicalScore / maxLexical : 0d;
                var similarityNormalised = candidate.Similarity is { } similarity
                    ? (similarity + 1d) / 2d
                    : NeutralSimilarityPrior;

                var fused = (LexicalWeight * lexicalNormalised) + (SimilarityWeight * similarityNormalised);
                return new ScoredMemoryEntry(candidate.Entry, fused * Decay(candidate.AgeDays, lambda));
            })
            .OrderByDescending(scored => scored.Score)
            .Take(limit)
            .ToList();
    }

    private static double Decay(double ageDays, double lambda)
        => Math.Exp(-lambda * Math.Max(0d, ageDays));
}
