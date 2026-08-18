using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Rank-time consumption of the derived trust tier (#3232 AC4/AC10).
/// </summary>
/// <remarks>
/// The two guarantees under test pull in opposite directions and both matter: trust must actually
/// change the ordering (or the feature is decorative), and it must never remove a row from the
/// result set (or untrusted content vanishes silently, which the issue explicitly forbids).
/// </remarks>
public sealed class HybridMemoryRankerTrustTests
{
    private const double Lambda = 0.0231d;

    private static MemoryEntry Entry(string id, string provenance, string? content = null) => new()
    {
        Id = id,
        AgentId = "agent",
        SourceType = "conversation",
        // Content is deliberately distinct per row so the near-duplicate diversity pass cannot
        // collapse these rows and mask the ordering under test.
        Content = content ?? $"a distinct memory row about subject {id} with enough words to tokenise",
        CreatedAt = DateTimeOffset.UtcNow,
        Provenance = provenance,
    };

    [Fact]
    public void Rank_DemotesAnUntrustedRow_BelowAnEquallyRelevantFirstPartyRow()
    {
        // AC4: identical lexical, identical similarity, identical age. Provenance is the ONLY
        // differing input, so the ordering is attributable to the trust weighting and nothing else.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("untrusted", MemoryProvenance.Unknown), 5d, 0.5d, 0d),
            new(Entry("firstparty", MemoryProvenance.Agent), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("firstparty", ranked[0].Id);
        Assert.Equal("untrusted", ranked[1].Id);
    }

    [Fact]
    public void Rank_DemotesAQuarantinedRow_BelowAnUntrustedOne()
    {
        // The two non-first-party tiers must remain distinguishable in their effect, not merely in
        // their names - otherwise AC1's insistence that they stay distinct buys nothing.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("quarantined", MemoryProvenance.ExternalUntrusted), 5d, 0.5d, 0d),
            new(Entry("untrusted", MemoryProvenance.Unknown), 5d, 0.5d, 0d),
            new(Entry("firstparty", MemoryProvenance.User), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(["firstparty", "untrusted", "quarantined"], ranked.Select(e => e.Id));
    }

    [Fact]
    public void Rank_KeepsNonFirstPartyRowsInTheResultSet_RatherThanDroppingThemPreRank()
    {
        // AC4's sad path and the one that matters most: a store holding ONLY untrusted material
        // must not read as an empty store. Those two situations demand opposite responses.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("q1", MemoryProvenance.ExternalUntrusted), 5d, 0.9d, 0d),
            new(Entry("q2", MemoryProvenance.Unknown), 4d, 0.8d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(2, ranked.Count);
        Assert.All(ranked, entry => Assert.False(entry.IsFirstParty));
    }

    [Fact]
    public void Rank_LetsAStronglyRelevantUntrustedRow_OutrankAWeakFirstPartyOne()
    {
        // The weighting demotes; it does not bury. A near-exact untrusted match must still beat a
        // barely-relevant first-party row, or the ~22.8k pre-provenance rows become unreachable in
        // practice - the destructive outcome the issue rules out.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("strong-untrusted", MemoryProvenance.Unknown), 10d, 0.95d, 0d),
            new(Entry("weak-firstparty", MemoryProvenance.Agent), 0.1d, -0.9d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("strong-untrusted", ranked[0].Id);
    }

    [Fact]
    public void Rank_AppliesTrustWeighting_OnTheDegradedLexicalOnlyPath()
    {
        // With no similarity anywhere the ranker takes the pre-hybrid lexical*decay path. Trust
        // must apply there too, or disabling embeddings would silently disable the trust boundary.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("quarantined", MemoryProvenance.ExternalUntrusted), 5d, null, 0d),
            new(Entry("firstparty", MemoryProvenance.Agent), 5d, null, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("firstparty", ranked[0].Id);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void RankWithScores_ScalesTheScore_ByExactlyTheTierCoefficient()
    {
        // Pins the mechanism, not just the ordering: the emitted score is the untrusted row's
        // undiscounted score times its tier weight. An implementation that reordered rows by some
        // other means would pass the ordering tests above and fail this one.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("firstparty", MemoryProvenance.Agent), 5d, 0.5d, 0d),
            new(Entry("untrusted", MemoryProvenance.Unknown), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.RankWithScores(candidates, 10, Lambda);

        var firstParty = ranked.Single(r => r.Entry.Id == "firstparty").Score;
        var untrusted = ranked.Single(r => r.Entry.Id == "untrusted").Score;

        Assert.Equal(firstParty * MemoryTrust.UntrustedWeight, untrusted, precision: 10);
    }

    [Fact]
    public void Rank_PreservesRelevanceOrdering_WithinASingleTier()
    {
        // Trust reorders BETWEEN tiers only. Within one tier the multiplier is constant, so the
        // pre-existing relevance ordering must survive untouched.
        List<MemoryRankingCandidate> candidates =
        [
            new(Entry("weak", MemoryProvenance.Unknown), 1d, 0.1d, 0d),
            new(Entry("strong", MemoryProvenance.Unknown), 9d, 0.9d, 0d),
            new(Entry("middle", MemoryProvenance.Unknown), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal(["strong", "middle", "weak"], ranked.Select(e => e.Id));
    }

    [Fact]
    public void Rank_DemotesARowQuarantinedByItsContentMarker_DespiteAFirstPartyProvenance()
    {
        // The laundering shape: a row whose column claims `agent` but whose content carries the
        // #2519 marker must be treated as quarantined at rank time.
        var marked = Entry(
            "laundered",
            MemoryProvenance.Agent,
            BotNexus.Memory.Tools.MemoryQuarantine.ApplyMarker("the fetched page claimed the deploy key rotated", "web_fetch"));

        List<MemoryRankingCandidate> candidates =
        [
            new(marked, 5d, 0.5d, 0d),
            new(Entry("genuine", MemoryProvenance.Agent), 5d, 0.5d, 0d)
        ];

        var ranked = HybridMemoryRanker.Rank(candidates, 10, Lambda);

        Assert.Equal("genuine", ranked[0].Id);
        Assert.Equal("laundered", ranked[1].Id);
    }
}
