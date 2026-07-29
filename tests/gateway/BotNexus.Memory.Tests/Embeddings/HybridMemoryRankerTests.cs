using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Ranking fusion tests. The critical guarantee is that with no similarity signal the
/// hybrid ranker reproduces the pre-existing lexical-times-decay ordering exactly.
/// </summary>
public sealed class HybridMemoryRankerTests
{
    private static MemoryEntry Entry(string id) => new()
    {
        Id = id,
        AgentId = "agent",
        SourceType = "conversation",
        Content = $"content-{id}",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private const double Lambda = 0.0231d; // ~30 day half-life, matching the store default.

    [Fact]
    public void Rank_WithNoSimilarities_ReproducesLexicalTimesDecayOrdering()
    {
        // "b" has the stronger raw lexical score but is far older; decay must still demote it,
        // exactly as the BM25-only path did before hybrid retrieval existed.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("a"), LexicalScore: 5d, Similarity: null, AgeDays: 0d),
            new(Entry("b"), LexicalScore: 8d, Similarity: null, AgeDays: 365d),
            new(Entry("c"), LexicalScore: 1d, Similarity: null, AgeDays: 0d)
        ];

        var expected = candidates
            .OrderByDescending(c => c.LexicalScore * Math.Exp(-Lambda * c.AgeDays))
            .Select(c => c.Entry.Id)
            .ToList();

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(expected, ranked.Select(e => e.Id));
        Assert.Equal(["a", "b", "c"], ranked.Select(e => e.Id).OrderBy(id => id));
    }

    [Fact]
    public void Rank_WithNoSimilarities_PreservesDecayOrderingForEqualLexicalScores()
    {
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("old"), LexicalScore: 4d, Similarity: null, AgeDays: 200d),
            new(Entry("new"), LexicalScore: 4d, Similarity: null, AgeDays: 1d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("new", ranked[0].Id);
    }

    [Fact]
    public void Rank_PromotesSemanticMatch_WhenLexicalEvidenceIsComparable()
    {
        // Both rows share surface terms to a similar degree, so lexical ranking alone cannot
        // separate them; the vector signal is what breaks the tie correctly.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("lexical"), LexicalScore: 3d, Similarity: -0.4d, AgeDays: 0d),
            new(Entry("semantic"), LexicalScore: 2d, Similarity: 0.97d, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("semantic", ranked[0].Id);
    }

    [Fact]
    public void Rank_DoesNotLetSimilarityOverrideAnOverwhelmingLexicalLead()
    {
        // The deliberate limit of the 0.6/0.4 weighting: a dominant exact-term match is not
        // displaced by similarity alone. Recall of true paraphrases is achieved by the vector
        // scan surfacing rows BM25 never returned (lexical score 0), not by outranking strong
        // lexical hits - which is what keeps exact matches competitive.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("lexical"), LexicalScore: 10d, Similarity: -0.4d, AgeDays: 0d),
            new(Entry("semantic"), LexicalScore: 1d, Similarity: 0.97d, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("lexical", ranked[0].Id);
    }

    [Fact]
    public void Rank_SurfacesVectorOnlyMatch_AboveWeakLexicalNoise()
    {
        // The row BM25 never returned (lexical score 0) is exactly what hybrid retrieval adds.
        // It is ranked against real lexical competition, not against the top lexical hit -
        // scale normalisation means the best lexical row always normalises to 1.0.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("strong"), LexicalScore: 10d, Similarity: 0.2d, AgeDays: 0d),
            new(Entry("noise"), LexicalScore: 1d, Similarity: -0.2d, AgeDays: 0d),
            new(Entry("paraphrase"), LexicalScore: 0d, Similarity: 0.95d, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("paraphrase", ranked[1].Id);
        Assert.Equal("noise", ranked[2].Id);
    }

    [Fact]
    public void Rank_GivesNeutralPrior_ToRowsWithNoComparableVector()
    {
        // Identical lexical strength; one row predates the embedding rollout. It must not be
        // scored as dissimilar merely for having no vector.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("embedded-poorly"), LexicalScore: 5d, Similarity: -0.9d, AgeDays: 0d),
            new(Entry("not-embedded"), LexicalScore: 5d, Similarity: null, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("not-embedded", ranked[0].Id);
    }

    [Fact]
    public void Rank_KeepsExactLexicalMatchCompetitive_WhenItHasNoVector()
    {
        // A strong exact match with no stored vector must not be buried by a mediocre
        // semantic match simply for lacking an embedding.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("exact"), LexicalScore: 10d, Similarity: null, AgeDays: 0d),
            new(Entry("fuzzy"), LexicalScore: 1d, Similarity: 0.35d, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("exact", ranked[0].Id);
    }

    [Fact]
    public void Rank_AppliesTemporalDecay_InHybridMode()
    {
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("stale"), LexicalScore: 5d, Similarity: 0.9d, AgeDays: 720d),
            new(Entry("fresh"), LexicalScore: 5d, Similarity: 0.9d, AgeDays: 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("fresh", ranked[0].Id);
    }

    [Fact]
    public void Rank_RespectsLimit()
    {
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("a"), 3d, 0.9d, 0d),
            new(Entry("b"), 2d, 0.8d, 0d),
            new(Entry("c"), 1d, 0.7d, 0d)
        ];

        Assert.Equal(2, HybridMemoryRanker.Rank(candidates, 2, Lambda).Count);
    }

    [Fact]
    public void Rank_ReturnsEmpty_ForEmptyOrNonPositiveLimit()
    {
        Assert.Empty(HybridMemoryRanker.Rank([], 10, Lambda));
        Assert.Empty(HybridMemoryRanker.Rank([new(Entry("a"), 1d, 0.5d, 0d)], 0, Lambda));
    }

    [Fact]
    public void Rank_HandlesAllZeroLexicalScores_WithoutDivideByZero()
    {
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("a"), 0d, 0.9d, 0d),
            new(Entry("b"), 0d, 0.1d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("a", ranked[0].Id);
    }
}
