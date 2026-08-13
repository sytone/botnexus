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

    private static MemoryEntry Entry(string id, string content, DateTimeOffset createdAt) => new()
    {
        Id = id,
        AgentId = "agent",
        SourceType = "conversation",
        Content = content,
        CreatedAt = createdAt
    };

    /// <summary>
    /// The observed defect shape: one recurring cron prompt indexed once per firing, so the rows
    /// differ only by the embedded date. Long enough to be realistic - a two-token snippet cannot
    /// distinguish "near-identical" from "unrelated" under any token-overlap measure.
    /// </summary>
    private static string CronPrompt(string date)
        => "Meeting Transcript Processing and Proactive Action. Review the meeting transcripts "
           + "captured today, extract decisions and commitments, and file any action items against "
           + $"the owning work item. Run date {date}.";

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
    public void Rank_CollapsesNearDuplicateContent_ToASingleRepresentative()
    {
        // AC1: three rows differing only by an embedded date must not occupy three slots.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("cron-1", CronPrompt("2026-07-16"), DateTimeOffset.UtcNow.AddDays(-3)), 9d, 0.9d, 0d),
            new(Entry("cron-2", CronPrompt("2026-07-17"), DateTimeOffset.UtcNow.AddDays(-2)), 9d, 0.9d, 0d),
            new(Entry("cron-3", CronPrompt("2026-07-18"), DateTimeOffset.UtcNow.AddDays(-1)), 9d, 0.9d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Single(ranked);
    }

    [Fact]
    public void Rank_KeepsTheMostRecent_RepresentativeOfANearDuplicateGroup()
    {
        // AC2: the survivor is chosen by recency, not by arrival order or score order. The oldest
        // row carries the strongest lexical score precisely so that "most recent" is the only
        // rule that can produce the asserted CreatedAt.
        var newest = DateTimeOffset.UtcNow.AddDays(-1);

        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("cron-old", CronPrompt("2026-07-16"), DateTimeOffset.UtcNow.AddDays(-9)), 12d, 0.9d, 0d),
            new(Entry("cron-new", CronPrompt("2026-07-18"), newest), 4d, 0.9d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Single(ranked);
        Assert.Equal("cron-new", ranked[0].Id);
        Assert.Equal(newest, ranked[0].CreatedAt);
    }

    [Fact]
    public void Rank_DoesNotSuppressDissimilarContent_WithNearEqualScores()
    {
        // AC3 - the sad path, and the more dangerous failure mode: an over-aggressive diversity
        // pass that eats real results is worse than the crowding it fixes. Near-equal fused
        // scores must never be mistaken for near-identical content.
        List<MemoryRankingCandidate> candidates =
        [
            new(
                Entry(
                    "azure",
                    "The deployment gate failed because the storage account firewall rejected the runner subnet.",
                    DateTimeOffset.UtcNow),
                5d,
                0.90d,
                0d),
            new(
                Entry(
                    "recipe",
                    "Sourdough starter needs feeding twice daily once the kitchen is warmer than twenty degrees.",
                    DateTimeOffset.UtcNow),
                5d,
                0.90d,
                0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(["azure", "recipe"], ranked.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Rank_FreesSuppressedSlots_RatherThanShrinkingTheResultSet()
    {
        // AC4: 3 near-duplicates + 4 distinct rows at topK=5 must still return 5 results, and the
        // freed slots must go to the distinct rows - not simply return 5 minus the suppressions.
        var now = DateTimeOffset.UtcNow;

        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("cron-1", CronPrompt("2026-07-16"), now.AddDays(-3)), 10d, 0.99d, 0d),
            new(Entry("cron-2", CronPrompt("2026-07-17"), now.AddDays(-2)), 10d, 0.99d, 0d),
            new(Entry("cron-3", CronPrompt("2026-07-18"), now.AddDays(-1)), 10d, 0.99d, 0d),
            new(Entry("d1", "Kusto cluster ingestion latency spiked during the regional failover drill.", now), 8d, 0.8d, 0d),
            new(Entry("d2", "Sourdough starter needs feeding twice daily in a warm kitchen.", now), 7d, 0.7d, 0d),
            new(Entry("d3", "Passport renewal appointment moved to the downtown office next Thursday.", now), 6d, 0.6d, 0d),
            new(Entry("d4", "Bicycle rear derailleur cable frayed and should be replaced before the ride.", now), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 5, Lambda);

        Assert.Equal(5, ranked.Count);
        Assert.Equal(["d1", "d2", "d3", "d4"], ranked.Select(e => e.Id).Where(id => id.StartsWith('d')).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Single(ranked, e => e.Id.StartsWith("cron", StringComparison.Ordinal));
    }

    [Fact]
    public void Rank_CollapsesNearDuplicates_OnTheDegradedLexicalOnlyPath()
    {
        // The defect is not vector-specific: with embeddings unavailable the same recurring rows
        // crowd the same slots, so the diversity pass applies to the degraded path too. Clause 5
        // is about the SCORING formula being unchanged, which it is.
        var now = DateTimeOffset.UtcNow;

        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("cron-1", CronPrompt("2026-07-16"), now.AddDays(-2)), 9d, null, 0d),
            new(Entry("cron-2", CronPrompt("2026-07-17"), now.AddDays(-1)), 9d, null, 0d),
            new(Entry("other", "Bicycle rear derailleur cable frayed before the ride.", now), 3d, null, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("cron-2", ranked[0].Id);
        Assert.Equal("other", ranked[1].Id);
    }

    [Fact]
    public void Rank_TreatsShortContentAsDistinct_UnlessItIsAnExactMatch()
    {
        // Token-overlap is unreliable on very short strings: "deploy prod" and "deploy staging"
        // share half their tokens without being duplicates. Below the minimum token count only an
        // exact content match may collapse, which is what keeps terse notes addressable.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("short-a", "deploy prod", DateTimeOffset.UtcNow), 5d, 0.9d, 0d),
            new(Entry("short-b", "deploy staging", DateTimeOffset.UtcNow), 5d, 0.9d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void RankWithScores_RetainsTheStrongestScore_OfACollapsedGroup()
    {
        // Collapsing must not demote the group: the representative inherits the best score in its
        // group, so suppressing a duplicate can never cost the group its ranking position.
        var now = DateTimeOffset.UtcNow;

        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("cron-strong-old", CronPrompt("2026-07-16"), now.AddDays(-5)), 10d, 0.99d, 0d),
            new(Entry("cron-weak-new", CronPrompt("2026-07-18"), now), 1d, 0.10d, 0d),
            new(Entry("rival", "Kusto ingestion latency spiked during the regional failover drill.", now), 9d, 0.95d, 0d)
        ];

        var ranked = HybridMemoryRanker.RankWithScores(candidates, 10, Lambda);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("cron-weak-new", ranked[0].Entry.Id);
        Assert.True(ranked[0].Score > ranked[1].Score);
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
